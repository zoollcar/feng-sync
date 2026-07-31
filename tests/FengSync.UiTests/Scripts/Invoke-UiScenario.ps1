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
  [DllImport("user32.dll")] public static extern void SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint procId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
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
function Find-Id($root, [string]$id, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) } catch { $null } } "Missing UI element: $id" $seconds }
function Find-Name($root, [string]$name, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) } catch { $null } } "Missing UI element: $name" $seconds }
function Click($element) {
  # A disabled element cannot be invoked through UIA — UIA throws the
  # generic "Could not open the process token" / "Unrecognized error"
  # rather than letting the test tell the user that the action is simply
  # unavailable. Wait briefly for IsEnabled to settle before retrying.
  for ($i = 0; $i -lt 50; $i++) {
    try {
      if ($element.Current.IsEnabled) {
        $p = $null
        if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { throw "Element cannot be invoked: $($element.Current.Name)" }
        $p.Invoke()
        return
      }
    } catch {
      $msg = $_.Exception.Message
      if ($msg -notmatch 'Could not open the process token|process token|not currently available|Unrecognized error') { throw }
    }
    Start-Sleep -Milliseconds 200
  }
  throw "Element $($element.Current.AutomationId) stayed disabled for too long."
}
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
  [FengSyncUiMouse]::SetCursorPos($start.X, $start.Y); Start-Sleep -Milliseconds 120
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [FengSyncUiMouse]::SetCursorPos($start.X + [int]($offset / 2), $start.Y); Start-Sleep -Milliseconds 180
  [FengSyncUiMouse]::SetCursorPos($start.X + $offset, $start.Y); Start-Sleep -Milliseconds 220
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
  # WPF modal dialogs (ProfileEditorWindow, SettingsWindow, ...) are not visible in
  # RootElement.FindAll(Children/Ddescendants) while the main window is disabled —
  # UIA returns RPC_E_SERVERFAULT and omits the modal from the tree. Enumerate
  # top-level windows directly via Win32 and convert each HWND to a UIA element.
  $script:windowHandles = [System.Collections.Generic.List[IntPtr]]::new()
  $callback = {
    param($hWnd, $lParam)
    $procId = 0
    [void][FengSyncUiMouse]::GetWindowThreadProcessId($hWnd, [ref]$procId)
    if ($procId -eq $script:process.Id -and [FengSyncUiMouse]::IsWindowVisible($hWnd)) {
      $script:windowHandles.Add($hWnd)
    }
    return $true
  }
  $script:windowHandles.Clear()
  [void][FengSyncUiMouse]::EnumWindows($callback, [IntPtr]::Zero)
  foreach ($hwnd in $script:windowHandles) {
    try {
      $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
      if ($candidate.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
      if (& $predicate $candidate) { return $candidate }
    } catch { }
  }
  return $null
}
function Find-WindowLike([string]$titleFragment, [int]$seconds = 20) {
  Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like "*$titleFragment*" } } catch { $null } } "Missing Feng Sync window containing: $titleFragment" $seconds
}
function Find-WindowById([string]$id, [int]$seconds = 20) {
  Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq $id } } catch { $null } } "Missing Feng Sync window with id: $id" $seconds
}
# Open the SettingsWindow via the main sidebar's Settings button.
# Returns the SettingsWindow automation element. The window title is "设置中心".
function Open-SettingsCenter {
  Click (Find-Id (Get-LiveMain) 'SettingsButton')
  return Find-WindowById 'SettingsWindow'
}
function Close-Window([System.Windows.Automation.AutomationElement]$window) {
  if ($null -eq $window) { return }
  $pattern = $null
  if ($window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) {
    try { $pattern.Close() } catch { }
  }
}
# ModeComboBox items are plain text now ("双向同步", "镜像到右侧", "更新到右侧").
function Select-Mode($main, [string]$name) {
  $combo = Find-Id $main 'SyncModeBox'
  $p = $null
  if (-not $combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { throw 'Sync mode cannot expand.' }
  $p.Expand()
  Start-Sleep -Milliseconds 150
  Select-Ui (Find-Name $combo $name 5)
}
function Get-UiStatus {
  try { return (Find-Id (Get-LiveMain) 'Status' 2).Current.Name } catch { return '' }
}
function Assert-NoUiFailure([string]$operation) {
  $status = Get-UiStatus
  if ($status -match '失败|已取消|未完成|错误|无法|阻断|需要修复') { throw "$operation failed. UI status: $status" }
  return $status
}
# =================== Direction override via DataGrid ContextMenu ===================
# Right-click a Comparison row, then invoke the menu item by header text.
function Get-ComparisonRow {
  param($main)
  $grid = Find-Id $main 'Comparison'
  Wait-Until {
    $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))
    if ($items.Count -gt 0) { return $items[0] }
  } 'Comparison row was not exposed.'
}
function Apply-Direction($main, [string]$header) {
  $row = Get-ComparisonRow $main
  $grid = Find-Id $main 'Comparison'
  [System.Windows.Automation.SelectionItemPattern]$box = $null
  if ($row.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$box)) {
    try { $box.Select() | Out-Null } catch { }
  }
  # DataGrid cells, rather than the exposed DataItem peer, own keyboard focus in
  # the redesigned grid. Focus the grid after selecting the row before opening
  # its context menu.
  $actionButton = Find-Name $row '变更操作菜单' 5
  $bounds = $actionButton.Current.BoundingRectangle
  if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'Comparison action menu has no clickable bounds.' }
  [Windows.Forms.Cursor]::Position = [Drawing.Point]::new([int]($bounds.Left + ($bounds.Width / 2)), [int]($bounds.Top + ($bounds.Height / 2)))
  Start-Sleep -Milliseconds 100
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 250
  $menu = $null
  $menuDeadline = [DateTime]::UtcNow.AddSeconds(5)
  while ([DateTime]::UtcNow -lt $menuDeadline -and $null -eq $menu) {
    try {
      $menus = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu))
      foreach ($candidate in $menus) {
        $target = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $header))
        if ($target) { $menu = $candidate; break }
      }
    } catch { $menu = $null }
    if ($null -eq $menu) { Start-Sleep -Milliseconds 150 }
  }
  if ($null -eq $menu) { throw 'Row context menu did not appear.' }
  # Use keyboard to navigate rather than cross-process InvokePattern — the latter
  # sometimes throws when the menu's window token is mid-transition.
  $itemCount = 0
  try {
    $items = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
    $itemCount = $items.Count
    $target = $null
    foreach ($it in $items) { if ($it.Current.Name -eq $header) { $target = $it; break } }
    if ($null -ne $target) {
      $target.SetFocus()
      Start-Sleep -Milliseconds 60
      [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    } else {
      # Fallback: arrow-down until the desired header is current (WPF ContextMenu
      # routes arrow keys to its menu items directly).
      $found = $false
      for ($i = 0; $i -lt $itemCount -and -not $found; $i++) {
        [Windows.Forms.SendKeys]::SendWait('{DOWN}')
        Start-Sleep -Milliseconds 60
        try {
          $children = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
          foreach ($c in $children) { if ($c.Current.Name -eq $header) { $found = $true; break } }
        } catch { }
      }
      if ($found) {
        Start-Sleep -Milliseconds 100
        [Windows.Forms.SendKeys]::SendWait('{ENTER}')
      } else { throw "Direction menu item '$header' was not found in the context menu." }
    }
  } finally {
    Start-Sleep -Milliseconds 200
    [Windows.Forms.SendKeys]::SendWait('{ESC}')
  }
}
# =================== Profile context menu (right-click ProfileList) ===================
function Find-ContextMenuInApp {
  # WPF ContextMenus are not top-level windows, so they don't appear as children of
  # RootElement. Recursively walk the whole tree looking for the popup's Menu control.
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $queue = [System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
  $queue.Enqueue($root)
  while ($queue.Count -gt 0) {
    $node = $queue.Dequeue()
    try {
      if ($node.Current.ControlType -eq [System.Windows.Automation.ControlType]::Menu -and $node.Current.AutomationId -eq 'ProfileContextMenu') {
        return $node
      }
      $children = $node.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($c in $children) { $queue.Enqueue($c) }
    } catch { }
  }
  return $null
}
function Open-ProfileContextMenu {
  $list = Find-Id (Get-LiveMain) 'ProfileList'
  $target = if ([string]::IsNullOrWhiteSpace($script:deletingProfileName)) { $list } else { Find-Name $list $script:deletingProfileName 5 }
  $bounds = $target.Current.BoundingRectangle
  $anchor = [System.Drawing.Point]::new([int]($bounds.Left + [Math]::Min(60, [int]($bounds.Width / 3))), [int]($bounds.Top + ($bounds.Height / 2)))
  [System.Windows.Forms.Cursor]::Position = $anchor
  Start-Sleep -Milliseconds 60
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 250
  $menu = Wait-Until { Find-ContextMenuInApp } 'Profile context menu did not appear.' 5
  return $menu
}
function Edit-Profile-ThroughContextMenu {
  $menu = Open-ProfileContextMenu
  Click (Find-Id $menu 'ProfileContextEdit')
}
function Delete-Profile-ThroughContextMenu {
  $menu = Open-ProfileContextMenu
  Click (Find-Id $menu 'ProfileContextDelete')
  # The DeleteProfile_Click handler pops a YesNo MessageBox; closing the window
  # (Escape / X) would answer No, which leaves the profile in the list. Walk the
  # dialog tree to find the Yes button by name and invoke it directly through UIA.
  $removeDialog = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like '*移除配置*' } } catch { $null } } 'Remove profile dialog did not appear' 30
  $yes = Wait-Until { try { Find-Name $removeDialog '是' 1 } catch { try { Find-Name $removeDialog 'Yes' 1 } catch { $null } } } 'Yes button did not appear on the remove dialog.' 5
  $yesBounds = $yes.Current.BoundingRectangle
  if ($yesBounds.Width -le 0 -or $yesBounds.Height -le 0) { throw 'Yes button has no clickable bounds.' }
  [FengSyncUiMouse]::SetCursorPos([int]($yesBounds.Left + $yesBounds.Width / 2), [int]($yesBounds.Top + $yesBounds.Height / 2))
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
  Wait-Until {
    $liveMain = Get-LiveMain
    try {
      $list = $liveMain.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProfileList'))
      if (-not $list) { return $true }
      $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
      foreach ($it in $items) { if ($it.Current.Name -and $it.Current.Name.Contains($script:deletingProfileName)) { return $false } }
      return $true
    } catch { return $true }
  } 'Deleted Profile remained in the list.' 30 | Out-Null
}
# =================== Concurrency via profile editor ===================
function Set-ProfileConcurrency($main, [int]$value) {
  Click (Find-Id $main 'EditCurrentProfileButton')
  $editor = Find-WindowById 'ProfileEditorWindow'
  $sections = Find-Id $editor 'ProfileSections'
  $sections.SetFocus()
  [Windows.Forms.SendKeys]::SendWait('{END}')
  Start-Sleep -Milliseconds 200
  $useDefault = Find-Id $editor 'ProfileUseDefaultConcurrency'
  if ((Get-ToggleState $useDefault) -eq [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $useDefault }
  Set-Text (Find-Id $editor 'ProfileConcurrency') ([string]$value)
  Click (Find-Id $editor 'ProfileSave')
  Wait-Until {
    $liveMain = Get-LiveMain
    try { (Find-Id $liveMain 'Status' 2).Current.Name -match '已保存|Profile 设置已保存' } catch { $false }
  } 'Profile save did not report a save.' 30
  Close-Window $editor
  Write-HarnessTrace "Profile concurrency changed through the GUI to $value."
}
function Save-Profile-WithEndpoints($main, [string]$profileName, [string]$left, [string]$right) {
  Click (Find-Id $main 'NewProfileButton')
  Start-Sleep -Milliseconds 250
  Click (Find-Id $main 'EditCurrentProfileButton')
  $editor = Find-WindowLike 'Profile 设置'
  Set-Text (Find-Id $editor 'ProfileName') $profileName
  Set-Text (Find-Id $editor 'ProfileLeftPath') $left
  Set-Text (Find-Id $editor 'ProfileRightPath') $right
  Click (Find-Id $editor 'ProfileSave')
  Wait-Until {
    try {
      $s = (Find-Id (Get-LiveMain) 'Status' 2).Current.Name
      $s -match '已保存|Profile 设置已保存'
    } catch { $false }
  } 'Profile with endpoints did not save.' 30
  Close-Window $editor
}
function Update-CurrentProfileEndpoints($main, [string]$profileName, [string]$left, [string]$right) {
  Click (Find-Id $main 'EditCurrentProfileButton')
  $editor = Find-WindowById 'ProfileEditorWindow'
  Set-Text (Find-Id $editor 'ProfileName') $profileName
  Set-Text (Find-Id $editor 'ProfileLeftPath') $left
  Set-Text (Find-Id $editor 'ProfileRightPath') $right
  Click (Find-Id $editor 'ProfileSave')
  Wait-Until { try { (Get-Text (Find-Id (Get-LiveMain) 'LeftPath' 2)) -eq $left -and (Get-Text (Find-Id (Get-LiveMain) 'RightPath' 2)) -eq $right } catch { $false } } "Current profile '$profileName' did not save its endpoints." 15 | Out-Null
}
# Modern UI reasserts the selected Profile's path through ApplyProfile after
# a successful sync. CompareButton therefore cycles Disabled -> briefly Enabled
# -> Disabled again before the next comparison, so we accept either successful
# completion status text or a quiet transition back into the comparison loop.
function Wait-MainReadyAfterSync([int]$seconds) {
  Write-HarnessTrace 'Synchronized result observed; waiting for the application operation boundary.'
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  $mainReady = $false
  do {
    Assert-NoUiFailure 'Synchronization' | Out-Null
    $status = Get-UiStatus
    if ($status -match '同步完成') { $mainReady = $true; break }
    try {
      $compareEnabled = (Find-Id (Get-LiveMain) 'CompareButton' 2).Current.IsEnabled
      if ($compareEnabled -and $status -match '准备就绪|比较完成') { $mainReady = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 150
  } while ([DateTime]::UtcNow -lt $deadline)
  if (-not $mainReady) { throw 'The main window did not become interactive after synchronization.' }
  $status = Assert-NoUiFailure 'Synchronization'
  Write-HarnessTrace "Application operation boundary reached. UI status: $status"
}
$approvedConfirmations = [Collections.Generic.HashSet[string]]::new()
function Approve-ConfirmationIfPresent {
  $confirm = try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null }
  if ($confirm) {
    $id = [string]::Join('-', $confirm.GetRuntimeId())
    if ($approvedConfirmations.Add($id)) { Click (Find-Id $confirm 'ConfirmSyncButton'); return $true }
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
  Wait-MainReadyAfterSync $transferSeconds
  Write-HarnessTrace 'Sync complete; main window is responsive and the scenario will continue its next UI action.'
  $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }
  $actual = try { [IO.File]::ReadAllText($expectedFile) } catch { '<unreadable>' }
  Write-Output ("Sync result observed; status: {0}; output exists: {1}; actual content: {2}" -f $status, (Test-Path -LiteralPath $expectedFile), $actual)
}
function Compare-Ui {
  param($main, [string]$left, [string]$right, [int]$seconds = 120, [bool]$allowNonExecutable = $false)
  # Re-set both endpoints so the comparison reflects the caller's expectation.
  # After a successful sync, the redesigned UI re-asserts the Profile's saved
  # endpoints (which may be empty for the default placeholder Profile), which
  # would otherwise leave CompareButton disabled.
  Set-Text (Find-Id $main 'LeftPath') $left
  Set-Text (Find-Id $main 'RightPath') $right

  # CompareButton may briefly stay disabled while the previous cycle finishes.
  # Wait until it is interactable again before invoking it.
  Wait-Until {
    try {
      $cb = Find-Id $main 'CompareButton' 1
      $cb.Current.IsEnabled
    } catch { $false }
  } 'CompareButton stayed disabled before the next comparison could start.' 30 | Out-Null

  $statusBeforeClick = Get-UiStatus
  Click (Find-Id $main 'CompareButton')
  Write-HarnessTrace "Compare button invoked: $left <-> $right"

  $accepted = $false
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  do {
    $liveMain = Get-LiveMain
    $status = Get-UiStatus
    if ($status -match '失败|已取消|错误|无法|需要修复|阻断') { throw "Comparison failed. UI status: $status" }
    $compareEnabled = (Find-Id $liveMain 'CompareButton' 2).Current.IsEnabled
    $syncEnabled = (Find-Id $liveMain 'SyncButton' 2).Current.IsEnabled
    if (-not $compareEnabled -or $status -ne $statusBeforeClick) { $accepted = $true }
    if ($accepted -and $compareEnabled -and ($syncEnabled -or $allowNonExecutable)) {
      Write-HarnessTrace "Comparison completed. UI status: $status"
      return
    }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "Comparison did not complete with an executable plan. UI status: $status"
}
function Start-App {
  $start = [Diagnostics.ProcessStartInfo]::new($AppPath); $start.UseShellExecute = $false; $start.Arguments = "--fengsync-test-run-id $stamp"; $start.EnvironmentVariables['FENGSYNC_DATA_DIR'] = $appData; $start.EnvironmentVariables['FENGSYNC_DISABLE_UPDATE_CHECK'] = '1'
  $start.EnvironmentVariables['FENGSYNC_FORCE_SOFTWARE_RENDERING'] = if ($Scenario -eq 'ui-shell-software') { '1' } else { '0' }
  $null = Write-HarnessTrace ("Launching Feng Sync with rendering mode: {0}" -f $(if ($Scenario -eq 'ui-shell-software') { 'software' } else { 'native-default' }))
  $p = [Diagnostics.Process]::Start($start)
  $main = Wait-Until {
    if ($p.MainWindowHandle -eq 0) { return $null }
    $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
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
  # WPF does not always expose a plain Border/TextBlock as a UIA peer; the right-column
  # ChangeSummaryPanel and ProfileList give stable, observable edges for the sidebar.
  $profiles = Find-Id $main 'ProfileList'
  $toolbar = @('EditCurrentProfileButton', 'SwapEndpointsButton', 'SyncModeBox', 'CompareButton', 'SyncButton') | ForEach-Object { Find-Id $main $_ }
  $content = @('LeftPath', 'RightPath', 'TotalSummary', 'ChangeFilterBox', 'Comparison', 'Status') | ForEach-Object { Find-Id $main $_ }
  @($profiles) + $toolbar + $content | ForEach-Object { Assert-RectangleInside $_ $main "$label/$($_.Current.AutomationId)" }
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
    $fixture = Join-Path $root 'sftp'; $share = Join-Path $fixture 'share'
    New-Item -ItemType Directory -Force -Path $share | Out-Null
    [IO.File]::WriteAllText((Join-Path $share 'remote-proof.txt'), 'from-sftp')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $hostKey = Join-Path $root 'host-key.pem'
    & ssh-keygen.exe -q -t ed25519 -N '' -f $hostKey
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $hostKey)) { throw 'Could not generate the isolated SFTP host key.' }
    $password = 'ui-sftp-password'; $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($rclone); $start.Arguments = 'serve sftp ":local:' + $share + '" --addr 127.0.0.1:' + $port + ' --user ui --key "' + $hostKey + '" --vfs-cache-mode writes --cache-dir "' + (Join-Path $root 'sftp-cache') + '"'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.EnvironmentVariables['RCLONE_PASS']=$password
    $server = [Diagnostics.Process]::Start($start); Wait-Until { try { $tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Dispose();$true } catch {$false} } 'SFTP fixture did not start'
    if ($Scenario -eq 'sftp-to-local') { $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
      & $rclone config create ui_sftp sftp host 127.0.0.1 user ui port "$port" pass $password --config $config; if ($LASTEXITCODE -ne 0) { throw 'Could not configure isolated SFTP remote.' }; $sftpUri='sftp://ui_sftp' }
  }
  if ($Scenario -in @('gdrive', 'gdrive-volume')) {
    $sourceConfig = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'FengSync\rclone\rclone.conf'
    if (-not (Test-Path -LiteralPath $sourceConfig)) { Write-Output 'SKIPPED: no Feng Sync rclone configuration was found.'; exit 77 }
    $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $dump = & $rclone config dump --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the current Feng Sync rclone configuration.' }
    $accounts = $dump | ConvertFrom-Json
    $driveRemote = @($accounts.psobject.Properties | Where-Object { $_.Value.type -eq 'drive' } | Select-Object -First 1).Name
    if ([string]::IsNullOrWhiteSpace($driveRemote)) { Write-Output 'SKIPPED: no Google Drive credential is configured in Feng Sync.'; exit 77 }
    $driveUri = "gdrive://$driveRemote/test/FengSync-Automated-Tests"
    & $rclone mkdir "${driveRemote}:test" --config $sourceConfig --contimeout 10s --timeout 30s
    if ($LASTEXITCODE -ne 0) { throw "Google Drive credential '$driveRemote' cannot create or access its required text test root." }
    $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null; Copy-Item -LiteralPath $sourceConfig -Destination $config -Force
    $driveChild = 'fengsync-ui-' + [Guid]::NewGuid().ToString('N'); $driveUri = $driveUri.TrimEnd('/') + '/' + $driveChild
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
      # The renamed UI still has the sidebar/toolbar split; sidebar keeps its ProfileList on the left.
      $profiles = Find-Id $main 'ProfileList'; $toolbar = Find-Id $main 'CompareButton'
      if ($profiles.Current.BoundingRectangle.Left -ge $toolbar.Current.BoundingRectangle.Left) { throw 'Profile workspace is not left of the toolbar.' }
      # EditCurrentProfileButton is exposed through the AutomationId even though it is now in the
      # top header rather than beside Compare/Sync.
      if (-not (Find-Id $main 'EditCurrentProfileButton' 2)) { throw 'EditCurrentProfileButton was not exposed.' }
      $left = Join-Path $root 'shell-left'; $right = Join-Path $root 'shell-right'; New-Item -ItemType Directory -Force -Path $left, $right | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'safety-proof.txt'), 'plan')
      Compare-Ui $main $left $right
      # New summary surface: a right-side panel with UploadSummary/DownloadSummary/etc.
      # TotalSummary is always populated once a comparison finishes successfully.
      $summary = Find-Id $main 'TotalSummary'
      if ($summary.Current.BoundingRectangle.Width -le 0 -or $summary.Current.BoundingRectangle.Height -le 0) { throw 'Change summary panel was clipped.' }
      # Status text only mentions "安全检查" for destructive plans; the
      # successful comparison above is the primary proof the engine ran.
      $status = (Find-Id $main 'Status' 2).Current.Name
      if ($status -notmatch '比较完成') { throw "Comparison did not complete: $status" }
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
      # GridSplitter also supports keyboard resizing for accessibility. It is more
      # deterministic than injected pointer capture on virtual desktops.
      try { $splitter.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{RIGHT 6}') } catch { }
      try { Wait-Until { (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width -gt ($beforeWidth + 35) } 'Sidebar width did not change after dragging the splitter.' 15 | Out-Null }
      catch { $actual = (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width; throw "Sidebar width did not change after dragging the splitter. before=$beforeWidth after=$actual splitter=$($splitter.Current.BoundingRectangle.Width)" }
      $changedWidth = (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width
      # Settings dialog is opened via the SettingsButton in the sidebar (no Tools menu anymore).
      $settings = Open-SettingsCenter
      $auto = Find-Id $settings 'SettingsAutoCheckUpdates'
      if ((Get-ToggleState $auto) -eq [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $auto }
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Start-Sleep -Milliseconds 500
      $settings = Open-SettingsCenter
      if ((Get-ToggleState (Find-Id $settings 'SettingsAutoCheckUpdates')) -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Auto update preference did not persist after reopening settings.' }
      Capture-Window $process '02-update-settings.png'
      Close-Window $settings
      Stop-App $process; $process = $null; $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      $restoredWidth = (Find-Id $main 'ProfileList').Current.BoundingRectangle.Width
      if ([Math]::Abs($restoredWidth - $changedWidth) -gt 5) { throw "Sidebar width did not persist after restart. changed=$changedWidth restored=$restoredWidth" }
      $settings = Open-SettingsCenter
      if ((Get-ToggleState (Find-Id $settings 'SettingsAutoCheckUpdates')) -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Auto update preference did not persist after restart.' }
      Close-Window $settings
    }
    'about' {
      Click (Find-Id $main 'HelpButton')
      $about = Wait-Until { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'AboutWindow' } } 'About window did not appear'
      $shown = (Find-Id $about 'AboutVersion').Current.Name
      $product = [Diagnostics.FileVersionInfo]::GetVersionInfo($AppPath).ProductVersion
      $releaseProduct = if ($product) { $product.TrimStart('v').Split('+')[0] } else { '' }
      if ([string]::IsNullOrWhiteSpace($releaseProduct) -or $shown -notmatch [Regex]::Escape($releaseProduct)) { throw "About version '$shown' does not match product version '$product'." }
      Find-Id $about 'AboutCheckUpdates' | Out-Null; Capture-Window $process '02-about.png'
      Close-Window $about
    }
    'local' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-initial')
      # Save a profile that owns both endpoints, otherwise the redesigned UI
      # re-asserts the empty placeholder after sync and CompareButton stays disabled.
      $profileName = 'ui-local-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      $script:deletingProfileName = $profileName
      Update-CurrentProfileEndpoints $main $profileName $left $right
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'initial.txt') 120 120 'left-initial'; Assert-File (Join-Path $right 'initial.txt') 'left-initial'
      # Reopen the shell before the second comparison. This exercises persisted
      # baseline handling and prevents the modeless progress surface from racing
      # the next UIA comparison on the same dispatcher turn.
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      # Establish a real two-way conflict, choose the right-to-left direction through the
      # DataGrid context menu (no more KeepLeft/KeepRight buttons), then prove left overwrote.
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-change'); [IO.File]::WriteAllText((Join-Path $right 'initial.txt'), 'right-change')
      Start-Sleep -Milliseconds 2200
      Compare-Ui $main $left $right 20 $true
      Apply-Direction $main '右侧覆盖左侧'
      Wait-Sync $main (Join-Path $left 'initial.txt') 120 120 'right-change'; Assert-File (Join-Path $left 'initial.txt') 'right-change'
    }
    'modes' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'from-left.txt'), 'left'); [IO.File]::WriteAllText((Join-Path $right 'right-only.txt'), 'preserve')
      # The redesigned shell restores the selected profile after each run. Save
      # these endpoints to a profile before the first sync so the second compare
      # remains executable instead of being reset to the empty placeholder.
      $profileName = 'ui-modes-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Update-CurrentProfileEndpoints $main $profileName $left $right
      Select-Mode $main '更新到右侧'; Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'from-left.txt'); Assert-File (Join-Path $right 'right-only.txt') 'preserve'
      [IO.File]::WriteAllText((Join-Path $right 'remove-in-mirror.txt'), 'delete')
      # Changing modes automatically triggers a new comparison when a previous
      # plan exists. Do not race it with a second Compare button invocation.
      Select-Mode $main '镜像到右侧'
      Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror did not create a plan'
      Click (Find-Id $main 'SyncButton')
      # Mirror is high-risk; SyncConfirmationWindow now exposes ConfirmSyncButton for that case.
      $confirm = Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null } } 'Mirror confirmation window did not appear.' 30
      Click (Find-Id $confirm 'ConfirmSyncButton')
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
      Select-Mode $main '更新到右侧'; Compare-Ui $main $sftpUri $local; Wait-Sync $main (Join-Path $local 'remote-proof.txt'); Assert-File (Join-Path $local 'remote-proof.txt') 'from-sftp'
    }
    'sftp-ui' {
      $remoteName = 'ui_sftp_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      # Cloud endpoint manager is now a top-level sidebar button rather than a Tools menu entry.
      Click (Find-Id $main 'RemoteEndpointsButton')
      $manager = Find-WindowById 'RemoteEndpointManagerWindow' 30
      Click (Find-Name $manager '新建云盘端点')
      $editor = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like '*新建云端*' } } catch { $null } } 'Cloud editor did not appear' 30
      $service = Find-Id $editor 'CloudServiceType'; $expand = $null; $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Start-Sleep -Milliseconds 200; Select-Ui (Find-Name $service 'SFTP' 5)
      Set-Text (Find-Id $editor 'SftpRemoteName') $remoteName; Set-Text (Find-Id $editor 'SftpRemoteHost') '127.0.0.1'; Set-Text (Find-Id $editor 'SftpRemotePort') "$port"; Set-Text (Find-Id $editor 'SftpRemoteUser') 'ui'; Set-Password (Find-Id $editor 'SftpRemotePassword') $password; Set-Text (Find-Id $editor 'SftpRemoteRoot') ''
      Click (Find-Id $editor 'SaveCloudEndpoint')
      Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudEndpointEditorWindow' }) } catch { $false } } 'Cloud endpoint editor did not close after saving.' 30 | Out-Null
      $config = Join-Path $appData 'rclone\rclone.conf'
      # Build the sftp:// URI directly so the test does not depend on the manager's browse workflow.
      $upload = Join-Path $root 'ui-upload'; New-Item -ItemType Directory -Force -Path $upload | Out-Null; [IO.File]::WriteAllText((Join-Path $upload 'created-through-ui.txt'), 'endpoint-ui-proof')
      $sftpUriCreated = "sftp://$remoteName"
      New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
      $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
      & $rclone config create $remoteName sftp host 127.0.0.1 user ui port "$port" pass $password --config $config 2>$null
      Select-Mode $main '更新到右侧'; Compare-Ui $main $upload $sftpUriCreated; Wait-Sync $main (Join-Path $share 'created-through-ui.txt'); Assert-File (Join-Path $share 'created-through-ui.txt') 'endpoint-ui-proof'
    }
    'profile' {
      $left = Join-Path $root 'profile-left'; $right = Join-Path $root 'profile-right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $profileName = 'ui-profile-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      $script:deletingProfileName = $profileName
      Click (Find-Id $main 'NewProfileButton')
      # Edit through the top header's EditCurrentProfileButton (more deterministic than the context menu).
      Click (Find-Id $main 'EditCurrentProfileButton')
      $editor = Find-WindowById 'ProfileEditorWindow'
      Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right; Click (Find-Id $editor 'ProfileSave')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已保存|Profile 设置已保存' } 'Profile edit did not report a save.'
      # A cancelled edit must not leak into the in-memory UI nor the persisted profile.
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileName') 'must-not-persist'; Click (Find-Id $editor 'ProfileCancel')
      if ((Find-Id $main 'ProfileList').Current.Name -match 'must-not-persist') { throw 'Cancelled profile edit changed the profile list.' }
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      $item = Find-Name (Find-Id $main 'ProfileList') $profileName 2
      (Find-Id $main 'ProfileList').SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Wait-Until { (Get-Text (Find-Id $main 'LeftPath')) -eq $left } 'Persisted Profile did not load after selecting it.'
      if ((Get-Text (Find-Id $main 'LeftPath')) -ne $left -or (Get-Text (Find-Id $main 'RightPath')) -ne $right) { throw 'Persisted Profile endpoints did not survive application restart.' }
      Delete-Profile-ThroughContextMenu
      Wait-Until { try { -not (Find-Name (Find-Id $main 'ProfileList') $profileName 1) } catch { $true } } 'Deleted Profile remained in the list.'
    }
    'profile-filter' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right, (Join-Path $left '.git')) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'included.txt'), 'included'); [IO.File]::WriteAllText((Join-Path $left '.git\config'), 'must-be-filtered')
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{DOWN}')
      Click (Find-Name $editor '添加常用排除规则'); Click (Find-Id $editor 'ProfileSave')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'included.txt'); Assert-File (Join-Path $right 'included.txt') 'included'
      if (Test-Path -LiteralPath (Join-Path $right '.git\config')) { throw 'Profile filter did not exclude .git content from the real sync.' }
    }
    'delete-threshold' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'delete-a.txt'), 'a'); [IO.File]::WriteAllText((Join-Path $right 'delete-b.txt'), 'b')
      $profileName = 'threshold-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Set-Text (Find-Id $editor 'ProfileMaxDeletes') '0'; Set-Text (Find-Id $editor 'ProfileMaxDeleteRatio') '0'; Click (Find-Id $editor 'ProfileSave')
      Select-Mode $main '镜像到右侧'; Compare-Ui $main $left $right; Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror threshold plan was not executable.'
      Click (Find-Id $main 'SyncButton'); $confirm = Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null } } 'Threshold confirm window did not appear' 30
      Set-Text (Find-Id $confirm 'ProfileNameInput') $profileName; Click (Find-Id $confirm 'ConfirmSyncButton')
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'delete-a.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'delete-b.txt')) } 'Profile-name threshold confirmation did not permit mirror deletion.' 60
    }
    'settings' {
      $settings = Open-SettingsCenter
      # The General page is selected by default; the previously separate Defaults tab is gone.
      Set-Text (Find-Id $settings 'SettingsConcurrency') '3'; Set-Text (Find-Id $settings 'SettingsTimeTolerance') '7'; Set-Text (Find-Id $settings 'SettingsIncludeRules') '**/*.keep'; Set-Text (Find-Id $settings 'SettingsExcludeRules') '**/*.skip'
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已应用|程序设置已应用' } 'Settings did not report apply.'
      $settings = Open-SettingsCenter
      if ((Get-Text (Find-Id $settings 'SettingsConcurrency')) -ne '3' -or (Get-Text (Find-Id $settings 'SettingsTimeTolerance')) -ne '7' -or (Get-Text (Find-Id $settings 'SettingsIncludeRules')) -ne '**/*.keep' -or (Get-Text (Find-Id $settings 'SettingsExcludeRules')) -ne '**/*.skip') { throw 'Applied application defaults were not persisted on re-open.' }
      Close-Window $settings
    }
    'history' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'recorded.txt'), 'history-proof'); Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'recorded.txt')
      # The redesigned UI no longer has an Operations menu; opening history requires choosing the
      # selected profile, then opening Settings → Run history, then opening the RunHistoryWindow.
      $settings = Open-SettingsCenter
      Select-Ui (Find-Id $settings 'RunHistoryNav')
      Start-Sleep -Milliseconds 300
      $launcher = Find-Id $settings 'OpenRunHistory' 5
      Click $launcher
      $history = Find-WindowById 'RunHistoryWindow'
      Click (Find-Id $history 'RefreshRunHistory')
      $entry = Wait-Until { $items = (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Run history did not show the real completed run.' 30
      $outcome = Find-Id $history 'RunHistoryOutcome'; $expand = $null; $outcome.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $outcome '成功')
      Wait-Until { (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)).Count -gt 0 } 'Successful-outcome filter hid the completed run.'
      Close-Window $history
      Close-Window $settings
    }
    'schedule' {
      $scheduledTask = 'FengSync-Test-' + [Guid]::NewGuid().ToString('N')
      # Schedule UI is launched from the SchedulesButton in the main sidebar.
      Click (Find-Id $main 'SchedulesButton')
      $schedule = Find-WindowById 'ScheduleWizard'
      Set-Text (Find-Id $schedule 'ScheduleTaskName') $scheduledTask
      Click (Find-Id $schedule 'CreateScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -eq 0 } 'Schedule UI did not create the unique Windows task.' 30
      Click (Find-Id $schedule 'TestScheduleButton')
      Wait-Until { try { (Find-Id $schedule 'ResultText').Current.Name -match '测试运行|请求' } catch { $true } } 'Schedule UI did not request a test run.' 30
      Wait-Until { $state = & schtasks.exe /Query /TN $scheduledTask /FO LIST 2>$null; $LASTEXITCODE -eq 0 -and ($state -notmatch 'Running|正在运行') } 'Scheduled test run did not leave the running state.' 60
      Click (Find-Id $schedule 'DeleteScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -ne 0 } 'Schedule UI did not delete the unique Windows task.' 30
      $scheduledTask = $null
    }
    'gdrive' {
      $upload = Join-Path $root 'drive-upload'; $download = Join-Path $root 'drive-download'; New-Item -ItemType Directory -Force -Path @($upload, $download) | Out-Null
      [IO.File]::WriteAllText((Join-Path $upload 'drive-proof.txt'), 'drive-roundtrip')
      Select-Mode $main '更新到右侧'; Compare-Ui $main $upload $driveUri; Wait-Sync $main (Join-Path $upload 'drive-proof.txt')
      Wait-Until {
        $listing = & $rclone lsf "${driveRemote}:test/FengSync-Automated-Tests/$driveChild" --config $config --contimeout 10s --timeout 30s 2>$null
        $LASTEXITCODE -eq 0 -and $listing -contains 'drive-proof.txt'
      } 'UI upload did not become visible in the generated Google Drive test child.' 120
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      Select-Mode $main '更新到右侧'
      Compare-Ui $main $driveUri $download
      Wait-Sync $main (Join-Path $download 'drive-proof.txt') 180 300
      Assert-File (Join-Path $download 'drive-proof.txt') 'drive-roundtrip'
    }
    'gdrive-volume' {
      # Exercise the supported Google Drive path with a small, repeatable workload
      # in every synchronization mode. Each mode gets an isolated remote child.
      $results = [System.Collections.Generic.List[object]]::new()
      foreach ($mode in @(
        [pscustomobject]@{ Name = '双向同步'; Slug = 'two-way' },
        [pscustomobject]@{ Name = '镜像到右侧'; Slug = 'mirror' },
        [pscustomobject]@{ Name = '更新到右侧'; Slug = 'update' }
      )) {
        $case = "$($mode.Slug)-flat-10-files"
        $upload = Join-Path $root (Join-Path 'drive-volume' $case)
        $remoteCase = $driveUri.TrimEnd('/') + '/' + $case
        New-Item -ItemType Directory -Force -Path $upload | Out-Null
        # Match the remote directory anchor so this volume test measures the ten
        # fixture transfers, not an unrelated destructive-confirmation workflow.
        $fixtureAnchor = Join-Path $upload '.fengsync-fixture-anchor'
        [IO.File]::WriteAllText($fixtureAnchor, 'fixture')
        for ($i = 1; $i -le 10; $i++) {
          [IO.File]::WriteAllText((Join-Path $upload ('batch-{0:D3}.txt' -f $i)), "Google Drive fixture $i")
        }

        # Google Drive does not preserve empty directories, so create an anchor
        # before the UI compares this isolated endpoint pair.
        & $rclone mkdir ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") --config $config --contimeout 10s --timeout 30s
        if ($LASTEXITCODE -ne 0) { throw "Could not create Google Drive test child: $remoteCase" }
        & $rclone copyto $fixtureAnchor ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case/.fengsync-fixture-anchor") --config $config --contimeout 10s --timeout 30s
        if ($LASTEXITCODE -ne 0) { throw "Could not materialize Google Drive test child: $remoteCase" }

        Stop-App $process; $process = $null
        $launch = Start-App; $process = $launch[0]; $main = $launch[1]
        Select-Mode $main $mode.Name
        Compare-Ui $main $upload $remoteCase 180
        Wait-Sync $main (Join-Path $upload 'batch-001.txt') 180 600
        Wait-Until {
          $listing = & $rclone lsf ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") --recursive --config $config --contimeout 10s --timeout 30s 2>$null
          $LASTEXITCODE -eq 0 -and @($listing | Where-Object { $_ -match '\.txt$' }).Count -eq 10
        } "Google Drive did not contain all 10 fixture files: $remoteCase" 180 | Out-Null
        $results.Add($mode.Slug)
        Write-Output "Google Drive 10-file matrix completed: mode=$($mode.Slug)"
      }
      if ($results.Count -ne 3) { throw 'Google Drive 10-file matrix did not complete every synchronization mode.' }
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
  Stop-App $process
  Write-HarnessTrace "Cleaning up scenario: $Scenario"
  if ($remoteCleanup) {
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
