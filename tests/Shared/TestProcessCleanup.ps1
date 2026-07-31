function Clear-FengSyncTestProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [int]$CurrentProcessId = $PID
    )

    # Only match processes unambiguously owned by this repository's test harness:
    # explicit run markers, test fixture directories, or the dedicated test scripts.
    # Never target a normally launched FengSync installation.
    $candidates = Get-CimInstance Win32_Process | Where-Object {
        $command = $_.CommandLine ?? ''
        $_.ProcessId -ne $CurrentProcessId -and $_.Name -in @('FengSync.exe', 'FengSync.Cli.exe', 'rclone.exe') -and (
            $command -like '*--fengsync-test-run-id*' -or
            $command -like '*\.fengsync-test\*' -or
            # Covers the older UI test executable format too, without touching
            # a normal application installation.
            ($_.Name -eq 'FengSync.exe' -and $command -like '*\tests\FengSync.UiTests\bin\*')
        )
    } | Sort-Object ProcessId -Unique

    foreach ($candidate in $candidates) {
        try { & taskkill.exe /PID $candidate.ProcessId /T /F *> $null }
        catch { Write-Warning "Could not stop stale FengSync test process $($candidate.ProcessId): $($_.Exception.Message)" }
    }
}
