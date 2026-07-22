[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Workspace,
  [Parameter(Mandatory)][string]$CliPath
)

$ErrorActionPreference = 'Stop'
$Workspace = [IO.Path]::GetFullPath($Workspace)
$CliPath = [IO.Path]::GetFullPath($CliPath)
if (-not (Test-Path -LiteralPath $CliPath)) { throw "CLI executable was not found: $CliPath" }

$runId = 'sftp-protocol-' + [Guid]::NewGuid().ToString('N')
$root = Join-Path $Workspace ('.fengsync-test\protocol\' + $runId)
$appData = Join-Path $root 'appdata'; $share = Join-Path $root 'share'; $readOnlyShare = Join-Path $root 'readonly'; $upload = Join-Path $root 'upload'; $download = Join-Path $root 'download'
New-Item -ItemType Directory -Force -Path $appData, $share, $readOnlyShare, $upload, $download | Out-Null

function Wait-Until([scriptblock]$condition, [string]$message, [int]$seconds = 30) {
  $until = [DateTime]::UtcNow.AddSeconds($seconds)
  do { if (& $condition) { return }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $until)
  throw $message
}
function Assert-File([string]$path, [string]$content) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Missing expected file: $path" }
  if ([IO.File]::ReadAllText($path) -cne $content) { throw "Unexpected content: $path" }
}
function Invoke-Cli([string]$profilePath, [int]$expectedExitCode = 0) {
  $output = & $CliPath run --profile $profilePath --non-interactive --json-log 2>&1
  if ($LASTEXITCODE -ne $expectedExitCode) { throw "CLI returned $LASTEXITCODE, expected $expectedExitCode. Output: $($output -join [Environment]::NewLine)" }
  $lines = @($output | Where-Object { $_ -match '^\{' })
  if ($expectedExitCode -eq 0 -and $lines.Count -ne 1) { throw "CLI did not emit exactly one JSON result: $($output -join [Environment]::NewLine)" }
}

