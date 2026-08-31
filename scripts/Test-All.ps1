[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
  [ValidateSet('Core', 'UiOffline', 'Online')][string]$Level = 'UiOffline'
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = [System.Collections.Generic.List[object]]::new()

function Require-Command([string]$name) {
  if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "Required command was not found: $name" }
}
function Invoke-Stage([string]$name, [scriptblock]$action) {
  $timer = [Diagnostics.Stopwatch]::StartNew()
  Write-Host "`n=== $name ===" -ForegroundColor Cyan
  try {
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
    $results.Add([pscustomobject]@{ Stage=$name; Result='Passed'; Duration=$timer.Elapsed })
  } catch {
    $results.Add([pscustomobject]@{ Stage=$name; Result='Failed'; Duration=$timer.Elapsed })
    throw
  }
}
function Reset-TestArtifacts {
  $artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace '.fengsync-test'))
  $artifactParent = [IO.Directory]::GetParent($artifactRoot)?.FullName
  if (-not [string]::Equals($artifactParent, $workspace, [StringComparison]::OrdinalIgnoreCase) -or
      -not [string]::Equals([IO.Path]::GetFileName($artifactRoot), '.fengsync-test', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear an unexpected test-artifact path: $artifactRoot"
  }

  $cleanup = Join-Path $workspace 'tests\Shared\TestProcessCleanup.ps1'
  . $cleanup
  Clear-FengSyncTestProcesses -Workspace $workspace
  if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
  }
  New-Item -ItemType Directory -Path $artifactRoot | Out-Null
  Write-Host "Prepared an empty test-artifact directory: $artifactRoot" -ForegroundColor DarkGray
}

Require-Command dotnet
Require-Command pwsh

if ($Level -eq 'Online') {
  $requiredR2Variables = @(
    'FENGSYNC_TEST_R2_ACCOUNT_ID',
    'FENGSYNC_TEST_R2_ACCESS_KEY_ID',
    'FENGSYNC_TEST_R2_SECRET_ACCESS_KEY'
  )
  $missingR2Variables = @($requiredR2Variables | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
  if ($missingR2Variables.Count -gt 0) {
    throw "Online tests require Cloudflare R2 credentials in: $($missingR2Variables -join ', ')."
  }
}

$rclone = Join-Path $workspace 'src\FengSync\Assets\rclone\rclone.exe'
if (-not (Test-Path -LiteralPath $rclone)) { throw "Bundled rclone was not found: $rclone" }

Push-Location $workspace
try {
  Reset-TestArtifacts
  Invoke-Stage 'Build' { & dotnet build .\FengSync.sln -c $Configuration --nologo }
  $winFspService = Get-Service -Name 'WinFsp.Launcher' -ErrorAction SilentlyContinue
  if ($Level -ne 'Core' -and $winFspService) { $env:FENGSYNC_TEST_REAL_MOUNT = '1' }
  elseif ($Level -ne 'Core') { Write-Warning 'WinFsp is not installed; the opt-in real mount test will not execute.' }
  try { Invoke-Stage 'Core, CLI, real SFTP and available WinFsp tests' { & dotnet test .\tests\FengSync.Tests\FengSync.Tests.csproj -c $Configuration --no-build --nologo } }
  finally { Remove-Item Env:FENGSYNC_TEST_REAL_MOUNT -ErrorAction SilentlyContinue }

  if ($Level -eq 'Core') { return }

  $filter = if ($Level -eq 'UiOffline') { 'Category!=External' } else { '' }
  $arguments = @('test', '.\tests\FengSync.UiTests\FengSync.UiTests.csproj', '-c', $Configuration, '--no-build', '--nologo')
  if ($filter) { $arguments += @('--filter', $filter) }
  $uiName = if ($Level -eq 'UiOffline') { 'WPF UI acceptance tests (online services excluded)' } else { 'WPF UI acceptance tests (including online services)' }
  if ($Level -eq 'Online') { $env:FENGSYNC_TEST_ONLINE_SERVICES = '1' }
  try { Invoke-Stage $uiName { & dotnet @arguments } }
  finally { Remove-Item Env:FENGSYNC_TEST_ONLINE_SERVICES -ErrorAction SilentlyContinue }
}
catch {
  Write-Error $_
  exit 1
}
finally {
  if ($results.Count -gt 0) {
    Write-Host "`n=== Test summary ===" -ForegroundColor Cyan
    $results | Format-Table Stage, Result, @{Label='Duration';Expression={ $_.Duration.ToString('mm\:ss') }} -AutoSize
    Write-Host "Failure artifacts, when any, are retained below: $workspace\.fengsync-test" -ForegroundColor DarkGray
  }
  Pop-Location
}
