param(
  [Parameter(Mandatory)][string]$PublishDirectory,
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [Parameter(Mandatory)][string]$ZipPath,
  [Parameter(Mandatory)][string]$Sha256Path
)

$ErrorActionPreference = 'Stop'

$publish = [IO.Path]::GetFullPath($PublishDirectory)
$zip = [IO.Path]::GetFullPath($ZipPath)
$sha = [IO.Path]::GetFullPath($Sha256Path)
$expectedZipName = "FengSync-$Version-win-x64.zip"
$expectedShaName = "$expectedZipName.sha256"

if (!(Test-Path -LiteralPath $publish -PathType Container)) { throw "Publish directory does not exist: $publish" }
if (!(Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Release ZIP does not exist: $zip" }
if (!(Test-Path -LiteralPath $sha -PathType Leaf)) { throw "Release checksum does not exist: $sha" }
if ((Split-Path -Leaf $zip) -cne $expectedZipName) { throw "Release ZIP must be named $expectedZipName." }
if ((Split-Path -Leaf $sha) -cne $expectedShaName) { throw "Release checksum must be named $expectedShaName." }

$exe = Join-Path $publish 'FengSync.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Release output missing: FengSync.exe" }
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($productVersion -cne $Version) { throw "FengSync.exe ProductVersion '$productVersion' must equal release version '$Version'." }

$manifestPath = Join-Path $publish 'release-manifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Release output missing: release-manifest.json' }
$manifestJson = Get-Content -LiteralPath $manifestPath -Raw
$manifest = $manifestJson | ConvertFrom-Json
if ($manifest.version -cne $Version) { throw "release-manifest.json version '$($manifest.version)' must equal release version '$Version'." }
if (@($manifest.files).Count -eq 0) { throw 'Release manifest must contain files.' }
for ($i = 1; $i -lt $manifest.files.Count; $i++) {
  $previous = $manifest.files[$i - 1].path
  $current = $manifest.files[$i].path
  if ([StringComparer]::Ordinal.Compare($previous, $current) -ge 0) {
    throw "Release manifest files must be sorted by path using StringComparer.Ordinal: '$previous' precedes '$current'."
  }
}

Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  $files = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/') })
  if ($files.Count -eq 0) { throw 'Release ZIP contains no files.' }
  $roots = @($files | ForEach-Object { ($_.FullName -split '/', 2)[0] } | Sort-Object -Unique)
  $expectedRoot = [IO.Path]::GetFileNameWithoutExtension($expectedZipName)
  if ($roots.Count -ne 1 -or $roots[0] -cne $expectedRoot) { throw "Release ZIP must contain exactly one root directory named '$expectedRoot'; found: $($roots -join ', ')." }
  if ($files | Where-Object { $_.FullName -notmatch '^[^/]+/.+' }) { throw 'Release ZIP contains a file outside its package root.' }
  $manifestEntries = @($files | Where-Object { $_.FullName -ceq "$expectedRoot/release-manifest.json" })
  if ($manifestEntries.Count -ne 1) { throw 'Release ZIP must contain exactly one release-manifest.json at its package root.' }
  $reader = [IO.StreamReader]::new($manifestEntries[0].Open())
  try { $packagedManifestJson = $reader.ReadToEnd() }
  finally { $reader.Dispose() }
  if ($packagedManifestJson -cne $manifestJson) { throw 'Release ZIP manifest does not match the validated publish manifest.' }
}
finally { $archive.Dispose() }

$actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedChecksumLine = "$actualHash  $expectedZipName"
$checksumLine = (Get-Content -LiteralPath $sha -Raw).Trim()
if ($checksumLine -cne $expectedChecksumLine) { throw "Release checksum must contain exactly '$expectedChecksumLine'." }

Write-Host "Release package gates passed for $expectedZipName."
