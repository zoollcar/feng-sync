[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ui-shell', 'ui-shell-native', 'ui-shell-software', 'ui-visual-matrix', 'update-settings', 'about', 'local', 'modes', 'selection', 'sftp-to-local', 'sftp-ui', 'profile', 'profile-filter', 'delete-threshold', 'settings', 'history', 'schedule', 'gdrive', 'gdrive-volume')][string]$Scenario,
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$Workspace
)

# Every scenario uses a new data root. On failure it is retained with its logs and
# screenshots; remote Google Drive cleanup is constrained to a generated child below
# the fixed test/FengSync-Automated-Tests test root.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FengSyncUiMouse {
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
'@
$AppPath = [IO.Path]::GetFullPath($AppPath); $Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Application not found: $AppPath" }
$cleanup = Join-Path $Workspace 'tests\Shared\TestProcessCleanup.ps1'; . $cleanup; Clear-FengSyncTestProcesses -Workspace $Workspace
$stamp = "ui-$Scenario-" + [Guid]::NewGuid().ToString('N')
$root = Join-Path $Workspace ('.fengsync-test\ui\' + $stamp)
$appData = Join-Path $root 'appdata'; $artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $root, $appData, $artifacts | Out-Null
# A test has no reason to retain completed progress windows. This makes every
# successful sync self-closing even before the explicit UI assertion below.
[IO.File]::WriteAllText((Join-Path $appData 'FengSync.local.json'), '{"ShowCompleted":false}')
$scenarioTimer = [Diagnostics.Stopwatch]::StartNew()
$harnessLog = Join-Path $root 'harness.log'
function Write-HarnessTrace([string]$message) {
  $line = '{0:O} | {1}' -f [DateTimeOffset]::Now, $message
  [IO.File]::AppendAllText($harnessLog, $line + [Environment]::NewLine)
  Write-Output $message
}
Write-HarnessTrace "Starting scenario: $Scenario"

function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
  $end = [DateTime]::UtcNow.AddSeconds($Seconds)
  do { $value = & $Condition; if ($null -ne $value -and $value -ne $false) { return $value }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $end)
  throw $Message
}
function Find-Id($root, [string]$id, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) } catch { $null } } "Missing UI element: $id" $seconds }
function Find-Name($root, [string]$name, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) } catch { $null } } "Missing UI element: $name" $seconds }
function Click($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { throw "Element cannot be invoked: $($element.Current.Name)" }; $p.Invoke() }
function Open-Menu($element) { $p = $null; if ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); return }; Click $element }
function Set-Text($element, [string]$value) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element has no ValuePattern: $($element.Current.Name)" }; $p.SetValue($value) }
function Set-Password($element, [string]$value) { $element.SetFocus(); [Windows.Forms.SendKeys]::SendWait($value) }
function Select-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { throw "Element cannot be selected: $($element.Current.Name)" }; $p.Select() }
function Toggle-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { throw "Element cannot be toggled: $($element.Current.Name)" }; $p.Toggle() }
function Get-ToggleState($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { throw "Element has no TogglePattern: $($element.Current.Name)" }; return $p.Current.ToggleState }
function Get-Text($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element has no ValuePattern: $($element.Current.Name)" }; return $p.Current.Value }
function Drag-ElementHorizontally($element, [int]$offset) {
  $box = $element.Current.BoundingRectangle
  if ($box.Width -le 0 -or $box.Height -le 0) { throw "Element cannot be dragged because it has no bounds: $($element.Current.AutomationId)" }
  $start = [Drawing.Point]::new([int]($box.Left + ($box.Width / 2)), [int]($box.Top + ($box.Height / 2)))
  [Windows.Forms.Cursor]::Position = $start; Start-Sleep -Milliseconds 80
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  [Windows.Forms.Cursor]::Position = [Drawing.Point]::new($start.X + $offset, $start.Y); Start-Sleep -Milliseconds 180
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}
function Get-LiveMain {
  if (-not $script:process -or $script:process.HasExited) { throw 'Feng Sync exited before the current UI step completed.' }
  $script:process.Refresh()
  if ($script:process.MainWindowHandle -eq 0) { throw 'Feng Sync no longer has a main window.' }
  return [System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
}
function Find-AppWindow([scriptblock]$predicate) {
  if (-not $script:process -or $script:process.HasExited) { return $null }
  $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
      $script:process.Id))
  foreach ($window in $windows) {
    if ($window.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and (& $predicate $window)) { return $window }
  }
  return $null
}
function Find-WindowLike([string]$titleFragment, [int]$seconds = 20) {
  Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like "*$titleFragment*" } } catch { $null } } "Missing Feng Sync window containing: $titleFragment" $seconds
}
function Select-Mode($main, [string]$name) { $combo = Find-Id $main 'SyncModeBox'; $p = $null; if (-not $combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { throw 'Sync mode cannot expand.' }; $p.Expand(); Select-Ui (Find-Name $combo $name) }
function Set-ProfileConcurrency($main, [int]$value) {
  Click (Find-Id $main 'EditCurrentProfileButton')
  $editor = Find-WindowLike 'Profile'
  $sections = Find-Id $editor 'ProfileSections'
  $sections.SetFocus()
  [Windows.Forms.SendKeys]::SendWait('{END}')
  $useDefault = Find-Id $editor 'ProfileUseDefaultConcurrency'
  if ((Get-ToggleState $useDefault) -eq [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $useDefault }
  Set-Text (Find-Id $editor 'ProfileConcurrency') ([string]$value)
  Click (Find-Id $editor 'ProfileSave')
  Wait-Until {
    $liveMain = Get-LiveMain
    (Find-Id $liveMain 'Status' 2).Current.Name -match '已保存' -and
      (Find-Id $liveMain 'ConcurrencyLabel' 2).Current.Name -match "^$value\s*路$"
  } "Profile concurrency did not change to $value." 30
  Write-HarnessTrace "Profile concurrency changed through the GUI to $value."
}
function Get-UiStatus {
  return (Find-Id (Get-LiveMain) 'Status' 2).Current.Name
}
function Assert-NoUiFailure([string]$operation) {
  $status = Get-UiStatus
  if ($status -match '失败|已取消|未完成|错误|无法') { throw "$operation failed. UI status: $status" }
  return $status
}
function Wait-MainReadyAfterSync([int]$seconds) {
  Write-HarnessTrace 'Synchronized result observed; waiting for the application operation boundary.'
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  $mainReady = $false
  do {
    Assert-NoUiFailure 'Synchronization' | Out-Null
    if ((Find-Id (Get-LiveMain) 'CompareButton' 2).Current.IsEnabled) { $mainReady = $true; break }
    Start-Sleep -Milliseconds 150
  } while ([DateTime]::UtcNow -lt $deadline)
  if (-not $mainReady) { throw 'The main window did not become interactive after synchronization.' }
  $status = Assert-NoUiFailure 'Synchronization'
  if ($status -notmatch '同步完成') { throw "Synchronization ended without a successful completion status. UI status: $status" }
  Write-HarnessTrace "Application operation boundary reached. UI status: $status"
}
$approvedConfirmations = [Collections.Generic.HashSet[string]]::new()
function Approve-ConfirmationIfPresent {
  $confirm = try { Find-AppWindow { param($window) $window.Current.Name -eq '确认同步操作' } } catch { $null }
  if ($confirm) {
    # A just-closed modal can remain in UIA's tree for one polling interval.
    # Its button must not receive a second Invoke, or WPF raises when DialogResult
    # is assigned after the modal loop has already ended.
    $id = [string]::Join('-', $confirm.GetRuntimeId())
    if ($approvedConfirmations.Add($id)) { Click (Find-Name $confirm '确认同步' 2); return $true }
  }
  return $false
}
function Wait-Sync { param($main, [string]$expectedFile, [int]$comparisonSeconds = 120, [int]$transferSeconds = 120, [string]$expectedContent = $null)
  $checkExpectedContent = $PSBoundParameters.ContainsKey('expectedContent')
  try { Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Comparison did not produce an executable plan' $comparisonSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Comparison did not produce an executable plan. UI status: $status" }
  $expectedDescription = if ($checkExpectedContent) { $expectedContent } else { '<existence-only>' }
  Write-Output "Comparison ready; expected sync output: $expectedFile; expected content: $expectedDescription"
  Click (Find-Id $main 'SyncButton')
  Write-HarnessTrace 'Sync button invoked.'
  # Verify the observable synchronization result rather than relying on localized
  # status text, which UI Automation can expose with a different console encoding.
  Write-HarnessTrace "Probing synchronized result: $expectedFile"
  $resultDeadline = [DateTime]::UtcNow.AddSeconds($transferSeconds)
  $resultObserved = $false
  try {
    do {
      Approve-ConfirmationIfPresent | Out-Null
      Assert-NoUiFailure 'Synchronization' | Out-Null
      $outputExists = Test-Path -LiteralPath $expectedFile
      if ($outputExists) {
        if (-not $checkExpectedContent) { $resultObserved = $true }
        else {
          try { $resultObserved = [IO.File]::ReadAllText($expectedFile) -eq $expectedContent }
          catch { $resultObserved = $false }
        }
      }
      if ($resultObserved) { break }
      Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $resultDeadline)
    if (-not $resultObserved) { throw "Expected synchronized result was not produced: $expectedFile" }
  }
  catch {
    $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }
    $actual = try { if (Test-Path -LiteralPath $expectedFile) { [IO.File]::ReadAllText($expectedFile) } else { '<missing>' } } catch { '<unreadable>' }
    throw "Expected synchronized result was not produced: $expectedFile. Expected content: $expectedContent. Actual: $actual. UI status: $status"
  }
  # Sync_Click re-enables CompareButton only from its finally block, after result
  # persistence and progress-window completion have finished. This is the actual
  # operation boundary; waiting on status text first can strand a completed test
  # at the main window when UI Automation observes a stale text peer.
  Wait-MainReadyAfterSync $transferSeconds
  Write-HarnessTrace 'Sync complete; main window is responsive and the scenario will continue its next UI action.'
  $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }
  $actual = try { [IO.File]::ReadAllText($expectedFile) } catch { '<unreadable>' }
  Write-Output ("Sync result observed; status: {0}; output exists: {1}; actual content: {2}" -f $status, (Test-Path -LiteralPath $expectedFile), $actual)
}
function Compare-Ui {
  param($main, [string]$left, [string]$right, [int]$seconds = 120)
  Set-Text (Find-Id $main 'LeftPath') $left
  Set-Text (Find-Id $main 'RightPath') $right

  # Changing SyncModeBox starts a comparison automatically when both endpoint
  # fields are populated. Let that operation settle and reuse its plan instead
  # of queuing an indistinguishable second comparison.
  $preflightDeadline = [DateTime]::UtcNow.AddSeconds($seconds)
  do {
    $liveMain = Get-LiveMain
    $status = Get-UiStatus
    $compareEnabled = (Find-Id $liveMain 'CompareButton' 2).Current.IsEnabled
    if ($compareEnabled) { break }
    if ($status -match '失败|已取消|错误|无法|需要修复') { throw "Comparison failed. UI status: $status" }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $preflightDeadline)
  if (-not $compareEnabled) { throw "An automatically started comparison did not finish. UI status: $status" }
  $syncEnabled = (Find-Id $liveMain 'SyncButton' 2).Current.IsEnabled
  if ($syncEnabled -and $status -match '比较完成') {
    Write-HarnessTrace "Using the comparison completed by the preceding UI action. UI status: $status"
    return
  }

  $statusBeforeClick = Get-UiStatus
  Click (Find-Id $main 'CompareButton')
  Write-HarnessTrace "Compare button invoked: $left <-> $right"

  # InvokePattern queues the WPF click; it does not wait for Compare_Click to
  # enter. Require evidence that this invocation was accepted before considering
  # enabled buttons. Otherwise a second compare can consume the previous plan.
  $accepted = $false
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  do {
    $liveMain = Get-LiveMain
    $status = Get-UiStatus
    if ($status -match '失败|已取消|错误|无法|需要修复') { throw "Comparison failed. UI status: $status" }
    $compareEnabled = (Find-Id $liveMain 'CompareButton' 2).Current.IsEnabled
    $syncEnabled = (Find-Id $liveMain 'SyncButton' 2).Current.IsEnabled
    if (-not $compareEnabled -or $status -ne $statusBeforeClick) { $accepted = $true }
    if ($accepted -and $compareEnabled -and $syncEnabled) {
      Write-HarnessTrace "Comparison completed. UI status: $status"
      return
    }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "Comparison did not complete with an executable plan. UI status: $status"
}
function New-SmallFiles { param([string]$directory, [int]$count)
  New-Item -ItemType Directory -Force -Path $directory | Out-Null
  for ($i = 1; $i -le $count; $i++) { [IO.File]::WriteAllText((Join-Path $directory ('batch-{0:D3}.txt' -f $i)), "small performance fixture $i") }
}
function New-FolderFiles { param([string]$directory, [int]$count)
  for ($i = 1; $i -le $count; $i++) {
    $folder = Join-Path $directory ('folder-{0:D3}' -f $i)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'item.txt'), "folder performance fixture $i")
  }
}
function Invoke-MeasuredSync { param($main, [string]$left, [string]$right, [string]$expectedFile, [int]$comparisonSeconds = 180, [int]$transferSeconds = 600)
  $comparisonTimer = [Diagnostics.Stopwatch]::StartNew()
  Compare-Ui $main $left $right
  try { Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Comparison did not produce an executable plan' $comparisonSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Comparison did not produce an executable plan. UI status: $status" }
  $comparisonTimer.Stop()
  $syncTimer = [Diagnostics.Stopwatch]::StartNew()
  Click (Find-Id $main 'SyncButton')
  Write-HarnessTrace 'Measured sync button invoked.'
  try {
    $resultDeadline = [DateTime]::UtcNow.AddSeconds($transferSeconds)
    do {
      Approve-ConfirmationIfPresent | Out-Null
      Assert-NoUiFailure 'Synchronization' | Out-Null
      if (Test-Path -LiteralPath $expectedFile) { break }
      Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $resultDeadline)
    if (-not (Test-Path -LiteralPath $expectedFile)) { throw "Expected synchronized file was not created: $expectedFile" }
  }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Expected synchronized file was not created: $expectedFile. UI status: $status" }
  Wait-MainReadyAfterSync $transferSeconds
  Write-HarnessTrace 'Measured sync complete; main window is responsive and the scenario will continue its next UI action.'
  $syncTimer.Stop()
  [pscustomobject]@{ CompareMilliseconds = $comparisonTimer.ElapsedMilliseconds; SyncMilliseconds = $syncTimer.ElapsedMilliseconds }
}
function Assert-GoogleDriveFileCount { param([string]$remotePath, [int]$count)
  Wait-Until {
    $listing = & $rclone lsf $remotePath --recursive --config $config --contimeout 10s --timeout 30s 2>$null
    $LASTEXITCODE -eq 0 -and @($listing | Where-Object { $_ -match '\.txt$' }).Count -eq $count
  } "Google Drive did not contain all $count fixture files: $remotePath" 180
}
function Start-App {
  $start = [Diagnostics.ProcessStartInfo]::new($AppPath); $start.UseShellExecute = $false; $start.Arguments = "--fengsync-test-run-id $stamp"; $start.EnvironmentVariables['FENGSYNC_DATA_DIR'] = $appData; $start.EnvironmentVariables['FENGSYNC_DISABLE_UPDATE_CHECK'] = '1'
  # Always set the value explicitly so a developer's parent shell cannot make
  # the native scenario accidentally inherit forced software rendering.
  $start.EnvironmentVariables['FENGSYNC_FORCE_SOFTWARE_RENDERING'] = if ($Scenario -eq 'ui-shell-software') { '1' } else { '0' }
  # Start-App returns a two-item array consumed by every scenario.  Do not let
  # its diagnostic text enter that pipeline and shift the process/main indexes.
  $null = Write-HarnessTrace ("Launching Feng Sync with rendering mode: {0}" -f $(if ($Scenario -eq 'ui-shell-software') { 'software' } else { 'native-default' }))
  $p = [Diagnostics.Process]::Start($start)
  $main = Wait-Until {
    if ($p.MainWindowHandle -eq 0) { return $null }
    $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    # A non-zero window handle arrives before WPF has necessarily populated the
    # visual/automation tree. Return only when the actual application shell is
    # available, so all rendering modes use the same reliable readiness gate.
    $profileList = $candidate.FindFirst(
      [System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'ProfileList'))
    if ($profileList) { return $candidate }
    return $null
  } 'Main window shell did not become ready'
  return @($p, $main)
}
function Stop-App {
  param($p)
  if (-not $p) { return }
  try {
    if (-not $p.HasExited) {
      try { $p.Kill($true) } catch { & taskkill.exe /PID $p.Id /T /F *> $null }
      if (-not $p.WaitForExit(10000)) {
        & taskkill.exe /PID $p.Id /T /F *> $null
        if (-not $p.WaitForExit(5000)) { throw "Feng Sync process $($p.Id) did not exit after forced termination." }
      }
    }
  }
  finally { $p.Dispose() }
}
function Assert-File { param([string]$path, [string]$content); if (-not (Test-Path -LiteralPath $path)) { throw "Expected file not found: $path" }; $actual = [IO.File]::ReadAllText($path); if ($actual -ne $content) { throw "Unexpected content in $path. Expected: $content. Actual: $actual" }; Write-Output "File assertion passed: $path; content: $actual" }
function Capture-Window { param($p, [string]$name)
  try {
    if (-not $p -or $p.MainWindowHandle -eq 0) { return }
    $bounds = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle).Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { return }
    $image = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height); $graphics = [Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $image.Size); $image.Save((Join-Path $artifacts $name), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $image.Dispose() }
  } catch { Write-Verbose "Could not capture UI screenshot: $_" }
}
function Set-WindowSize { param($window, [double]$width, [double]$height, [string]$label)
  $windowPattern = $null
  if ($window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) {
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
  }
  $transform = $null
  if (-not $window.TryGetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern, [ref]$transform) -or -not $transform.Current.CanResize) {
    throw "Main window does not support UI Automation resizing for $label."
  }
  $transform.Resize($width, $height)
  Wait-Until {
    $bounds = (Get-LiveMain).Current.BoundingRectangle
    # At non-100% DPI WPF applies the logical minimum size after the physical
    # resize request.  A larger resulting rectangle is valid and still proves
    # the narrowest available layout on that display.
    $bounds.Width -ge ($width - 2) -and $bounds.Height -ge ($height - 2)
  } "Main window did not resize to the $label visual-matrix target." 15 | Out-Null
}
function Assert-RectangleInside { param($element, $container, [string]$label)
  $rect = $element.Current.BoundingRectangle; $outer = $container.Current.BoundingRectangle; $tolerance = 2
  if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "$label has no visible bounds." }
  if ($rect.Left -lt ($outer.Left - $tolerance) -or $rect.Top -lt ($outer.Top - $tolerance) -or $rect.Right -gt ($outer.Right + $tolerance) -or $rect.Bottom -gt ($outer.Bottom + $tolerance)) {
    throw "$label is clipped outside the main-window bounds."
  }
}
function Test-RectanglesOverlap { param($first, $second)
  $a = $first.Current.BoundingRectangle; $b = $second.Current.BoundingRectangle
  return $a.Left -lt $b.Right -and $a.Right -gt $b.Left -and $a.Top -lt $b.Bottom -and $a.Bottom -gt $b.Top
}
function Assert-VisualMatrixGeometry { param($main, [string]$label)
  # WPF does not always expose a plain Border as a UIA peer, so use the
  # explicitly named workspace header and list as the observable sidebar edge.
  $workspaceHeader = Find-Id $main 'ProfileWorkspaceHeader'; $profiles = Find-Id $main 'ProfileList'
  $toolbar = @('CompareButton', 'SyncModeBox', 'EditCurrentProfileButton', 'KeepRightButton', 'KeepLeftButton', 'SyncButton') | ForEach-Object { Find-Id $main $_ }
  # PreviewPanel is a Border and therefore may not expose a UIA peer; its two
  # named text peers give a stable, user-visible clipping assertion instead.
  $content = @('LeftPath', 'RightPath', 'Comparison', 'Summary', 'SafetySummary', 'Status') | ForEach-Object { Find-Id $main $_ }
  @($workspaceHeader, $profiles) + $toolbar + $content | ForEach-Object { Assert-RectangleInside $_ $main "$label/$($_.Current.AutomationId)" }
  foreach ($button in $toolbar) {
    if ($button.Current.BoundingRectangle.Left -lt ($profiles.Current.BoundingRectangle.Right - 2)) { throw "$label/$($button.Current.AutomationId) enters the profile workspace." }
  }
  for ($i = 0; $i -lt $toolbar.Count; $i++) {
    for ($j = $i + 1; $j -lt $toolbar.Count; $j++) {
      if (Test-RectanglesOverlap $toolbar[$i] $toolbar[$j]) { throw "$label toolbar controls overlap: $($toolbar[$i].Current.AutomationId) and $($toolbar[$j].Current.AutomationId)." }
    }
  }
  if ($profiles.Current.BoundingRectangle.Left -ge $toolbar[0].Current.BoundingRectangle.Left) { throw "$label profile list is not left of the toolbar." }
}

