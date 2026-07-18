[CmdletBinding()]
param([string] $Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = Join-Path $workspace '.fengsync-test\gui-sftp-fixture'
$share = Join-Path $root 'share'
# Reuse the disposable fixture key to avoid expensive RSA generation during each GUI run.
$hostKey = Join-Path $workspace '.fengsync-test\real-sftp-host.pem'
$port = 22922
$user = 'gui'
$password = 'gui-sftp-password'
$node = (Get-Command node -ErrorAction Stop).Source
$hostScript = Join-Path $workspace 'src\FengSync.Core\SftpServer\node-sftp-host.cjs'
$modules = Join-Path $workspace '.fengsync-test\sftp-node\node_modules'

if (-not (Test-Path (Join-Path $modules 'ssh2\package.json'))) {
    $fixture = Split-Path $modules -Parent
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    Copy-Item (Join-Path $workspace 'src\FengSync.Core\SftpServer\package.json') (Join-Path $fixture 'package.json') -Force
    Copy-Item (Join-Path $workspace 'src\FengSync.Core\SftpServer\package-lock.json') (Join-Path $fixture 'package-lock.json') -Force
    & npm ci --omit=dev --prefix $fixture
    if ($LASTEXITCODE -ne 0) { throw '无法准备固定 ssh2 GUI 测试依赖。' }
}

if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $share | Out-Null
$salt = New-Object byte[] 16
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
$derive = [Security.Cryptography.Rfc2898DeriveBytes]::new($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256)
$hash = $derive.GetBytes(32)
$derive.Dispose()
$options = [ordered]@{
 Enabled = $true; ListenAddress = '127.0.0.1'; Port = $port; MaxConnections = 4
 Accounts = @([ordered]@{ UserName = $user; Enabled = $true; PasswordSalt = [Convert]::ToBase64String($salt); PasswordHash = [Convert]::ToBase64String($hash); PasswordIterations = 210000; PublicKeys = @() })
 Shares = @([ordered]@{ VirtualName = 'docs'; PhysicalPath = $share; Permission = 'ReadWrite' })
}
$payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{ Options = $options; HostKeyPath = $hostKey } | ConvertTo-Json -Depth 8 -Compress)))
$start = [Diagnostics.ProcessStartInfo]::new($node)
$start.Arguments = '"{0}"' -f $hostScript
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardInput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardOutput = $true
$start.EnvironmentVariables['FENGSYNC_SFTP_CONFIG'] = $payload; $start.EnvironmentVariables['NODE_PATH'] = $modules
$server = [Diagnostics.Process]::Start($start)
try {
    $ready = $false
    foreach ($attempt in 1..50) { try { $tcp = [Net.Sockets.TcpClient]::new(); $tcp.Connect('127.0.0.1', $port); $tcp.Dispose(); $ready = $true; break } catch { Start-Sleep -Milliseconds 100 } }
    if (-not $ready) { throw "SFTP fixture did not listen on port ${port}: $($server.StandardError.ReadToEnd())" }
    & (Join-Path $PSScriptRoot 'Run-GuiAcceptance.ps1') -Configuration $Configuration -IncludeSftp -SftpPort $port -SftpUser $user -SftpPassword $password -SftpRemoteName 'fengsync_gui' -SftpShareName 'docs'
    if ($LASTEXITCODE -ne 0) { throw 'GUI SFTP acceptance script failed.' }
}
finally {
    if ($server -and -not $server.HasExited) { $server.StandardInput.Close(); if (-not $server.WaitForExit(3000)) { $server.Kill($true) } }
    if ($server) { [IO.File]::WriteAllText((Join-Path $root 'server.stderr.log'), $server.StandardError.ReadToEnd()) }
}