$server = $null
$previousDataDir = $env:FENGSYNC_DATA_DIR
try {
  $modules = Join-Path $Workspace '.fengsync-test\sftp-node\node_modules'
  if (-not (Test-Path (Join-Path $modules 'ssh2\package.json'))) {
    $moduleRoot = Split-Path $modules -Parent; New-Item -ItemType Directory -Force -Path $moduleRoot | Out-Null
    Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package.json') (Join-Path $moduleRoot 'package.json') -Force
    Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package-lock.json') (Join-Path $moduleRoot 'package-lock.json') -Force
    & npm ci --omit=dev --prefix $moduleRoot
    if ($LASTEXITCODE -ne 0) { throw 'Unable to install pinned SFTP fixture dependency.' }
  }
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
  $user = 'ui'; $password = 'ui-sftp-password'; $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
  $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
  $options = @{ Enabled=$true; ListenAddress='127.0.0.1'; Port=$port; MaxConnections=2; Accounts=@(@{UserName=$user;Enabled=$true;PasswordSalt=[Convert]::ToBase64String($salt);PasswordHash=[Convert]::ToBase64String($hash);PasswordIterations=210000;PublicKeys=@()}); Shares=@(@{VirtualName='docs';PhysicalPath=$share;Permission='ReadWrite'}, @{VirtualName='readonly';PhysicalPath=$readOnlyShare;Permission='ReadOnly'}) }
  $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{ Options=$options; HostKeyPath=(Join-Path $root 'host-key.pem') } | ConvertTo-Json -Depth 8 -Compress)))
  $start = [Diagnostics.ProcessStartInfo]::new((Get-Command node -ErrorAction Stop).Source)
  $start.Arguments = '"' + (Join-Path $Workspace 'src\FengSync.Core\SftpServer\node-sftp-host.cjs') + '"'; $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardInput = $true; $start.RedirectStandardError = $true
  $start.EnvironmentVariables['FENGSYNC_SFTP_CONFIG'] = $payload; $start.EnvironmentVariables['NODE_PATH'] = $modules
  $server = [Diagnostics.Process]::Start($start)
  # A raw TCP probe closes before the SSH handshake and is treated as an auth
  # failure by some ssh2 builds. The actual CLI connection below is the readiness
  # assertion; first make sure the managed host did not exit during startup.
  Wait-Until { -not $server.HasExited } 'SFTP fixture exited during startup'
  Start-Sleep -Milliseconds 500

  $rclone = Join-Path (Split-Path $CliPath) 'Assets\rclone\rclone.exe'
  $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
  & $rclone config create protocol_sftp sftp host 127.0.0.1 user $user port "$port" pass $password --config $config
  if ($LASTEXITCODE -ne 0) { throw 'Could not configure the isolated SFTP remote.' }
  if (-not (Test-Path -LiteralPath $config)) { throw "rclone did not create the isolated configuration: $config" }
  $env:FENGSYNC_DATA_DIR = $appData

  $relative = 'nested/中文 proof.txt'; $source = Join-Path $upload $relative; New-Item -ItemType Directory -Force -Path (Split-Path $source), (Join-Path $upload 'empty-directory') | Out-Null
  [IO.File]::WriteAllText($source, 'sftp-protocol-roundtrip')
  $uploadProfile = @{ Id='sftp-upload'; Name='SFTP upload'; LeftPath=$upload; RightPath='sftp://protocol_sftp/docs'; Mode=2; Enabled=$true } | ConvertTo-Json -Compress
  $uploadProfilePath = Join-Path $root 'upload.fengsync.json'; Set-Content -LiteralPath $uploadProfilePath -Value $uploadProfile -NoNewline
  Invoke-Cli $uploadProfilePath
  Assert-File (Join-Path $share $relative) 'sftp-protocol-roundtrip'
  if (-not (Test-Path -LiteralPath (Join-Path $share 'empty-directory'))) { throw 'SFTP upload did not create the empty source directory.' }
  if (Get-ChildItem -LiteralPath $share -Recurse -Filter '*.partial' -ErrorAction SilentlyContinue) { throw 'SFTP upload left a partial file in the share.' }

  [IO.File]::WriteAllText((Join-Path $share 'obsolete.txt'), 'obsolete')
  $mirrorProfile = @{ Id='sftp-mirror'; Name='SFTP mirror'; LeftPath=$upload; RightPath='sftp://protocol_sftp/docs'; Mode=1; Enabled=$true } | ConvertTo-Json -Compress
  $mirrorProfilePath = Join-Path $root 'mirror.fengsync.json'; Set-Content -LiteralPath $mirrorProfilePath -Value $mirrorProfile -NoNewline
  Invoke-Cli $mirrorProfilePath
  if (Test-Path -LiteralPath (Join-Path $share 'obsolete.txt')) { throw 'SFTP mirror did not delete a destination-only file.' }

  $readOnlyProfile = @{ Id='sftp-readonly'; Name='SFTP readonly'; LeftPath=$upload; RightPath='sftp://protocol_sftp/readonly'; Mode=2; Enabled=$true } | ConvertTo-Json -Compress
  $readOnlyProfilePath = Join-Path $root 'readonly.fengsync.json'; Set-Content -LiteralPath $readOnlyProfilePath -Value $readOnlyProfile -NoNewline
  # The CLI classifies a backend refusal during plan preparation as a
  # configuration/endpoint error (rather than a partial-transfer result).
  Invoke-Cli $readOnlyProfilePath 4
  if (Get-ChildItem -LiteralPath $readOnlyShare -Recurse -File -ErrorAction SilentlyContinue) { throw 'Read-only SFTP share accepted an upload.' }

  & $rclone config create bad_sftp sftp host 127.0.0.1 user $user port "$port" pass 'wrong-password' --config $config
  if ($LASTEXITCODE -ne 0) { throw 'Could not configure the invalid-credential SFTP remote.' }
  $badCredentialProfile = @{ Id='sftp-bad-credential'; Name='SFTP bad credential'; LeftPath='sftp://bad_sftp/docs'; RightPath=$download; Mode=2; Enabled=$true } | ConvertTo-Json -Compress
  $badCredentialPath = Join-Path $root 'bad-credential.fengsync.json'; Set-Content -LiteralPath $badCredentialPath -Value $badCredentialProfile -NoNewline
  Invoke-Cli $badCredentialPath 4

  $downloadProfile = @{ Id='sftp-download'; Name='SFTP download'; LeftPath='sftp://protocol_sftp/docs'; RightPath=$download; Mode=2; Enabled=$true } | ConvertTo-Json -Compress
  $downloadProfilePath = Join-Path $root 'download.fengsync.json'; Set-Content -LiteralPath $downloadProfilePath -Value $downloadProfile -NoNewline
  Invoke-Cli $downloadProfilePath
  Assert-File (Join-Path $download $relative) 'sftp-protocol-roundtrip'

  $server.Kill($true); $server.WaitForExit(); $server.Dispose(); $server = $null
  $failedProfilePath = Join-Path $root 'offline.fengsync.json'; Set-Content -LiteralPath $failedProfilePath -Value $downloadProfile -NoNewline
  Invoke-Cli $failedProfilePath 4
  Write-Output "Passed real SFTP protocol integration: $runId"
}
finally {
  $env:FENGSYNC_DATA_DIR = $previousDataDir
  if ($server) { if (-not $server.HasExited) { $server.Kill($true); $server.WaitForExit() }; $server.Dispose() }
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