$process = $null; $server = $null; $remoteCleanup = $null; $sftpUri = $null; $driveUri = $null; $driveChild = $null; $scheduledTask = $null; $passed = $false
try {
  if ($Scenario -in @('sftp-to-local', 'sftp-ui')) {
    $fixture = Join-Path $root 'sftp'; $share = Join-Path $fixture 'share'; $modules = Join-Path $Workspace '.fengsync-test\sftp-node\node_modules'
    New-Item -ItemType Directory -Force -Path $share | Out-Null
    if (-not (Test-Path (Join-Path $modules 'ssh2\package.json'))) {
      $moduleRoot = Split-Path $modules -Parent; New-Item -ItemType Directory -Force -Path $moduleRoot | Out-Null
      Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package.json') (Join-Path $moduleRoot 'package.json') -Force
      Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package-lock.json') (Join-Path $moduleRoot 'package-lock.json') -Force
      & npm ci --omit=dev --prefix $moduleRoot; if ($LASTEXITCODE -ne 0) { throw 'Unable to install pinned SFTP fixture dependency.' }
    }
    [IO.File]::WriteAllText((Join-Path $share 'remote-proof.txt'), 'from-sftp')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $password = 'ui-sftp-password'; $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16); $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $options = @{ Enabled=$true; ListenAddress='127.0.0.1'; Port=$port; MaxConnections=2; Accounts=@(@{UserName='ui';Enabled=$true;PasswordSalt=[Convert]::ToBase64String($salt);PasswordHash=[Convert]::ToBase64String($hash);PasswordIterations=210000;PublicKeys=@()}); Shares=@(@{VirtualName='docs';PhysicalPath=$share;Permission='ReadWrite'}) }
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{ Options=$options; HostKeyPath=(Join-Path $Workspace '.fengsync-test\real-sftp-host.pem') } | ConvertTo-Json -Depth 8 -Compress)))
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command node -ErrorAction Stop).Source); $start.Arguments = '"' + (Join-Path $Workspace 'src\FengSync.Core\SftpServer\node-sftp-host.cjs') + '" --fengsync-test-run-id ' + $stamp; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.EnvironmentVariables['FENGSYNC_SFTP_CONFIG']=$payload; $start.EnvironmentVariables['NODE_PATH']=$modules
    $server = [Diagnostics.Process]::Start($start); Wait-Until { try { $tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Dispose();$true } catch {$false} } 'SFTP fixture did not start'
    if ($Scenario -eq 'sftp-to-local') { $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'; $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
      & $rclone config create ui_sftp sftp host 127.0.0.1 user ui port "$port" pass $password --config $config; if ($LASTEXITCODE -ne 0) { throw 'Could not configure isolated SFTP remote.' }; $sftpUri='sftp://ui_sftp/docs' }
  }
  if ($Scenario -in @('gdrive', 'gdrive-volume')) {
    # Discover the currently configured Feng Sync Google Drive credential.  The test
    # never guesses a remote name and only creates a random child below this fixed,
    # application-owned test root.
    $sourceConfig = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'FengSync\rclone\rclone.conf'
    if (-not (Test-Path -LiteralPath $sourceConfig)) { Write-Output 'SKIPPED: no Feng Sync rclone configuration was found.'; exit 77 }
    $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $dump = & $rclone config dump --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the current Feng Sync rclone configuration.' }
    $accounts = $dump | ConvertFrom-Json
    $driveRemote = @($accounts.psobject.Properties | Where-Object { $_.Value.type -eq 'drive' } | Select-Object -First 1).Name
    if ([string]::IsNullOrWhiteSpace($driveRemote)) { Write-Output 'SKIPPED: no Google Drive credential is configured in Feng Sync.'; exit 77 }
    # The configured credential is allowed to be exercised only below its test
    # directory. The generated child prevents this test from touching user files.
    $driveUri = "gdrive://$driveRemote/test/FengSync-Automated-Tests"
    # The durable test root is intentionally retained. Only the generated child
    # beneath it is test data and will be purged in finally.
    & $rclone mkdir "${driveRemote}:test" --config $sourceConfig --contimeout 10s --timeout 30s
    if ($LASTEXITCODE -ne 0) { throw "Google Drive credential '$driveRemote' cannot create or access its required text test root." }
    $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null; Copy-Item -LiteralPath $sourceConfig -Destination $config -Force
    $driveChild = 'fengsync-ui-' + [Guid]::NewGuid().ToString('N'); $driveUri = $driveUri.TrimEnd('/') + '/' + $driveChild
    # Feng Sync scans both endpoint roots before planning. Google Drive does not
    # reliably materialize an empty directory, so place one disposable marker in
    # this generated child; the visible UI flow still performs the proof upload.
    & $rclone mkdir "${driveRemote}:test/FengSync-Automated-Tests/$driveChild" --config $sourceConfig --contimeout 10s --timeout 30s
    if ($LASTEXITCODE -ne 0) { throw "Could not create Google Drive test child: $driveUri" }
    $anchor = Join-Path $root '.fengsync-fixture-anchor'; [IO.File]::WriteAllText($anchor, 'fixture')
    & $rclone copyto $anchor "${driveRemote}:test/FengSync-Automated-Tests/$driveChild/.fengsync-fixture-anchor" --config $sourceConfig --contimeout 10s --timeout 30s
    if ($LASTEXITCODE -ne 0) { throw "Could not materialize Google Drive test child: $driveUri" }
    $remoteCleanup = $driveUri
  }
  $launch = Start-App; $process = $launch[0]; $main = $launch[1]
  Capture-Window $process '01-main.png'
  switch ($Scenario) {
    { $_ -in @('ui-shell', 'ui-shell-native', 'ui-shell-software') } {
      $profiles = Find-Id $main 'ProfileList'; $header = Find-Id $main 'ProfileWorkspaceHeader'; $toolbar = Find-Id $main 'CompareButton'
      if ($profiles.Current.BoundingRectangle.Left -ge $toolbar.Current.BoundingRectangle.Left) { throw 'Profile workspace is not left of the toolbar.' }
      if ([Math]::Abs($header.Current.BoundingRectangle.Top - $toolbar.Current.BoundingRectangle.Top) -gt 20) { throw 'Profile workspace and toolbar are not vertically aligned in the shell.' }
      $keepRight = Find-Id $main 'KeepRightButton'; $keepLeft = Find-Id $main 'KeepLeftButton'
      if ($keepRight.Current.BoundingRectangle.Left -ge $keepLeft.Current.BoundingRectangle.Left) { throw 'KeepRightButton must precede KeepLeftButton.' }
      if ($keepRight.Current.Name -ne '右侧覆盖左侧' -or $keepLeft.Current.Name -ne '左侧覆盖右侧') { throw 'Direction button names no longer match their AutomationId.' }
      # Produce a real local comparison instead of accepting the initial placeholder
      # text. This proves SafetySummary is rendered from an actual plan.
      $left = Join-Path $root 'shell-left'; $right = Join-Path $root 'shell-right'; New-Item -ItemType Directory -Force -Path $left, $right | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'safety-proof.txt'), 'plan')
      Compare-Ui $main $left $right
      $summary = Find-Id $main 'Summary'; $safety = Find-Id $main 'SafetySummary'
      if ($summary.Current.BoundingRectangle.Height -le 0 -or $safety.Current.BoundingRectangle.Height -le 0 -or $safety.Current.BoundingRectangle.Top -lt $summary.Current.BoundingRectangle.Bottom) { throw 'Preview summary or safety text was clipped.' }
      if ($safety.Current.Name -notmatch '安全检查') { throw "SafetySummary was not populated by the real comparison: $($safety.Current.Name)" }
      Capture-Window $process '02-shell.png'
      $screenshot = Join-Path $artifacts '02-shell.png'
      if (-not (Test-Path -LiteralPath $screenshot) -or (Get-Item -LiteralPath $screenshot).Length -le 0) { throw 'Shell screenshot was not saved.' }
      if ($main.Current.BoundingRectangle.Width -le 0 -or $main.Current.BoundingRectangle.Height -le 0) { throw 'Main window is not visible after shell rendering.' }
      Write-HarnessTrace ("Shell rendering verified for mode: {0}; screenshot: {1}" -f $(if ($Scenario -eq 'ui-shell-software') { 'software' } else { 'native-default' }), $screenshot)
    }
    'ui-visual-matrix' {
      $sizes = @(
        [pscustomobject]@{ Label = '1040x640'; Width = 1040; Height = 640 },
        [pscustomobject]@{ Label = '1366x768'; Width = 1366; Height = 768 },
        [pscustomobject]@{ Label = '1626x894'; Width = 1626; Height = 894 }
      )
      foreach ($size in $sizes) {
        Set-WindowSize $main $size.Width $size.Height $size.Label
        $main = Get-LiveMain
        Assert-VisualMatrixGeometry $main $size.Label
        Capture-Window $process ("02-visual-{0}.png" -f $size.Label)
        Write-HarnessTrace "Visual matrix verified: $($size.Label)."
      }
      $windowPattern = $null
      if (-not $main.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) { throw 'Main window does not expose WindowPattern for the maximized visual-matrix target.' }
      $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
      Wait-Until { (Get-LiveMain).Current.BoundingRectangle.Width -gt 0 } 'Main window did not maximize.' 15 | Out-Null
      $main = Get-LiveMain
      Assert-VisualMatrixGeometry $main 'maximized'
      Capture-Window $process '02-visual-maximized.png'
      Write-HarnessTrace 'Visual matrix verified: maximized.'
    }
    'update-settings' {
      $profiles = Find-Id $main 'ProfileList'; $beforeWidth = $profiles.Current.BoundingRectangle.Width
      $splitter = Find-Id $main 'SidebarSplitter'; Drag-ElementHorizontally $splitter 75
      Wait-Until { (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width -gt ($beforeWidth + 35) } 'Sidebar width did not change after dragging the splitter.' 15 | Out-Null
      $changedWidth = (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem')
      $settings = Find-WindowLike '设置'
      $auto = Find-Id $settings 'SettingsAutoCheckUpdates'
      if ((Get-ToggleState $auto) -eq [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $auto }
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem'); $settings = Find-WindowLike '设置'
      if ((Get-ToggleState (Find-Id $settings 'SettingsAutoCheckUpdates')) -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Auto update preference did not persist after reopening settings.' }
      Capture-Window $process '02-update-settings.png'; Click (Find-Id $settings 'SettingsCancel')
      Stop-App $process; $process = $null; $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      $restoredWidth = (Find-Id $main 'ProfileList').Current.BoundingRectangle.Width
      if ([Math]::Abs($restoredWidth - $changedWidth) -gt 5) { throw "Sidebar width did not persist after restart. changed=$changedWidth restored=$restoredWidth" }
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem'); $settings = Find-WindowLike '设置'
      if ((Get-ToggleState (Find-Id $settings 'SettingsAutoCheckUpdates')) -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Auto update preference did not persist after restart.' }
      Click (Find-Id $settings 'SettingsCancel')
    }
    'about' {
      Open-Menu (Find-Id $main 'HelpMenu'); Click (Find-Id $main 'AboutMenuItem')
      $about = Wait-Until { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'AboutWindow' } } 'About window did not appear'; $shown = (Find-Id $about 'AboutVersion').Current.Name
      $product = [Diagnostics.FileVersionInfo]::GetVersionInfo($AppPath).ProductVersion
      $releaseProduct = if ($product) { $product.TrimStart('v').Split('+')[0] } else { '' }
      if ([string]::IsNullOrWhiteSpace($releaseProduct) -or $shown -notmatch [Regex]::Escape($releaseProduct)) { throw "About version '$shown' does not match product version '$product'." }
      Find-Id $about 'AboutCheckUpdates' | Out-Null; Capture-Window $process '02-about.png'
      [Windows.Forms.SendKeys]::SendWait('{ESC}')
    }
    'local' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-initial')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'initial.txt') 120 120 'left-initial'; Assert-File (Join-Path $right 'initial.txt') 'left-initial'
      # Establish a true two-way conflict, select its row, choose the right-to-left
      # direction in the visible UI, then prove the left file is the right content.
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-change'); [IO.File]::WriteAllText((Join-Path $right 'initial.txt'), 'right-change')
      Start-Sleep -Milliseconds 2200; Compare-Ui $main $left $right
      $grid = Find-Id $main 'Comparison'; $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
      $row = Wait-Until { $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'No comparison row was shown'
      Select-Ui $row
      Click (Find-Id $main 'KeepRightButton'); Wait-Sync $main (Join-Path $left 'initial.txt') 120 120 'right-change'; Assert-File (Join-Path $left 'initial.txt') 'right-change'
    }
    'modes' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'from-left.txt'), 'left'); [IO.File]::WriteAllText((Join-Path $right 'right-only.txt'), 'preserve')
      Select-Mode $main '更新 →'; Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'from-left.txt'); Assert-File (Join-Path $right 'right-only.txt') 'preserve'
      [IO.File]::WriteAllText((Join-Path $right 'remove-in-mirror.txt'), 'delete')
      Select-Mode $main '镜像 →'; Compare-Ui $main $left $right
      Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror did not create a plan'; Click (Find-Id $main 'SyncButton')
      $confirm = Find-WindowLike '确认同步'; Click (Find-Name $confirm '确认同步')
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'remove-in-mirror.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'right-only.txt')) } 'Mirror did not delete every right-only file' 60
      Assert-File (Join-Path $right 'from-left.txt') 'left'
    }
    'selection' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $excluded = Join-Path $left 'not-selected.txt'; [IO.File]::WriteAllText($excluded, 'must-stay-local')
      Compare-Ui $main $left $right
      $grid = Find-Id $main 'Comparison'
      $row = Wait-Until { $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Comparison row was not exposed.'
      $box = Wait-Until { $items = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)); if ($items.Count -gt 0) { $items[0] } } 'Comparison selection checkbox was not exposed.'
      Toggle-Ui $box
      Wait-Until { -not (Find-Id $main 'SyncButton').Current.IsEnabled } 'Deselecting the only planned file did not disable sync.'
      if (Test-Path -LiteralPath (Join-Path $right 'not-selected.txt')) { throw 'Deselected file was unexpectedly synchronized.' }
    }
    'sftp-to-local' {
      $local = Join-Path $root 'download'; New-Item -ItemType Directory -Force -Path $local | Out-Null
      Select-Mode $main '更新 →'; Compare-Ui $main $sftpUri $local; Wait-Sync $main (Join-Path $local 'remote-proof.txt'); Assert-File (Join-Path $local 'remote-proof.txt') 'from-sftp'
    }
    'sftp-ui' {
      $remoteName = 'ui_sftp_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'CloudEndpointManager')
      $manager = Find-WindowLike '端点管理'; Click (Find-Id $manager 'NewCloudEndpoint')
      $editor = Find-WindowLike '新建云端'; $service = Find-Id $editor 'CloudServiceType'; $expand = $null; $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $service 'SFTP')
      Set-Text (Find-Id $editor 'SftpRemoteName') $remoteName; Set-Text (Find-Id $editor 'SftpRemoteHost') '127.0.0.1'; Set-Text (Find-Id $editor 'SftpRemotePort') "$port"; Set-Text (Find-Id $editor 'SftpRemoteUser') 'ui'; Set-Password (Find-Id $editor 'SftpRemotePassword') $password; Set-Text (Find-Id $editor 'SftpRemoteRoot') 'docs'
      Click (Find-Id $editor 'SaveCloudEndpoint'); Wait-Until { (Find-Id $manager 'CloudEndpointStatus').Current.Name -match '已创建' } 'SFTP endpoint was not created by the UI.' 60
      Click (Find-Id $manager 'AddCloudEndpointRight')
      $upload = Join-Path $root 'ui-upload'; New-Item -ItemType Directory -Force -Path $upload | Out-Null; [IO.File]::WriteAllText((Join-Path $upload 'created-through-ui.txt'), 'endpoint-ui-proof')
      Select-Mode $main '更新 →'; Compare-Ui $main $upload (Get-Text (Find-Id $main 'RightPath')); Wait-Sync $main (Join-Path $share 'created-through-ui.txt'); Assert-File (Join-Path $share 'created-through-ui.txt') 'endpoint-ui-proof'
    }
    'profile' {
      $left = Join-Path $root 'profile-left'; $right = Join-Path $root 'profile-right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $profileName = 'ui-profile-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      # New, then Edit is the same path a user follows from the Profile pane.
      Click (Find-Id $main 'NewProfileButton'); Click (Find-Id $main 'EditProfileButton')
      $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right; Click (Find-Id $editor 'ProfileSave')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已保存' } 'Profile edit did not report a save.'
      # A cancelled edit must not leak into the in-memory UI nor the persisted profile.
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') 'must-not-persist'; Click (Find-Id $editor 'ProfileCancel')
      if ((Find-Id $main 'ProfileList').Current.Name -match 'must-not-persist') { throw 'Cancelled profile edit changed the profile list.' }
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      # Find the item, then select it through the normal keyboard interaction in
      # the Profile pane. The WPF ListBoxItem provider exposes no SelectionItem
      # pattern on this desktop, but End is the equivalent user action here.
      $item = Find-Name (Find-Id $main 'ProfileList') $profileName
      (Find-Id $main 'ProfileList').SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Wait-Until { (Get-Text (Find-Id $main 'LeftPath')) -eq $left } 'Persisted Profile did not load after selecting it.'
      if ((Get-Text (Find-Id $main 'LeftPath')) -ne $left -or (Get-Text (Find-Id $main 'RightPath')) -ne $right) { throw 'Persisted Profile endpoints did not survive application restart.' }
      Click (Find-Id $main 'DeleteProfileButton')
      $removeDialog = Find-WindowLike '移除配置'; [Windows.Forms.SendKeys]::SendWait('%y')
      Wait-Until { try { -not (Find-Name (Find-Id $main 'ProfileList') $profileName 1) } catch { $true } } 'Deleted Profile remained in the list.'
    }
    'profile-filter' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right, (Join-Path $left '.git')) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'included.txt'), 'included'); [IO.File]::WriteAllText((Join-Path $left '.git\config'), 'must-be-filtered')
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{DOWN}')
      Click (Find-Name $editor '常用排除'); Click (Find-Id $editor 'ProfileSave')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'included.txt'); Assert-File (Join-Path $right 'included.txt') 'included'
      if (Test-Path -LiteralPath (Join-Path $right '.git\config')) { throw 'Profile filter did not exclude .git content from the real sync.' }
    }
    'delete-threshold' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'delete-a.txt'), 'a'); [IO.File]::WriteAllText((Join-Path $right 'delete-b.txt'), 'b')
      $profileName = 'threshold-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Set-Text (Find-Id $editor 'ProfileMaxDeletes') '0'; Set-Text (Find-Id $editor 'ProfileMaxDeleteRatio') '0'; Click (Find-Id $editor 'ProfileSave')
      Select-Mode $main '镜像 →'; Compare-Ui $main $left $right; Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror threshold plan was not executable.'
      Click (Find-Id $main 'SyncButton'); $confirm = Find-WindowLike '确认同步'; Set-Text (Find-Id $confirm 'ProfileNameInput') $profileName; Click (Find-Id $confirm 'ConfirmSyncButton')
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'delete-a.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'delete-b.txt')) } 'Profile-name threshold confirmation did not permit mirror deletion.' 60
    }
    'settings' {
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem')
      $settings = Find-WindowLike '设置'; Select-Ui (Find-Id $settings 'SettingsDefaultsTab')
      Set-Text (Find-Id $settings 'SettingsConcurrency') '3'; Set-Text (Find-Id $settings 'SettingsTimeTolerance') '7'; Set-Text (Find-Id $settings 'SettingsIncludeRules') '**/*.keep'; Set-Text (Find-Id $settings 'SettingsExcludeRules') '**/*.skip'
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已应用' } 'Settings did not report apply.'
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem'); $settings = Find-WindowLike '设置'; Select-Ui (Find-Id $settings 'SettingsDefaultsTab')
      if ((Get-Text (Find-Id $settings 'SettingsConcurrency')) -ne '3' -or (Get-Text (Find-Id $settings 'SettingsTimeTolerance')) -ne '7' -or (Get-Text (Find-Id $settings 'SettingsIncludeRules')) -ne '**/*.keep' -or (Get-Text (Find-Id $settings 'SettingsExcludeRules')) -ne '**/*.skip') { throw 'Applied application defaults were not persisted on re-open.' }
      Click (Find-Id $settings 'SettingsCancel')
    }
    'history' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'recorded.txt'), 'history-proof'); Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'recorded.txt')
      Open-Menu (Find-Id $main 'OperationsMenu'); Click (Find-Id $main 'RunHistoryMenuItem'); $history = Find-WindowLike '历史'
      Click (Find-Id $history 'RefreshRunHistory')
      $entry = Wait-Until { $items = (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Run history did not show the real completed run.' 30
      $outcome = Find-Id $history 'RunHistoryOutcome'; $expand = $null; $outcome.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $outcome '成功')
      Wait-Until { (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)).Count -gt 0 } 'Successful-outcome filter hid the completed run.'
      $history.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{ESC}')
    }
    'schedule' {
      $scheduledTask = 'FengSync-Test-' + [Guid]::NewGuid().ToString('N')
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'ScheduleMenuItem'); $schedule = Find-WindowLike '计划任务'
      Set-Text (Find-Id $schedule 'ScheduleTaskName') $scheduledTask
      Click (Find-Id $schedule 'CreateScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -eq 0 } 'Schedule UI did not create the unique Windows task.' 30
      Click (Find-Id $schedule 'TestScheduleButton')
      Wait-Until { try { (Find-Id $schedule 'ResultText').Current.Name -match '测试运行|请求' } catch { $true } } 'Schedule UI did not request a test run.' 30
      # schtasks may refuse deletion while its just-requested action is still
      # starting. Wait for the short CLI invocation to leave Running state.
      Wait-Until { $state = & schtasks.exe /Query /TN $scheduledTask /FO LIST 2>$null; $LASTEXITCODE -eq 0 -and ($state -notmatch 'Running|正在运行') } 'Scheduled test run did not leave the running state.' 60
      Click (Find-Id $schedule 'DeleteScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -ne 0 } 'Schedule UI did not delete the unique Windows task.' 30
      $scheduledTask = $null
    }
    'gdrive' {
      $upload = Join-Path $root 'drive-upload'; $download = Join-Path $root 'drive-download'; New-Item -ItemType Directory -Force -Path @($upload, $download) | Out-Null
      [IO.File]::WriteAllText((Join-Path $upload 'drive-proof.txt'), 'drive-roundtrip')
      Select-Mode $main '更新 →'; Compare-Ui $main $upload $driveUri; Wait-Sync $main (Join-Path $upload 'drive-proof.txt')
      Wait-Until {
        $listing = & $rclone lsf "${driveRemote}:test/FengSync-Automated-Tests/$driveChild" --config $config --contimeout 10s --timeout 30s 2>$null
        $LASTEXITCODE -eq 0 -and $listing -contains 'drive-proof.txt'
      } 'UI upload did not become visible in the generated Google Drive test child.' 120
      # Start a second real application session for the download. This models the
      # common upload-then-later-download workflow and avoids reusing a Drive RC
      # daemon while its previous request is still unwinding.
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      Select-Mode $main '更新 →'
      Compare-Ui $main $driveUri $download
      # Drive's second scan is performed again immediately before execution; allow
      # its bounded 2-minute RC request plus transfer time on slow Google APIs.
      Wait-Sync $main (Join-Path $download 'drive-proof.txt') 180 300
      Assert-File (Join-Path $download 'drive-proof.txt') 'drive-roundtrip'
    }
    'gdrive-volume' {
      # This is deliberately a measurement test rather than a fixed time budget:
      # Google API latency varies, while the emitted timings make file-count
      # regressions visible without turning a healthy remote run into a flaky test.
      $results = [System.Collections.Generic.List[object]]::new()
      foreach ($mode in @(
        [pscustomobject]@{ Name = '双向 ↔'; Slug = 'two-way' },
        [pscustomobject]@{ Name = '镜像 →'; Slug = 'mirror' },
        [pscustomobject]@{ Name = '更新 →'; Slug = 'update' }
      )) {
        foreach ($workload in @(
          [pscustomobject]@{ Slug = 'flat-10-files'; Files = 10; Create = { param($path) New-SmallFiles $path 10 }; Expected = 'batch-001.txt' },
          [pscustomobject]@{ Slug = 'flat-100-files'; Files = 100; Create = { param($path) New-SmallFiles $path 100 }; Expected = 'batch-001.txt' },
          [pscustomobject]@{ Slug = '100-folders-one-file-each'; Files = 100; Create = { param($path) New-FolderFiles $path 100 }; Expected = 'folder-001/item.txt' }
        )) {
          $case = "$($mode.Slug)-$($workload.Slug)"
          $upload = Join-Path $root (Join-Path 'drive-volume' $case)
          $remoteCase = $driveUri.TrimEnd('/') + '/' + $case
          # Google Drive does not preserve an empty directory. Materialize each
          # isolated case so both comparison roots exist before the UI scans them.
          & $rclone mkdir ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") --config $config --contimeout 10s --timeout 30s
          if ($LASTEXITCODE -ne 0) { throw "Could not create Google Drive performance test child: $remoteCase" }
          $anchor = Join-Path $root ("$case-anchor"); [IO.File]::WriteAllText($anchor, 'fixture')
          & $rclone copyto $anchor ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case/.fengsync-fixture-anchor") --config $config --contimeout 10s --timeout 30s
          if ($LASTEXITCODE -ne 0) { throw "Could not materialize Google Drive performance test child: $remoteCase" }
          & ($workload.Create) $upload
          # Each workload gets a new app/RC daemon. Reusing the completed session can leave
          # an old comparison plan enabled and cause the next visible comparison to never run.
          Stop-App $process; $process = $null
          $launch = Start-App; $process = $launch[0]; $main = $launch[1]
          $usesHighConcurrency = $workload.Files -eq 100
          if ($usesHighConcurrency) {
            # Set the endpoints first so the profile gear edits the values visible
            # on the main window. The following measured comparison then proves the
            # saved 10-way setting is the one actually used for synchronization.
            Set-Text (Find-Id $main 'LeftPath') $upload
            Set-Text (Find-Id $main 'RightPath') $remoteCase
            Set-ProfileConcurrency $main 10
          }
          Select-Mode $main $mode.Name
          # Completion is driven by the application's operation boundary. This
          # longer value is only an emergency brake for a genuinely stuck remote
          # request, not a delay used to advance the matrix.
          $timing = Invoke-MeasuredSync $main $upload $remoteCase (Join-Path $upload $workload.Expected) 180 1800
          if ($usesHighConcurrency) { Set-ProfileConcurrency $main 3 }
          Assert-GoogleDriveFileCount ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") $workload.Files
          $results.Add([pscustomobject]@{ Mode = $mode.Slug; Workload = $workload.Slug; Files = $workload.Files; CompareMilliseconds = $timing.CompareMilliseconds; SyncMilliseconds = $timing.SyncMilliseconds })
          Write-Output ("Google Drive performance: mode={0}, workload={1}, files={2}, compare={3}ms, sync={4}ms" -f $mode.Slug, $workload.Slug, $workload.Files, $timing.CompareMilliseconds, $timing.SyncMilliseconds)
        }
      }
      if ($results.Count -ne 9) { throw 'Google Drive performance matrix did not complete every mode and workload combination.' }
    }
  }
  Capture-Window $process '99-complete.png'
  $passed = $true
  $scenarioTimer.Stop()
  Write-HarnessTrace ("Passed {0}; completed in {1}" -f $Scenario, $scenarioTimer.Elapsed)
}
catch {
  Write-HarnessTrace ("Failed {0}: {1}" -f $Scenario, $_.Exception.Message)
  throw
}
finally {
  # Closing the application is the first failure-path action. Diagnostics and
  # fixture cleanup must never delay or prevent the next UI scenario.
  Stop-App $process
  Write-HarnessTrace "Cleaning up scenario: $Scenario"
  if ($remoteCleanup) {
    # The URI was validated as a child of the caller-provided dedicated test root.
    $parts = $remoteCleanup.Substring('gdrive://'.Length).Split('/',2); $config = Join-Path $appData 'rclone\rclone.conf'; $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    if ($parts.Count -eq 2) { & $rclone purge ($parts[0] + ':' + $parts[1]) --config $config --contimeout 10s --timeout 30s; if ($LASTEXITCODE -ne 0) { Write-Error "Google Drive cleanup failed for generated child $remoteCleanup" } }
  }
  if ($server) {
    try {
      if (-not $server.HasExited) {
        try { $server.Kill($true) } catch { & taskkill.exe /PID $server.Id /T /F *> $null }
        if (-not $server.WaitForExit(5000)) {
          & taskkill.exe /PID $server.Id /T /F *> $null
          if (-not $server.WaitForExit(5000)) { Write-Warning "SFTP fixture process $($server.Id) did not exit after forced termination." }
        }
      }
    }
    finally { $server.Dispose() }
  }
  if ($scheduledTask) { & schtasks.exe /Delete /F /TN $scheduledTask *> $null }
  if (Test-Path -LiteralPath $root) { Write-Output "Artifacts retained: $root" }
}
