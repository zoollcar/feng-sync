[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
  [switch]$SkipGoogleDrive
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

Require-Command dotnet
Require-Command pwsh
Require-Command node

$rclone = Join-Path $workspace 'src\FengSync\Assets\rclone\rclone.exe'
if (-not (Test-Path -LiteralPath $rclone)) { throw "Bundled rclone was not found: $rclone" }

Push-Location $workspace
try {
  Invoke-Stage 'Build' { & dotnet build .\FengSync.sln -c $Configuration --nologo }
  Invoke-Stage 'Core, CLI and real SFTP protocol tests' { & dotnet test .\tests\FengSync.Tests\FengSync.Tests.csproj -c $Configuration --no-build --nologo }

  $filter = if ($SkipGoogleDrive) { 'Category!=External' } else { '' }
  $arguments = @('test', '.\tests\FengSync.UiTests\FengSync.UiTests.csproj', '-c', $Configuration, '--no-build', '--nologo')
  if ($filter) { $arguments += @('--filter', $filter) }
  $uiName = if ($SkipGoogleDrive) { 'WPF UI acceptance tests (Google Drive excluded)' } else { 'WPF UI acceptance tests (including configured Google Drive)' }
  Invoke-Stage $uiName { & dotnet @arguments }
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
