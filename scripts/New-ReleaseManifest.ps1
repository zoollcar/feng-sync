param(
  [Parameter(Mandatory)][string]$PublishDirectory,
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [string]$OutputPath
)
$dir = [IO.Path]::GetFullPath($PublishDirectory)
if (!(Test-Path -LiteralPath $dir -PathType Container)) { throw "Publish directory does not exist: $dir" }
if (!$OutputPath) { $OutputPath = Join-Path $dir 'release-manifest.json' }
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$files = Get-ChildItem -LiteralPath $dir -File -Recurse | Where-Object { [IO.Path]::GetFullPath($_.FullName) -ne $outputFull -and $_.Name -notmatch '\.zip(\.sha256)?$' } | ForEach-Object {
  $relative = [IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\','/')
  if ($relative -match '(^|/)\.\.(/|$)' -or $relative -match ':' ) { throw "Unsafe release path: $relative" }
  [ordered]@{ path=$relative; size=[int64]$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
} | Sort-Object path
if (!$files) { throw 'Publish directory contains no release files.' }
$manifest = [ordered]@{ product='FengSync'; version=$Version; runtime='win-x64'; files=@($files) }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

# Read back the output: a release gate must not rely only on the input argument.
$written = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
if ($written.product -cne 'FengSync' -or $written.runtime -cne 'win-x64' -or $written.version -cne $Version) {
  throw "Generated manifest does not match the requested FengSync win-x64 version '$Version'."
}
