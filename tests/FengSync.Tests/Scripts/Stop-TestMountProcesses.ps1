# Cleans up rclone mount processes spawned by Feng Sync integration tests. Tests mark their processes
# by passing the --cache-dir under the FENGSYNC_DATA_DIR mount/cache path; this script stops anything
# that still matches the pattern when a test run aborts mid-way.
function Stop-TestMountProcesses {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DataDir)

    if (-not (Test-Path $DataDir)) { return }
    $cacheRoot = Join-Path $DataDir 'mount\cache'
    $candidates = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'rclone.exe' -and ($_.CommandLine -like "*$cacheRoot*")
    }
    foreach ($candidate in $candidates) {
        try { & taskkill.exe /PID $candidate.ProcessId /T /F *> $null }
        catch { Write-Warning "Could not stop stale test mount $($candidate.ProcessId): $($_.Exception.Message)" }
    }
}