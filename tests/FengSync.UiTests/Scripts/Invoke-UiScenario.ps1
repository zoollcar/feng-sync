[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('ui-shell', 'ui-shell-native', 'ui-shell-software', 'ui-visual-matrix', 'update-settings', 'about', 'local', 'local-move', 'modes', 'selection', 'sftp-to-local', 'sftp-ui', 'sftp-service', 'cloud-transfers', 'batch-run', 'profile', 'profile-filter', 'delete-threshold', 'settings', 'history', 'schedule', 'gdrive', 'gdrive-volume', 'r2', 'r2-volume')][string]$Scenario,
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$Workspace,
    # Off by default. See the "Desktop-free input" section below: a scenario normally parks its windows
    # outside the visible desktop and drives them through UI Automation, so running the suite never covers
    # the user's screen, never moves their cursor and never takes their foreground window. Pass this switch
    # to watch a scenario run in the foreground with real mouse input.
    [switch]$ShowOnScreen
)

# Every scenario uses a new data root. On failure it is retained with its logs and
# screenshots; remote cleanup is constrained to a generated child below a fixed
# test root and is verified before the scenario can finish.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public struct FengSyncPoint { public int X; public int Y; }
public static class FengSyncUiMouse {
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint procId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll", EntryPoint="ShowWindow")] public static extern bool ShowWindowNative(IntPtr hWnd, int command);
  [DllImport("user32.dll", EntryPoint="SetWindowPos")] public static extern bool SetWindowPosNative(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
  // Desktop-free input: PostMessage delivers a gesture straight to a window's message queue, and
  // ScreenToClient converts a UIA screen rectangle into that window's client space. Neither needs the
  // window to be raised, and neither touches the physical cursor.
  [DllImport("user32.dll", EntryPoint="PostMessage")] public static extern bool PostMessageNative(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref FengSyncPoint point);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
  public static readonly IntPtr TOPMOST = new IntPtr(-1), NOTOPMOST = new IntPtr(-2);
  public const uint NOMOVE = 0x0002, NOSIZE = 0x0001, SHOWWINDOW = 0x0040, NOZORDER = 0x0004, NOACTIVATE = 0x0010;
  public const int RESTORE = 9;
  public const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
}
'@
$AppPath = [IO.Path]::GetFullPath($AppPath); $Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Application not found: $AppPath" }
$cleanup = Join-Path $Workspace 'tests\Shared\TestProcessCleanup.ps1'; . $cleanup; Clear-FengSyncTestProcesses -Workspace $Workspace
$stamp = "ui-$Scenario-" + [Guid]::NewGuid().ToString('N')
$root = Join-Path $Workspace ('.fengsync-test\ui\' + $stamp)
$appData = Join-Path $root 'appdata'; $artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $root, $appData, $artifacts | Out-Null
$screenshotManifest = [System.Collections.Generic.List[object]]::new()
$script:comparisonScreenshotCount = 0
$script:syncScreenshotCount = 0
$script:activeSyncLabel = $null
$script:activeSyncProgressCaptured = $false
# A test has no reason to retain completed progress windows. This makes every
# successful sync self-closing even before the explicit UI assertion below.
[IO.File]::WriteAllText((Join-Path $appData 'FengSync.local.json'), '{"ShowCompleted":false}')
$scenarioTimer = [Diagnostics.Stopwatch]::StartNew()
$harnessLog = Join-Path $root 'harness.log'
function Write-HarnessTrace([string]$message) {
  $line = '{0:O} | {1}' -f [DateTimeOffset]::Now, $message
  [IO.File]::AppendAllText($harnessLog, $line + [Environment]::NewLine)
  # Write-Host rather than Write-Output: a trace is diagnostics, not a value. Emitting it to the pipeline
  # let a trace written inside a function join that function's return value, which turned a returned window
  # element into an array and made the next lookup fail with a baffling "missing element".
  Write-Host $message
}
Write-HarnessTrace "Starting scenario: $Scenario"
Write-HarnessTrace $(if ($ShowOnScreen) { 'Input mode: on screen (windows raised, real mouse input).' } else { 'Input mode: desktop-free (windows parked off screen, input delivered through UI Automation).' })

# =================== Desktop-free input ===================
# Default behaviour: a scenario must not disturb whoever is using the machine. Concretely that means
#   1. no window is ever raised or activated, so the user's foreground window keeps focus;
#   2. the physical cursor never moves, so no real mouse input is synthesized;
#   3. every Feng Sync window is parked outside the visible desktop, so nothing is drawn over the user's work.
# Input is delivered instead through UI Automation patterns, and — for the few gestures WPF only produces
# from a real window message, such as opening a ContextMenu — through PostMessage straight to the window's
# message queue. Both work while the window sits off screen and while it is not the active window.
# Pass -ShowOnScreen to get the historical behaviour back: windows restored, raised, and driven with real
# mouse input. That mode exists for watching a scenario by hand, and is never the default.
$script:showOnScreen = [bool]$ShowOnScreen
# Parked far outside every virtual desktop. SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE moves the window
# without resizing it, without reordering it against other windows and without giving it focus.
$script:offScreenOrigin = -32000

function Initialize-TestWindowPlacement {
  # Called as soon as the main window exists, before any interaction. A window parked here is still a
  # normal, visible, fully rendered HWND: UI Automation sees it, WPF lays it out, and its BoundingRectangle
  # stays correct — only its screen position changes. Every geometry assertion in this script compares
  # rectangles inside the same window, so those all keep working.
  if ($script:showOnScreen) { return }
  if (-not $script:process -or $script:process.HasExited) { return }
  foreach ($handle in Get-AppWindowHandles) { Park-WindowOffScreen $handle }
}

function Get-AppWindowHandles {
  $handles = [System.Collections.Generic.List[IntPtr]]::new()
  if (-not $script:process -or $script:process.HasExited) { return $handles }
  $callback = {
    param($hWnd, $lParam)
    $procId = 0
    [void][FengSyncUiMouse]::GetWindowThreadProcessId($hWnd, [ref]$procId)
    if ($procId -eq $script:process.Id -and [FengSyncUiMouse]::IsWindowVisible($hWnd)) { $handles.Add($hWnd) }
    return $true
  }
  [void][FengSyncUiMouse]::EnumWindows($callback, [IntPtr]::Zero)
  return $handles
}

function Park-WindowOffScreen([IntPtr]$handle) {
  if ($script:showOnScreen -or $handle -eq [IntPtr]::Zero) { return }
  $flags = [FengSyncUiMouse]::NOSIZE -bor [FengSyncUiMouse]::NOZORDER -bor [FengSyncUiMouse]::NOACTIVATE
  [void][FengSyncUiMouse]::SetWindowPosNative($handle, [IntPtr]::Zero, $script:offScreenOrigin, $script:offScreenOrigin, 0, 0, $flags)
}

# A window created after startup (a modal dialog, a message box) is parked as soon as it is seen, so the
# user never catches a dialog flashing up in the middle of the screen.
function Park-AllAppWindows {
  if ($script:showOnScreen) { return }
  foreach ($handle in Get-AppWindowHandles) { Park-WindowOffScreen $handle }
}

function Activate-TestWindow {
  # Kept as the single "make the window ready for the next step" seam. Desktop-free mode deliberately does
  # nothing here: the whole point is that the test never raises or focuses a window. Only -ShowOnScreen
  # restores the restore/raise dance.
  if (-not $script:showOnScreen) { return }
  if (-not $script:process -or $script:process.HasExited -or $script:process.MainWindowHandle -eq 0) { return }
  $handle = $script:process.MainWindowHandle
  [void][FengSyncUiMouse]::ShowWindowNative($handle, [FengSyncUiMouse]::RESTORE)
  $flags = [FengSyncUiMouse]::NOMOVE -bor [FengSyncUiMouse]::NOSIZE -bor [FengSyncUiMouse]::SHOWWINDOW
  [void][FengSyncUiMouse]::SetWindowPosNative($handle, [FengSyncUiMouse]::TOPMOST, 0, 0, 0, 0, $flags)
  [void][FengSyncUiMouse]::SetWindowPosNative($handle, [FengSyncUiMouse]::NOTOPMOST, 0, 0, 0, 0, $flags)
  [void][FengSyncUiMouse]::SetForegroundWindow($handle)
}

# The narrow fallback for the gestures WPF only routes to an *active* window — opening a ContextMenu is the
# one that matters here. It raises the window in the Z-order and takes the foreground so the posted message
# reaches WPF's input system, but it never moves the window: it stays parked outside the visible desktop, so
# the user still sees nothing, and the cursor is still never touched. Only the foreground window changes,
# and only for the length of one gesture. Callers try the purely posted gesture first and fall back to this.
function Focus-OffScreenWindow {
  if ($script:showOnScreen) { Activate-TestWindow; return }
  if (-not $script:process -or $script:process.HasExited -or $script:process.MainWindowHandle -eq 0) { return }
  $handle = $script:process.MainWindowHandle
  $flags = [FengSyncUiMouse]::NOMOVE -bor [FengSyncUiMouse]::NOSIZE -bor [FengSyncUiMouse]::SHOWWINDOW
  [void][FengSyncUiMouse]::SetWindowPosNative($handle, [FengSyncUiMouse]::TOPMOST, 0, 0, 0, 0, $flags)
  [void][FengSyncUiMouse]::SetWindowPosNative($handle, [FengSyncUiMouse]::NOTOPMOST, 0, 0, 0, 0, $flags)
  $set = [FengSyncUiMouse]::SetForegroundWindow($handle)
  Start-Sleep -Milliseconds 150
  Write-HarnessTrace "raised the parked window into the foreground without moving it (hwnd=0x$($handle.ToInt64().ToString('X')), accepted=$set)."
}

# --- gesture helpers ---------------------------------------------------------------------------------
# The window that owns an element, falling back to the main window for elements WPF exposes without a
# NativeWindowHandle of their own (most non-hwnd peers, including list items and text blocks).
function Get-OwningWindowHandle($element) {
  $handle = [IntPtr]$element.Current.NativeWindowHandle
  if ($handle -ne [IntPtr]::Zero) { return $handle }
  if ($script:process -and -not $script:process.HasExited -and $script:process.MainWindowHandle -ne 0) { return $script:process.MainWindowHandle }
  return [IntPtr]::Zero
}

# The point inside an element that a gesture should target, in the owning window's client space. The
# horizontal fraction is a parameter because a full-width row's centre can fall in the blank area to the
# right of its text — the profile list is hit reliably near its left edge, and did so before this rewrite.
function Get-ElementClientPoint($element, [double]$horizontalFraction = 0.5) {
  $bounds = $element.Current.BoundingRectangle
  if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Element has no usable bounds: $($element.Current.AutomationId)" }
  $handle = Get-OwningWindowHandle $element
  if ($handle -eq [IntPtr]::Zero) { throw "Element is not owned by a Feng Sync window: $($element.Current.AutomationId)" }
  $point = [FengSyncPoint]::new()
  $point.X = [int]($bounds.Left + $bounds.Width * $horizontalFraction)
  $point.Y = [int]($bounds.Top + $bounds.Height / 2)
  if (-not [FengSyncUiMouse]::ScreenToClient($handle, [ref]$point)) { throw "ScreenToClient failed for: $($element.Current.AutomationId)" }
  return @{ Handle = $handle; X = $point.X; Y = $point.Y }
}

function Get-PackedPoint([int]$x, [int]$y) { return [IntPtr]((($y -band 0xFFFF) -shl 16) -bor ($x -band 0xFFFF)) }

function Invoke-RightClickGesture($element, [double]$horizontalFraction = 0.5) {
  if ($script:showOnScreen) {
    $bounds = $element.Current.BoundingRectangle
    Activate-TestWindow
    try { $element.SetFocus() } catch { }
    Start-Sleep -Milliseconds 100
    [FengSyncUiMouse]::SetCursorPos([int]($bounds.Left + $bounds.Width * $horizontalFraction), [int]($bounds.Top + ($bounds.Height / 2))) | Out-Null
    [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
    return
  }
  # WPF hit-tests a mouse message against the position of the real pointer, not against the coordinates in
  # the message, so a posted click is discarded whenever the cursor is somewhere else — which, in a
  # desktop-free run, it always is. The keyboard route has no such dependency, and WPF opens a ContextMenu
  # from the context-menu key just as it does from a right-click. So that is what gets posted: the element
  # is focused through UI Automation first, then the key is delivered to the owning window.
  try { $element.SetFocus() } catch { }
  Start-Sleep -Milliseconds 120
  $handle = Get-OwningWindowHandle $element
  if ($handle -eq [IntPtr]::Zero) { throw "Element is not owned by a Feng Sync window: $($element.Current.AutomationId)" }
  Post-KeyToWindow $handle 0x5D 0x01000001   # VK_APPS; the extended-key bit is set because VK_APPS is an extended key
  Write-HarnessTrace "posted context-menu key to hwnd=0x$($handle.ToInt64().ToString('X')) after focusing '$($element.Current.AutomationId)'."
}

# Posts a key press and release. lParam follows the WM_KEYDOWN layout: repeat count 1 in the low word, and
# the caller supplies the remaining bits (scan-code/extended-key flags).
function Post-KeyToWindow([IntPtr]$handle, [int]$virtualKey, [int]$downFlags = 1) {
  $down = [IntPtr]($downFlags -bor 1)
  $up = [IntPtr]($downFlags -bor 0xC0000001)
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0100, [IntPtr]$virtualKey, $down)   # WM_KEYDOWN
  Start-Sleep -Milliseconds 60
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0101, [IntPtr]$virtualKey, $up)     # WM_KEYUP
}

# Shift+F10 is the second key Windows and WPF both treat as "open the context menu", so a window that
# ignores the context-menu key still answers this one.
function Post-ContextMenuKeyChord([IntPtr]$handle) {
  $shift = [IntPtr]0x10
  $f10 = [IntPtr]0x79
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0100, $shift, [IntPtr]1)
  Start-Sleep -Milliseconds 40
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0100, $f10, [IntPtr]1)
  Start-Sleep -Milliseconds 60
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0101, $f10, [IntPtr]0xC0000001)
  Start-Sleep -Milliseconds 40
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0101, $shift, [IntPtr]0xC0000001)
}

# Answers a native modal dialog with "Yes". A MessageBox's buttons are Win32 controls, so the message its
# own Yes button would send is the only handle this harness has on them: WM_COMMAND with IDYES (6) in the
# low word of wParam and BN_CLICKED (0) in the high word.
function Answer-NativeDialogYes($dialog) {
  $handle = [IntPtr]$dialog.Current.NativeWindowHandle
  if ($handle -eq [IntPtr]::Zero) { throw "Native dialog has no window handle: $($dialog.Current.Name)" }
  [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0111, [IntPtr]6, [IntPtr]::Zero)
  Write-HarnessTrace "posted IDYES to native dialog '$($dialog.Current.Name)' (hwnd=0x$($handle.ToInt64().ToString('X')))."
}

function Invoke-LeftClickGesture($element) {
  if ($script:showOnScreen) {
    $bounds = $element.Current.BoundingRectangle
    Activate-TestWindow
    Start-Sleep -Milliseconds 100
    [FengSyncUiMouse]::SetCursorPos([int]($bounds.Left + ($bounds.Width / 2)), [int]($bounds.Top + ($bounds.Height / 2))) | Out-Null
    [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    return
  }
  # A UIA invoke is the desktop-free click; the message pair is only the fallback for a peer WPF exposes
  # without InvokePattern.
  $pattern = $null
  if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
  $target = Get-ElementClientPoint $element
  $packed = Get-PackedPoint $target.X $target.Y
  [void][FengSyncUiMouse]::PostMessageNative($target.Handle, 0x0201, [IntPtr]::Zero, $packed)
  Start-Sleep -Milliseconds 60
  [void][FengSyncUiMouse]::PostMessageNative($target.Handle, 0x0202, [IntPtr]::Zero, $packed)
}

function Send-KeysToWindow($element, [string]$keys) {
  if ($script:showOnScreen) {
    $element.SetFocus()
    Start-Sleep -Milliseconds 100
    [Windows.Forms.SendKeys]::SendWait($keys)
    return
  }
  # Desktop-free mode cannot use SendKeys: it types into whatever window is foreground, which is precisely
  # the window the test must leave alone. Posted keystrokes reach WPF's input system just as well — WPF
  # routes them to whatever element holds keyboard focus, and that does not depend on where the pointer is,
  # which is exactly why the keyboard works where a posted click does not. The element is focused first
  # through UI Automation.
  $handle = Get-OwningWindowHandle $element
  if ($handle -eq [IntPtr]::Zero) { throw "Element is not owned by a Feng Sync window: $($element.Current.AutomationId)" }
  try { $element.SetFocus() } catch { }
  Start-Sleep -Milliseconds 100
  foreach ($token in [Regex]::Matches($keys, '\{[^}]*\}|.')) {
    $text = $token.Value
    if (-not $text.StartsWith('{')) { Post-CharToWindow $handle $text; continue }
    $parts = $text.Trim('{', '}').Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -eq 0) { continue }
    $name = $parts[0].ToUpperInvariant()
    $repeat = 1
    if ($parts.Count -gt 1) { [void][int]::TryParse($parts[1], [ref]$repeat) }
    if (-not $script:virtualKeys.ContainsKey($name)) { throw "Unsupported key in desktop-free mode: $text" }
    for ($i = 0; $i -lt $repeat; $i++) { Post-KeyToWindow $handle $script:virtualKeys[$name] }
  }
}

# The keys this harness actually presses, so a step already written in SendKeys syntax can be delivered as
# posted messages instead. Add to this map rather than falling back to the foreground-dependent SendKeys.
$script:virtualKeys = @{
  'LEFT' = 0x25; 'UP' = 0x26; 'RIGHT' = 0x27; 'DOWN' = 0x28
  'HOME' = 0x24; 'END' = 0x23; 'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09
  'BACKSPACE' = 0x08; 'DELETE' = 0x2E; 'SPACE' = 0x20; 'APPS' = 0x5D; 'F10' = 0x79
}

# WM_CHAR carries literal text into the focused element, which is how the password fallback types.
function Post-CharToWindow([IntPtr]$handle, [string]$text) {
  foreach ($character in $text.ToCharArray()) {
    [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0102, [IntPtr][int]$character, [IntPtr]1)
    Start-Sleep -Milliseconds 20
  }
}

function Enable-RcloneWindowsProxy {
  # The online fixture invokes bundled rclone before the application starts. GUI
  # processes and this isolated PowerShell host may not inherit shell proxy variables,
  # so mirror Feng Sync's WinINET fallback for fixture setup and for the child app.
  if ($env:HTTPS_PROXY -or $env:HTTP_PROXY -or $env:ALL_PROXY) { return }
  $internet = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
  if ($internet.ProxyEnable -ne 1 -or [string]::IsNullOrWhiteSpace($internet.ProxyServer)) { return }
  $entries = @{}
  $singleProxy = $internet.ProxyServer -notmatch '='
  if (-not $singleProxy) {
    foreach ($part in $internet.ProxyServer -split ';') {
      $pair = $part -split '=', 2
      if ($pair.Count -eq 2) { $entries[$pair[0].Trim().ToLowerInvariant()] = $pair[1].Trim() }
    }
  } else {
    $entries.http = $internet.ProxyServer
    $entries.https = $internet.ProxyServer
    $entries.socks = $internet.ProxyServer
  }
  $asHttp = { param([string]$value) if ($value -match '://') { $value } else { 'http://' + $value } }
  if ($entries.http) { $env:HTTP_PROXY = & $asHttp $entries.http }
  if ($entries.https) { $env:HTTPS_PROXY = & $asHttp $entries.https }
  if ($entries.socks -and -not $env:ALL_PROXY) {
    $env:ALL_PROXY = if ($singleProxy) { & $asHttp $entries.socks } elseif ($entries.socks -match '://') { $entries.socks } else { 'socks5://' + $entries.socks }
  }
  $env:NO_PROXY = '127.0.0.1,localhost,::1'
}

function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
  $end = [DateTime]::UtcNow.AddSeconds($Seconds)
  do { $value = & $Condition; if ($null -ne $value -and $value -ne $false) { return $value }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $end)
  throw $Message
}
function Find-Id($root, [string]$id, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) } catch { $null } } "Missing UI element: $id" $seconds }
function Find-Name($root, [string]$name, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) } catch { $null } } "Missing UI element: $name" $seconds }
function Find-NameContaining($root, [string]$fragment, [int]$seconds = 20) { Wait-Until { try { $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition); foreach ($item in $items) { if ($item.Current.Name -like "*$fragment*") { return $item } }; $null } catch { $null } } "Missing UI element containing: $fragment" $seconds }
function Click($element) {
  # A disabled element cannot be invoked through UIA — UIA throws the
  # generic "Could not open the process token" / "Unrecognized error"
  # rather than letting the test tell the user that the action is simply
  # unavailable. Wait briefly for IsEnabled to settle before retrying.
  for ($i = 0; $i -lt 50; $i++) {
    try {
      if ($element.Current.IsEnabled) {
        # InvokePattern is the desktop-free click. A control that does not offer it cannot be clicked
        # without the pointer, and saying so is better than a click that silently lands nowhere.
        $p = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
        throw "Element cannot be invoked: $($element.Current.Name)"
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
function Set-Password($element, [string]$value) {
  $pattern = $null
  if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($value); return }
  Send-KeysToWindow $element $value
}
function Select-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { throw "Element cannot be selected: $($element.Current.Name)" }; $p.Select() }
function Select-UiOrAncestor($element) {
  $current = $element
  while ($current) {
    $pattern = $null
    if ($current.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
    $current = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($current)
  }
  throw "Element has no selectable ancestor: $($element.Current.Name)"
}
function Toggle-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { throw "Element cannot be toggled: $($element.Current.Name)" }; $p.Toggle() }
function Get-ToggleState($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { throw "Element has no TogglePattern: $($element.Current.Name)" }; return $p.Current.ToggleState }
function Get-Text($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element has no ValuePattern: $($element.Current.Name)" }; return $p.Current.Value }
function Scroll-ToTop($element) {
  $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
  $scrollable = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
  if (-not $scrollable) { return }
  $pattern = $null
  if ($scrollable.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and $pattern.Current.VerticallyScrollable) {
    $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 180
  }
}
function Scroll-ToBottom($element) {
  # The desktop-free replacement for pressing End in a scroll viewer: the same destination, reached through
  # the ScrollPattern instead of a keystroke that would need the window to be foreground.
  $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
  $scrollable = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
  if (-not $scrollable) { return }
  $pattern = $null
  if ($scrollable.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and $pattern.Current.VerticallyScrollable) {
    $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Start-Sleep -Milliseconds 180
  }
}
function Drag-ElementHorizontally($element, [int]$offset) {
  # A pointer drag cannot be reproduced without the pointer: WPF hit-tests mouse input against the position
  # of the real cursor, so a posted press is discarded rather than routed. Only an on-screen run can drag;
  # a desktop-free step has to reach the same state through the keyboard or a UI Automation equivalent.
  if (-not $script:showOnScreen) { throw "Dragging '$($element.Current.AutomationId)' requires -ShowOnScreen; use the keyboard equivalent instead." }
  $box = $element.Current.BoundingRectangle
  if ($box.Width -le 0 -or $box.Height -le 0) { throw "Element cannot be dragged because it has no bounds: $($element.Current.AutomationId)" }
  Activate-TestWindow
  Start-Sleep -Milliseconds 180
  $start = [Drawing.Point]::new([int]($box.Left + ($box.Width / 2)), [int]($box.Top + ($box.Height / 2)))
  [FengSyncUiMouse]::SetCursorPos($start.X, $start.Y); Start-Sleep -Milliseconds 120
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [FengSyncUiMouse]::SetCursorPos($start.X + [int]($offset / 2), $start.Y); Start-Sleep -Milliseconds 180
  [FengSyncUiMouse]::SetCursorPos($start.X + $offset, $start.Y); Start-Sleep -Milliseconds 220
  [FengSyncUiMouse]::mouse_event([FengSyncUiMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}

# Dismisses a ContextMenu without a foreground window. The item invoke that normally precedes this already
# closes the menu, so this is a best-effort sweep for a menu left open by a failed step; posting Escape to
# the popup's own window is harmless when it is already gone.
function Close-ContextMenuBestEffort {
  if ($script:showOnScreen) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 120; return }
  foreach ($id in @('ComparisonActionMenu', 'ProfileContextMenu')) {
    $menu = try { Find-ContextMenuInApp $id } catch { $null }
    if ($null -eq $menu) { continue }
    $handle = [IntPtr]$menu.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { continue }
    [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)   # WM_KEYDOWN, VK_ESCAPE
    [void][FengSyncUiMouse]::PostMessageNative($handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)   # WM_KEYUP, VK_ESCAPE
    Start-Sleep -Milliseconds 120
  }
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
  $window = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like "*$titleFragment*" } } catch { $null } } "Missing Feng Sync window containing: $titleFragment" $seconds
  # A modal dialog opens centred on screen; a desktop-free run moves it away the moment it is located, so
  # the user never catches it flashing up. Parking is idempotent, so repeating it per lookup is harmless.
  Park-AllAppWindows
  return $window
}
function Find-WindowById([string]$id, [int]$seconds = 20) {
  $window = Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq $id } } catch { $null } } "Missing Feng Sync window with id: $id" $seconds
  Park-AllAppWindows
  return $window
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
  $menu = $null
  for ($attempt = 1; $attempt -le 3 -and $null -eq $menu; $attempt++) {
    # The menu button carries an InvokePattern, which is the desktop-free path and needs no window state.
    # The right-click gesture below is the fallback, and it too avoids the physical cursor by posting the
    # message pair to the owning window.
    [System.Windows.Automation.InvokePattern]$invoke = $null
    if (-not $actionButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
      Invoke-RightClickGesture $actionButton
    } else {
      if ($script:showOnScreen) { Activate-TestWindow; try { $actionButton.SetFocus() } catch { } }
      $invoke.Invoke()
    }
    $menuDeadline = [DateTime]::UtcNow.AddSeconds(2)
    while ([DateTime]::UtcNow -lt $menuDeadline -and $null -eq $menu) {
      try {
        $candidate = Find-ContextMenuInApp 'ComparisonActionMenu'
        if ($candidate) {
          $target = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $header))
          if ($target) { $menu = $candidate }
        }
      } catch { $menu = $null }
      if ($null -eq $menu) { Start-Sleep -Milliseconds 150 }
    }
    if ($null -eq $menu) {
      Close-ContextMenuBestEffort
      Start-Sleep -Milliseconds 150
    }
  }
  if ($null -eq $menu) { throw 'Row context menu did not appear after three attempts.' }
  # Invoke the MenuItem through UI Automation. The arrow-key walk below is only a fallback for a build that
  # exposes no InvokePattern, because a ContextMenu routes arrow keys through the foreground window.
  $itemCount = 0
  try {
    $items = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
    $itemCount = $items.Count
    $target = $null
    foreach ($it in $items) { if ($it.Current.Name -eq $header) { $target = $it; break } }
    if ($null -ne $target) {
      Click $target
    } elseif ($script:showOnScreen) {
      # Fallback: arrow-down until the desired header is current (WPF ContextMenu
      # routes arrow keys to its menu items directly).
      $found = $false
      for ($i = 0; $i -lt $itemCount -and -not $found; $i++) {
        Send-KeysToWindow $menu '{DOWN}'
        Start-Sleep -Milliseconds 60
        try {
          $children = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
          foreach ($c in $children) { if ($c.Current.Name -eq $header) { $found = $true; break } }
        } catch { }
      }
      if ($found) {
        Start-Sleep -Milliseconds 100
        Send-KeysToWindow $menu '{ENTER}'
      } else { throw "Direction menu item '$header' was not found in the context menu." }
    } else {
      throw "Direction menu item '$header' was not exposed with an invocable pattern, and the keyboard fallback needs -ShowOnScreen."
    }
  } finally {
    Start-Sleep -Milliseconds 200
    Close-ContextMenuBestEffort
  }
  Wait-Until { (Find-Id (Get-LiveMain) 'Status').Current.Name -match [Regex]::Escape($header) } "Direction override '$header' was not applied." 10 | Out-Null
}
# =================== Profile context menu (right-click ProfileList) ===================
function Find-ContextMenuInApp([string]$automationId = 'ProfileContextMenu') {
  # WPF ContextMenus are not top-level windows, so they don't appear as children of
  # RootElement. Recursively walk the whole tree looking for the popup's Menu control.
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $queue = [System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
  $queue.Enqueue($root)
  while ($queue.Count -gt 0) {
    $node = $queue.Dequeue()
    try {
      if ($node.Current.ControlType -eq [System.Windows.Automation.ControlType]::Menu -and $node.Current.AutomationId -eq $automationId) {
        return $node
      }
      $children = $node.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($c in $children) { $queue.Enqueue($c) }
    } catch { }
  }
  return $null
}
function Get-ContextMenuItemNames($menu) {
  try {
    $items = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))
    return (@($items | ForEach-Object { $_.Current.AutomationId }) -join ',')
  } catch { return "<unreadable: $($_.Exception.Message)>" }
}

function Open-ProfileContextMenu([string]$requiredItemId = 'ProfileContextEdit') {
  $list = Find-Id (Get-LiveMain) 'ProfileList'
  $target = if ([string]::IsNullOrWhiteSpace($script:deletingProfileName)) { $list } else { Find-Name $list $script:deletingProfileName 5 }
  # The ContextMenu belongs to the ListBox and acts on the selected row, so the row has to be selected
  # before the menu is asked for — with a real right-click the press itself does that, and the keyboard
  # route has to do it explicitly.
  if (-not [string]::IsNullOrWhiteSpace($script:deletingProfileName)) { Select-UiOrAncestor $target }
  # The menu is only usable once it actually contains the item the caller is about to invoke; a popup that
  # has been raised but not populated would otherwise be returned and then time out looking for its items.
  $itemCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $requiredItemId)
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    # Three routes, cheapest first: the context-menu key, the Shift+F10 chord, then the same key again with
    # the window raised in the Z-order. None of them moves the pointer or unparks the window.
    switch ($attempt) {
      1 { Invoke-RightClickGesture $target }
      2 { Focus-OffScreenWindow; Post-ContextMenuKeyChord (Get-OwningWindowHandle $target) }
      default { Focus-OffScreenWindow; Invoke-RightClickGesture $target }
    }
    $menu = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $menu) {
      try {
        $candidate = Find-ContextMenuInApp
        if ($candidate -and $candidate.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $itemCondition)) { $menu = $candidate }
      } catch { $menu = $null }
      if ($null -eq $menu) { Start-Sleep -Milliseconds 150 }
    }
    if ($menu) { return $menu }
    $open = try { Find-ContextMenuInApp } catch { $null }
    Write-HarnessTrace "profile context menu attempt $attempt did not expose '$requiredItemId'; menu present=$($null -ne $open); items=$(if ($open) { Get-ContextMenuItemNames $open } else { '<none>' })"
    Close-ContextMenuBestEffort
  }
  throw "Profile context menu did not expose '$requiredItemId' after three attempts."
}
function Edit-Profile-ThroughContextMenu {
  $menu = Open-ProfileContextMenu 'ProfileContextEdit'
  Click (Find-Id $menu 'ProfileContextEdit')
}
function Delete-Profile-ThroughContextMenu {
  $menu = Open-ProfileContextMenu 'ProfileContextDelete'
  Click (Find-Id $menu 'ProfileContextDelete')
  # The DeleteProfile_Click handler pops a YesNo MessageBox; closing the window
  # (Escape / X) would answer No, which leaves the profile in the list. Walk the
  # dialog tree to find the Yes button by name and invoke it directly through UIA.
  $removeDialog = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like '*移除配置*' } } catch { $null } } 'Remove profile dialog did not appear' 30
  Park-WindowOffScreen ([IntPtr]$removeDialog.Current.NativeWindowHandle)
  $yes = Wait-Until { try { Find-Name $removeDialog '是' 1 } catch { try { Find-Name $removeDialog 'Yes' 1 } catch { $null } } } 'Yes button did not appear on the remove dialog.' 5
  # A MessageBox is a native dialog, so its Yes button exposes no InvokePattern and — unlike the WPF windows
  # this harness drives — cannot be activated through one. It does answer the WM_COMMAND its own button
  # would send, which needs neither the pointer nor the foreground window.
  $invokable = $null
  if ($yes.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokable)) { $invokable.Invoke() }
  elseif ($script:showOnScreen) { Invoke-LeftClickGesture $yes }
  else { Answer-NativeDialogYes $removeDialog }
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
  Park-WindowOffScreen ([IntPtr]$editor.Current.NativeWindowHandle)
  # Bring the concurrency field into view by scrolling, not by pressing End: the keyboard route needs a
  # foreground window, and a scroll is the same state change through UI Automation.
  Scroll-ToBottom (Find-Id $editor 'ProfileSections')
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
  # A finished sync can restore or activate the shell; put it back off screen before the next step.
  Park-AllAppWindows
  $status = Assert-NoUiFailure 'Synchronization'
  Write-HarnessTrace "Application operation boundary reached. UI status: $status"
}
$approvedConfirmations = [Collections.Generic.HashSet[string]]::new()
function Approve-ConfirmationIfPresent {
  $confirm = try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null }
  if ($confirm) {
    Park-WindowOffScreen ([IntPtr]$confirm.Current.NativeWindowHandle)
    $id = [string]::Join('-', $confirm.GetRuntimeId())
    if ($approvedConfirmations.Add($id)) {
      Capture-Element $confirm ("sync-{0:D2}-confirmation.png" -f $script:syncScreenshotCount) 'sync-confirmation' $script:activeSyncLabel
      Click (Find-Id $confirm 'ConfirmSyncButton')
      return $true
    }
  }
  return $false
}
function Wait-Sync { param($main, [string]$expectedFile, [int]$comparisonSeconds = 120, [int]$transferSeconds = 120, [string]$expectedContent = $null)
  $checkExpectedContent = $PSBoundParameters.ContainsKey('expectedContent')
  try { Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Comparison did not produce an executable plan' $comparisonSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Comparison did not produce an executable plan. UI status: $status" }
  $expectedDescription = if ($checkExpectedContent) { $expectedContent } else { '<existence-only>' }
  Write-Output "Comparison ready; expected sync output: $expectedFile; expected content: $expectedDescription"
  $script:syncScreenshotCount++
  $script:activeSyncLabel = "sync-$($script:syncScreenshotCount)-$([IO.Path]::GetFileName($expectedFile))"
  $script:activeSyncProgressCaptured = $false
  Click (Find-Id $main 'SyncButton')
  Write-HarnessTrace 'Sync button invoked.'
  Capture-Element (Get-LiveMain) ("sync-{0:D2}-started.png" -f $script:syncScreenshotCount) 'sync-started' $script:activeSyncLabel
  Write-HarnessTrace "Probing synchronized result: $expectedFile"
  $resultDeadline = [DateTime]::UtcNow.AddSeconds($transferSeconds)
  $resultObserved = $false
  try {
    do {
      Approve-ConfirmationIfPresent | Out-Null
      if (-not $script:activeSyncProgressCaptured) {
        $progress = try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'ProgressWindow' } } catch { $null }
        if ($progress) {
          Park-WindowOffScreen ([IntPtr]$progress.Current.NativeWindowHandle)
          try {
            $bytesCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProgressBytesText')
            $speedCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProgressSpeedText')
            $bytesText = $progress.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bytesCondition)
            $speedText = $progress.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $speedCondition)
            if ($bytesText -and $speedText) {
              $completedBeforeMetricCheck = $false
              foreach ($metric in @($bytesText, $speedText)) {
                $metricBounds = $metric.Current.BoundingRectangle
                if ([string]::IsNullOrWhiteSpace($metric.Current.Name) -or $metricBounds.Width -le 0 -or $metricBounds.Height -lt 16) {
                  # A very small local transfer can complete between locating the
                  # progress window and reading its children. Its closing peers are
                  # no longer evidence of clipping once the main UI reports success.
                  if ((Get-UiStatus) -match '同步完成') { $completedBeforeMetricCheck = $true; break }
                  throw 'Sync progress metric is empty or clipped.'
                }
              }
              if (-not $completedBeforeMetricCheck) {
                if ($speedText.Current.BoundingRectangle.Height -lt 48) {
                  if ((Get-UiStatus) -match '同步完成') { $completedBeforeMetricCheck = $true }
                  else { throw 'Sync speed metric does not reserve enough height for wrapped ETA text.' }
                }
                if (-not $completedBeforeMetricCheck) {
                  Capture-Element $progress ("sync-{0:D2}-progress.png" -f $script:syncScreenshotCount) 'sync-progress' $script:activeSyncLabel
                  $script:activeSyncProgressCaptured = $true
                }
              }
            }
          }
          catch [System.Windows.Automation.ElementNotAvailableException] { }
        }
      }
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
    $cause = $_.Exception.Message
    $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }
    $actual = try { if (Test-Path -LiteralPath $expectedFile) { [IO.File]::ReadAllText($expectedFile) } else { '<missing>' } } catch { '<unreadable>' }
    throw "Synchronization observation failed: $cause Expected file: $expectedFile. Expected content: $expectedContent. Actual: $actual. UI status: $status"
  }
  Wait-MainReadyAfterSync $transferSeconds
  Capture-Element (Get-LiveMain) ("sync-{0:D2}-complete.png" -f $script:syncScreenshotCount) 'sync-complete' $script:activeSyncLabel
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
  $compareTimer = [Diagnostics.Stopwatch]::StartNew()
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
    if (-not $compareEnabled -or $status -ne $statusBeforeClick -or ($compareTimer.ElapsedMilliseconds -ge 500 -and $status -match '比较完成')) { $accepted = $true }
    # A baseline safety notice intentionally remains as the final status for a
    # usable plan; scanning/analyzing text is transitional and must not be captured.
    $comparisonFinished = $status -match '比较完成|仅一端存在 sync\.fengdb；本次停用删除基线'
    if ($accepted -and $comparisonFinished -and $compareEnabled -and ($syncEnabled -or $allowNonExecutable)) {
      Write-HarnessTrace "Comparison completed. UI status: $status"
      $script:comparisonScreenshotCount++
      $mode = try { (Find-Id $liveMain 'SyncModeBox').Current.Name } catch { 'unknown-mode' }
      $leftKind = if ($left -match '^(?<kind>[a-z0-9]+)://') { $Matches.kind } else { 'local' }
      $rightKind = if ($right -match '^(?<kind>[a-z0-9]+)://') { $Matches.kind } else { 'local' }
      Capture-Element $liveMain ("compare-{0:D2}-{1}-to-{2}.png" -f $script:comparisonScreenshotCount, $leftKind, $rightKind) 'comparison' "$mode | $leftKind -> $rightKind | $status"
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
  # As early as possible: a desktop-free run moves the shell out of the visible desktop before any step can
  # raise it, so the user never sees the window at all.
  Initialize-TestWindowPlacement
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
function Capture-Element {
  param(
    [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$element,
    [Parameter(Mandatory)][string]$name,
    [Parameter(Mandatory)][string]$category,
    [string]$context = ''
  )
  if (-not $script:showOnScreen) {
    # A screen copy is impossible for a window parked off screen — it would capture the desktop there. The
    # geometry assertions that matter have already run against the same rectangles, so the run records that
    # the evidence was not taken rather than pretending it was. The bounds guard below is deliberately not
    # applied here: with no capture to protect it would only turn a window that happened to collapse between
    # being located and being described — a fast local sync closing its progress surface — into a failure.
    $screenshotManifest.Add([pscustomobject]@{ File = ''; Category = $category; Context = $context; CapturedUtc = [DateTimeOffset]::UtcNow; Note = "skipped: $name (desktop-free run; pass -ShowOnScreen to capture screenshots)" })
    Write-HarnessTrace "Screenshot skipped (desktop-free run): $name [$category] $context"
    return
  }
  $bounds = $element.Current.BoundingRectangle
  if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Cannot capture '$name' because the UI element has no visible bounds." }
  Activate-TestWindow
  $handle = [IntPtr]$element.Current.NativeWindowHandle
  if ($handle -ne [IntPtr]::Zero) { [void][FengSyncUiMouse]::SetForegroundWindow($handle) }
  Start-Sleep -Milliseconds 180
  $target = Join-Path $artifacts $name
  $image = [Drawing.Bitmap]::new([int][Math]::Ceiling($bounds.Width), [int][Math]::Ceiling($bounds.Height))
  $graphics = [Drawing.Graphics]::FromImage($image)
  try {
    $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $image.Size)
    $image.Save($target, [Drawing.Imaging.ImageFormat]::Png)
  }
  finally { $graphics.Dispose(); $image.Dispose() }
  if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -le 0) { throw "UI screenshot was not saved: $target" }
  $screenshotManifest.Add([pscustomobject]@{ File = $name; Category = $category; Context = $context; CapturedUtc = [DateTimeOffset]::UtcNow })
  Write-HarnessTrace "Screenshot captured: $name [$category] $context"
}
function Capture-Window { param($p, [string]$name, [string]$category = 'window', [string]$context = '')
  if (-not $p -or $p.MainWindowHandle -eq 0) { throw "Cannot capture '$name' because Feng Sync has no main window." }
  Capture-Element ([System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)) $name $category $context
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
  $content = @('LeftPath', 'RightPath', 'TotalSummary', 'UploadFilterButton', 'Comparison', 'Status') | ForEach-Object { Find-Id $main $_ }
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
  foreach ($side in @('Left', 'Right')) {
    $title = Find-Id $main "${side}EndpointTitle"
    $path = Find-Id $main "${side}Path"
    if ($title.Current.BoundingRectangle.Height -lt 14 -or $path.Current.BoundingRectangle.Height -lt 20) { throw "$label $side endpoint title or path is clipped." }
    if ($path.Current.BoundingRectangle.Top -lt ($title.Current.BoundingRectangle.Bottom - 1)) { throw "$label $side endpoint title overlaps its path." }
  }
}
function New-R2EndpointThroughUi { param($main, [string]$remoteName, [string]$bucketPath, [string]$accountId, [string]$accessKeyId, [string]$secretAccessKey)
  Click (Find-Id $main 'RemoteEndpointsButton')
  $manager = Find-WindowById 'RemoteEndpointManagerWindow' 30
  Click (Find-Name $manager '新建云盘端点')
  $editor = Find-WindowById 'CloudEndpointEditorWindow' 30
  $service = Find-Id $editor 'CloudServiceType'; $expand = $null
  $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null
  $expand.Expand(); Select-Ui (Find-Name $service 'S3 Bucket' 5); Start-Sleep -Milliseconds 200
  $providerBox = Find-Id $editor 'S3Provider'; $providerExpand = $null
  $providerBox.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$providerExpand) | Out-Null; $providerExpand.Expand()
  $cloudflare = Wait-Until { try { Find-Name $providerBox 'Cloudflare' 3 } catch { $null } } 'S3 Provider metadata did not load Cloudflare.' 30
  Select-Ui $cloudflare
  Click (Find-Id $editor 'NextCloudEndpoint'); Start-Sleep -Milliseconds 200
  Set-Text (Find-Id $editor 'S3DisplayName') $remoteName
  Set-Text (Find-Id $editor 'S3Region') 'auto'
  Set-Text (Find-Id $editor 'S3Endpoint') "https://$accountId.r2.cloudflarestorage.com"
  $bucketParts = $bucketPath.Trim('/').Split('/', 2)
  Set-Text (Find-Id $editor 'S3Bucket') $bucketParts[0]
  if ($bucketParts.Count -gt 1) { Set-Text (Find-Id $editor 'S3Subdirectory') $bucketParts[1] }
  Capture-Element $editor 'remote-editor-r2-before-credentials.png' 'remote-endpoint-editor' 'Cloudflare R2 fields before credentials'
  Set-Text (Find-Id $editor 'S3AccessKeyId') $accessKeyId
  Set-Password (Find-Id $editor 'S3SecretAccessKey') $secretAccessKey
  Click (Find-Id $editor 'SaveCloudEndpoint')
  Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudEndpointEditorWindow' }) } catch { $false } } 'S3 endpoint editor did not close after saving.' 60 | Out-Null
  $remoteItem = Find-NameContaining (Find-Id $manager 'CloudEndpointList') $remoteName 30
  Select-UiOrAncestor $remoteItem
  Wait-Until { try { (Find-Name $manager '.fengsync-fixture-anchor' 2).Current.Name -eq '.fengsync-fixture-anchor' } catch { $false } } 'Cloud file manager did not browse the R2 prefix created for this test.' 60 | Out-Null
  Capture-Element $manager 'remote-file-manager-r2.png' 'remote-file-manager' 'Cloudflare R2 isolated prefix listing'
  Close-Window $manager
  return "s3://$remoteName/$bucketPath"
}

$process = $null; $server = $null; $remoteCleanup = $null; $sftpUri = $null; $driveUri = $null; $driveChild = $null; $r2Uri = $null; $r2Child = $null; $scheduledTask = $null; $passed = $false
try {
  if ($Scenario -in @('sftp-to-local', 'sftp-ui')) {
    $fixture = Join-Path $root 'sftp'; $share = Join-Path $fixture 'share'
    New-Item -ItemType Directory -Force -Path $share | Out-Null
    [IO.File]::WriteAllText((Join-Path $share 'remote-proof.txt'), 'from-sftp')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $hostKey = Join-Path $root 'host-key.pem'
    & ssh-keygen.exe -q -t ed25519 -N '' -f $hostKey
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $hostKey)) { throw 'Could not generate the isolated SFTP host key.' }
    $password = 'ui-' + [Guid]::NewGuid().ToString('N'); $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($rclone); $start.Arguments = 'serve sftp ":local:' + $share + '" --addr 127.0.0.1:' + $port + ' --user ui --key "' + $hostKey + '" --vfs-cache-mode writes --cache-dir "' + (Join-Path $root 'sftp-cache') + '"'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.EnvironmentVariables['RCLONE_PASS']=$password
    $server = [Diagnostics.Process]::Start($start); Wait-Until { try { $tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Dispose();$true } catch {$false} } 'SFTP fixture did not start'
    if ($Scenario -eq 'sftp-to-local') { $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
      $obscuredPassword = & $rclone obscure $password; if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($obscuredPassword)) { throw 'Could not obscure the isolated SFTP password.' }
      & $rclone config create ui_sftp sftp host 127.0.0.1 user ui port "$port" pass $obscuredPassword --config $config; if ($LASTEXITCODE -ne 0) { throw 'Could not configure isolated SFTP remote.' }; $sftpUri='sftp://ui_sftp' }
  }
  if ($Scenario -in @('gdrive', 'gdrive-volume')) {
    Enable-RcloneWindowsProxy
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
    $remoteCleanup = [pscustomobject]@{
      Label = 'Google Drive'; Target = "${driveRemote}:test/FengSync-Automated-Tests/$driveChild"
      Parent = "${driveRemote}:test/FengSync-Automated-Tests"; Child = $driveChild; Config = $config
    }
  }
  if ($Scenario -in @('r2', 'r2-volume')) {
    Enable-RcloneWindowsProxy
    $accountId = $env:FENGSYNC_TEST_R2_ACCOUNT_ID
    $accessKeyId = $env:FENGSYNC_TEST_R2_ACCESS_KEY_ID
    $secretAccessKey = $env:FENGSYNC_TEST_R2_SECRET_ACCESS_KEY
    $sessionToken = $env:FENGSYNC_TEST_R2_SESSION_TOKEN
    $bucket = if ([string]::IsNullOrWhiteSpace($env:FENGSYNC_TEST_R2_BUCKET)) { 'feng-sync-e2e-test' } else { $env:FENGSYNC_TEST_R2_BUCKET }
    if ([string]::IsNullOrWhiteSpace($accountId) -or [string]::IsNullOrWhiteSpace($accessKeyId) -or [string]::IsNullOrWhiteSpace($secretAccessKey)) {
      Write-Output 'SKIPPED: Cloudflare R2 test credentials are not configured.'; exit 77
    }
    $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $config = Join-Path $root 'r2-fixture.conf'
    $r2Remote = 'fixture_r2'
    # Keep credentials out of the config file and command line. Both the fixture
    # rclone process and the application inherit these variables from this isolated host.
    $env:AWS_ACCESS_KEY_ID = $accessKeyId; $env:AWS_SECRET_ACCESS_KEY = $secretAccessKey
    if ([string]::IsNullOrWhiteSpace($sessionToken)) { Remove-Item Env:AWS_SESSION_TOKEN -ErrorAction SilentlyContinue }
    else { $env:AWS_SESSION_TOKEN = $sessionToken }
    $configLines = @(
      "[$r2Remote]", 'type = s3', 'provider = Cloudflare', 'env_auth = true', 'region = auto',
      "endpoint = https://$accountId.r2.cloudflarestorage.com", 'acl = private', 'no_check_bucket = true'
    )
    [IO.File]::WriteAllLines($config, $configLines, [Text.UTF8Encoding]::new($false))
    $r2Child = 'fengsync-ui-' + [Guid]::NewGuid().ToString('N')
    $r2Root = "FengSync-Automated-Tests/$r2Child"
    $r2Uri = "s3://$r2Remote/$bucket/$r2Root"
    $remoteCleanup = [pscustomobject]@{
      Label = 'Cloudflare R2'; Target = "${r2Remote}:$bucket/$r2Root"
      Parent = "${r2Remote}:$bucket/FengSync-Automated-Tests"; Child = $r2Child; Config = $config
    }
    $anchor = Join-Path $root '.fengsync-r2-fixture-anchor'; [IO.File]::WriteAllText($anchor, 'fixture')
    & $rclone copyto $anchor "${r2Remote}:$bucket/$r2Root/.fengsync-fixture-anchor" --config $config --contimeout 10s --timeout 30s
    if ($LASTEXITCODE -ne 0) { throw "Could not materialize Cloudflare R2 test child: $r2Uri" }
  }
  $launch = Start-App; $process = $launch[0]; $main = $launch[1]
  Capture-Window $process '01-main.png' 'main-window' 'initial application state'
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
      [IO.File]::WriteAllText((Join-Path $right 'download-proof.txt'), 'remote')
      Compare-Ui $main $left $right
      # New summary surface: a right-side panel with UploadSummary/DownloadSummary/etc.
      # TotalSummary is always populated once a comparison finishes successfully.
      $summary = Find-Id $main 'TotalSummary'
      if ($summary.Current.BoundingRectangle.Width -le 0 -or $summary.Current.BoundingRectangle.Height -le 0) { throw 'Change summary panel was clipped.' }
      # Status text only mentions "安全检查" for destructive plans; the
      # successful comparison above is the primary proof the engine ran.
      $status = (Find-Id $main 'Status' 2).Current.Name
      if ($status -notmatch '比较完成') { throw "Comparison did not complete: $status" }
      Capture-Window $process '02-shell.png' 'comparison' 'mixed upload and download comparison'
      $action = Find-Id $main 'ComparisonActionButton'
      # HelpText is what WPF exposes for a ToolTip, and UI Automation reads it without the pointer ever
      # being over the control — so the assertion holds in both modes.
      if ($action.Current.HelpText -notmatch '复制新项目到') { throw "Comparison action did not expose its hover explanation: $($action.Current.HelpText)" }
      if ($script:showOnScreen) {
        # Only a real hover makes WPF open the tooltip, so the screenshot of it needs the on-screen mode.
        $actionBounds = $action.Current.BoundingRectangle
        [Windows.Forms.Cursor]::Position = [Drawing.Point]::new([int]($actionBounds.Left + ($actionBounds.Width / 2)), [int]($actionBounds.Top + ($actionBounds.Height / 2)))
        Start-Sleep -Milliseconds 1600
      }
      Capture-Window $process '03-shell-tooltip.png' 'comparison-detail' 'comparison action tooltip'

      Click (Find-Id $main 'UploadFilterButton')
      Wait-Until { $items = (Find-Id $main 'Comparison').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); $items.Count -eq 1 } 'Upload summary filter did not narrow the comparison list.' | Out-Null
      Click (Find-Id $main 'ChangeSummaryTotal')
      Wait-Until { $items = (Find-Id $main 'Comparison').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); $items.Count -eq 2 } 'Total summary button did not restore all comparison rows.' | Out-Null
      $screenshot = Join-Path $artifacts '02-shell.png'
      # A desktop-free run records the screenshot as skipped, so only an on-screen run has a file to check.
      if ($script:showOnScreen -and (-not (Test-Path -LiteralPath $screenshot) -or (Get-Item -LiteralPath $screenshot).Length -le 0)) { throw 'Shell screenshot was not saved.' }
      if ($main.Current.BoundingRectangle.Width -le 0 -or $main.Current.BoundingRectangle.Height -le 0) { throw 'Main window is not visible after shell rendering.' }
      Write-HarnessTrace ("Shell rendering verified for mode: {0}; screenshot: {1}" -f $(if ($Scenario -eq 'ui-shell-software') { 'software' } else { 'native-default' }), $(if ($script:showOnScreen) { $screenshot } else { 'skipped (desktop-free run)' }))
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
      # Shrink instead of grow: a restored/default sidebar may already be at its
      # 320 px maximum, making a positive drag a legitimate no-op.
      $splitter = Find-Id $main 'SidebarSplitter'
      if ($script:showOnScreen) {
        Drag-ElementHorizontally $splitter -60
        # GridSplitter also supports keyboard resizing for accessibility.
        try { Activate-TestWindow; $splitter.SetFocus(); Start-Sleep -Milliseconds 100; Send-KeysToWindow $splitter '{LEFT 5}' } catch { }
      } else {
        # A posted drag cannot move a GridSplitter: WPF hit-tests mouse input against the real pointer, which
        # a desktop-free run never places over the window. The arrow keys the splitter also accepts are
        # delivered as posted keystrokes instead — six presses at the app's 12 px step is the same 72 px the
        # drag asked for.
        Send-KeysToWindow $splitter '{LEFT 6}'
      }
      try { Wait-Until { (Find-Id (Get-LiveMain) 'ProfileList').Current.BoundingRectangle.Width -lt ($beforeWidth - 25) } 'Sidebar width did not change after dragging the splitter.' 15 | Out-Null }
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
      Capture-Element $settings '02-update-settings.png' 'settings' 'update preference after reopening'
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
      Find-Id $about 'AboutCheckUpdates' | Out-Null; Capture-Element $about '02-about.png' 'about' 'built product version'
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
      Capture-Element (Get-LiveMain) 'compare-conflict-resolved-right-to-left.png' 'comparison-override' 'two-way conflict resolved with right side overwriting left'
      Wait-Sync $main (Join-Path $left 'initial.txt') 120 120 'right-change'; Assert-File (Join-Path $left 'initial.txt') 'right-change'
    }
    'local-move' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $profileName = 'ui-move-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Update-CurrentProfileEndpoints $main $profileName $left $right
      [IO.File]::WriteAllText((Join-Path $left 'before-name.txt'), 'move-proof')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'before-name.txt') 120 120 'move-proof'
      Stop-App $process; $process = $null; $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      Move-Item -LiteralPath (Join-Path $left 'before-name.txt') -Destination (Join-Path $left 'after-name.txt')
      Start-Sleep -Milliseconds 2200
      Compare-Ui $main $left $right 120 $true
      Capture-Element (Get-LiveMain) 'compare-move-detected-unselected.png' 'comparison-move' 'medium-confidence move detected and awaiting confirmation'
      $moveRow = Get-ComparisonRow $main
      $moveBox = Wait-Until { $items = $moveRow.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)); if ($items.Count -gt 0) { $items[0] } } 'Move selection checkbox was not exposed.'
      if ((Get-ToggleState $moveBox) -ne [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $moveBox }
      Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Confirmed move did not become executable.' | Out-Null
      Capture-Element (Get-LiveMain) 'compare-move-confirmed.png' 'comparison-move' 'medium-confidence move explicitly selected'
      Wait-Sync $main (Join-Path $right 'after-name.txt') 120 120 'move-proof'
      if (Test-Path -LiteralPath (Join-Path $right 'before-name.txt')) { throw 'Move comparison did not remove the old right-side path.' }
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
      Capture-Element (Get-LiveMain) 'compare-mirror-delete-right.png' 'comparison' 'mirror comparison with right-side deletions'
      Click (Find-Id $main 'SyncButton')
      # Mirror is high-risk; SyncConfirmationWindow now exposes ConfirmSyncButton for that case.
      $confirm = Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null } } 'Mirror confirmation window did not appear.' 30
      Capture-Element $confirm 'sync-mirror-confirmation.png' 'sync-confirmation' 'mirror deletion confirmation'
      Click (Find-Id $confirm 'ConfirmSyncButton')
      Capture-Element (Get-LiveMain) 'sync-mirror-started.png' 'sync-started' 'mirror deletion synchronization'
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'remove-in-mirror.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'right-only.txt')) } 'Mirror did not delete every right-only file' 60
      Wait-MainReadyAfterSync 60
      Capture-Element (Get-LiveMain) 'sync-mirror-complete.png' 'sync-complete' 'mirror deletion synchronization'
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
      Capture-Element (Get-LiveMain) 'compare-selection-excluded.png' 'comparison-selection' 'only planned item deselected'
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
      Capture-Element $manager 'remote-endpoint-manager.png' 'remote-endpoints' 'remote endpoint manager'
      Click (Find-Name $manager '新建云盘端点')
      $editor = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like '*新建云端*' } } catch { $null } } 'Cloud editor did not appear' 30
      $service = Find-Id $editor 'CloudServiceType'; $expand = $null
      # Every Provider's form is generated from its CloudProviderDescriptor row, so each one is walked here:
      # select it, open its connection step, and require every AutomationId the descriptor names to be a
      # laid-out control. Selecting a service and only screenshotting step 1 would prove nothing — a provider
      # whose ids were never registered, or a panel that kept the previous provider's inputs, is invisible to
      # the user until a field cannot be reached. This is the counter-example for "adding a Provider is one
      # row here plus its fields": the row is only finished once the scenario can drive its form.
      # S3 is first because its bespoke panel needs the provider metadata to load.
      $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null
      $expand.Expand(); Start-Sleep -Milliseconds 200; Select-Ui (Find-Name $service 'S3 Bucket' 20); Start-Sleep -Milliseconds 200
      try { $expand.Collapse() } catch { }
      $providerBox = Find-Id $editor 'S3Provider'; $providerExpand = $null
      $providerBox.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$providerExpand) | Out-Null; $providerExpand.Expand()
      $cloudflare = Wait-Until { try { Find-Name $providerBox 'Cloudflare' 3 } catch { $null } } 'S3 Provider metadata did not load Cloudflare.' 30
      Select-Ui $cloudflare
      Click (Find-Id $editor 'NextCloudEndpoint'); Start-Sleep -Milliseconds 200
      foreach ($id in @('S3DisplayName', 'S3AccessKeyId', 'S3SecretAccessKey', 'S3Endpoint', 'S3Region', 'S3Bucket', 'S3Subdirectory', 'TestCloudEndpoint', 'SaveCloudEndpoint')) { Assert-RectangleInside (Find-Id $editor $id) $editor "S3 editor/$id" }
      Capture-Element $editor 'remote-editor-s3-connection.png' 'remote-endpoint-editor' 'S3 connection fields'
      Click (Find-Id $editor 'BackCloudEndpoint'); Start-Sleep -Milliseconds 200

      $providerForms = @(
        [pscustomobject]@{ Service = 'Google Drive'; Name = 'GDriveRemoteName'; Root = 'GDriveRemoteRoot'; Fields = @('OAuthClientId', 'OAuthClientSecret') },
        [pscustomobject]@{ Service = 'OneDrive'; Name = 'OneDriveRemoteName'; Root = 'OneDriveRemoteRoot'; Fields = @('OAuthClientId', 'OAuthClientSecret') },
        [pscustomobject]@{ Service = 'Dropbox'; Name = 'DropboxRemoteName'; Root = 'DropboxRemoteRoot'; Fields = @('OAuthClientId', 'OAuthClientSecret') },
        [pscustomobject]@{ Service = 'Box'; Name = 'BoxRemoteName'; Root = 'BoxRemoteRoot'; Fields = @('OAuthClientId', 'OAuthClientSecret') },
        [pscustomobject]@{ Service = 'WebDAV'; Name = 'WebDavRemoteName'; Root = 'WebDavRemoteRoot'; Fields = @('WebDavUrl', 'WebDavVendor', 'WebDavUser', 'WebDavPassword') },
        [pscustomobject]@{ Service = 'SFTP'; Name = 'SftpRemoteName'; Root = 'SftpRemoteRoot'; Fields = @('SftpRemoteHost', 'SftpRemotePort', 'SftpRemoteUser', 'SftpRemotePassword', 'SftpRemoteKeyFile') }
      )
      $allFormIds = @()
      foreach ($form in $providerForms) { $allFormIds += @($form.Name, $form.Root) + $form.Fields }
      $allFormIds = @($allFormIds | Select-Object -Unique)
      foreach ($form in $providerForms) {
        $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Start-Sleep -Milliseconds 200; Select-Ui (Find-Name $service $form.Service 5); Start-Sleep -Milliseconds 200
        try { $expand.Collapse() } catch { }
        Click (Find-Id $editor 'NextCloudEndpoint'); Start-Sleep -Milliseconds 300
        Scroll-ToTop $editor
        $formIds = @($form.Name, $form.Root) + $form.Fields
        foreach ($id in $formIds) {
          $field = Find-Id $editor $id
          $rect = $field.Current.BoundingRectangle
          if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "$($form.Service) form rendered $id with no size." }
        }
        # The top and bottom anchors of the generated stack are the two that must be reachable without
        # scrolling; everything the descriptor lists sits between them.
        Assert-RectangleInside (Find-Id $editor $form.Name) $editor "$($form.Service) display name"
        Assert-RectangleInside (Find-Id $editor $form.Root) $editor "$($form.Service) root directory"
        # A field belonging to another Provider must not be on this form. The rclone parameters the editor
        # writes come from whatever inputs it reads, so a leftover field would put another backend's key on
        # this remote. AutomationId is matched exactly, so the shared OAuth boxes are only foreign to WebDAV
        # and SFTP — and the per-provider name and root boxes are foreign everywhere.
        foreach ($id in ($allFormIds | Where-Object { $formIds -notcontains $_ })) {
          $foreign = $null
          try { $foreign = $editor.FindFirst([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) } catch { }
          if ($foreign) { throw "$($form.Service) form shows a field belonging to another Provider: $id" }
        }
        Capture-Element $editor ("remote-editor-{0}.png" -f ($form.Service.ToLowerInvariant().Replace(' ', '-'))) 'remote-endpoint-editor' "$($form.Service) settings"
        Click (Find-Id $editor 'BackCloudEndpoint'); Start-Sleep -Milliseconds 200
      }
      Click (Find-Id $editor 'NextCloudEndpoint'); Start-Sleep -Milliseconds 200
      Set-Text (Find-Id $editor 'SftpRemoteName') $remoteName; Set-Text (Find-Id $editor 'SftpRemoteHost') '127.0.0.1'; Set-Text (Find-Id $editor 'SftpRemotePort') "$port"; Set-Text (Find-Id $editor 'SftpRemoteUser') 'ui'; Set-Text (Find-Id $editor 'SftpRemoteRoot') ''
      Capture-Element $editor 'remote-editor-sftp-configured-without-password.png' 'remote-endpoint-editor' 'SFTP fields populated before entering password'
      Set-Password (Find-Id $editor 'SftpRemotePassword') $password
      Click (Find-Id $editor 'SaveCloudEndpoint')
      Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudEndpointEditorWindow' }) } catch { $false } } 'Cloud endpoint editor did not close after saving.' 30 | Out-Null
      $config = Join-Path $appData 'rclone\rclone.conf'
      $remoteItem = Find-NameContaining (Find-Id $manager 'CloudEndpointList') $remoteName 30
      Select-UiOrAncestor $remoteItem
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'Cloud file manager did not browse the endpoint created through its editor.' 30 | Out-Null
      Capture-Element $manager 'remote-file-manager-sftp.png' 'remote-file-manager' 'SFTP endpoint directory listing'
      # Editing an existing endpoint has to round-trip the password rclone keeps obscured: a rewrite that
      # obscured it a second time would corrupt it, and nothing about the save would report an error — the
      # endpoint would simply stop authenticating. Re-browsing after the save is what catches that.
      Click (Find-Id $manager 'EditCloudEndpoint')
      $edit = Wait-Until { try { Find-AppWindow { param($window) $window.Current.Name -like '*编辑云端*' } } catch { $null } } 'Cloud endpoint edit window did not appear' 30
      if ((Get-Text (Find-Id $edit 'SftpRemoteName')) -ne $remoteName) { throw 'The edit form did not show the name of the endpoint it is editing.' }
      if ((Get-Text (Find-Id $edit 'SftpRemoteHost')) -ne '127.0.0.1') { throw 'The edit form did not pre-fill the saved host.' }
      if ((Get-Text (Find-Id $edit 'SftpRemoteUser')) -ne 'ui') { throw 'The edit form did not pre-fill the saved user.' }
      Capture-Element $edit 'remote-endpoint-editor-edit.png' 'remote-endpoint-editor' 'editing an existing SFTP endpoint'
      Click (Find-Id $edit 'SaveCloudEndpoint')
      Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudEndpointEditorWindow' }) } catch { $false } } 'Cloud endpoint editor did not close after saving an edit.' 30 | Out-Null
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'The edited endpoint stopped browsing its directory.' 30 | Out-Null
      # Renaming has no rclone operation behind it: the section in rclone.conf is rewritten and the running
      # daemon has to pick the change up. Browsing the renamed endpoint is what proves both halves at once —
      # that the daemon resolved the new name, and that the obscured password survived the section copy.
      # Creating a copy instead of moving would leave the old name behind, which the row scan below catches.
      $renamedName = 'ui_renamed_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Click (Find-Id $manager 'RenameCloudEndpoint')
      $renamePrompt = Find-WindowLike '重命名端点' 30
      Set-Text (Find-Id $renamePrompt 'TextPromptNameBox') $renamedName
      Click (Find-Id $renamePrompt 'TextPromptConfirm')
      Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.Name -eq '重命名端点' }) } catch { $false } } 'The rename prompt did not close.' 30 | Out-Null
      Wait-Until { try { (Find-NameContaining (Find-Id $manager 'CloudEndpointList') $renamedName 2).Current.Name -like "*$renamedName*" } catch { $false } } 'The endpoint list did not show the renamed endpoint.' 30 | Out-Null
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'The renamed endpoint stopped browsing its directory.' 30 | Out-Null
      $staleRows = @()
      foreach ($row in (Find-Id $manager 'CloudEndpointList').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($row.Current.Name -like "*$remoteName*") { $staleRows += $row.Current.Name }
      }
      if ($staleRows.Count -gt 0) { throw "The old endpoint name survived the rename: $($staleRows -join '; ')" }
      Capture-Element $manager 'remote-file-manager-sftp-renamed.png' 'remote-file-manager' 'the SFTP endpoint after being renamed'
      # The endpoint list's toolbar sits in a 340 px column, so every control in it has to survive the wrap
      # rather than being clipped away.
      foreach ($id in @('CloudEndpointSearch', 'CloudEndpointSort', 'CloudEndpointSortDirection', 'CheckAllCloudEndpoints', 'ExportCloudEndpoints', 'ImportCloudEndpoints')) { Assert-RectangleInside (Find-Id $manager $id) $manager "endpoint list/$id" }
      # Searching is not a cosmetic filter: a query that matches nothing has to empty the file pane too, and a
      # query that matches the endpoint has to bring both back. Asserting only the list would miss a pane left
      # showing the contents of an endpoint the user can no longer see.
      Set-Text (Find-Id $manager 'CloudEndpointSearch') 'no-endpoint-matches-this'
      Wait-Until { try { $null -eq (Find-NameContaining (Find-Id $manager 'CloudEndpointList') $renamedName 1) } catch { $true } } 'The search box did not hide the endpoint it does not match.' 15 | Out-Null
      Wait-Until { try { $null -eq (Find-Name $manager 'remote-proof.txt' 1) } catch { $true } } 'The file pane kept listing an endpoint the search box had hidden.' 15 | Out-Null
      Set-Text (Find-Id $manager 'CloudEndpointSearch') $renamedName
      Wait-Until { try { (Find-NameContaining (Find-Id $manager 'CloudEndpointList') $renamedName 2).Current.Name -like "*$renamedName*" } catch { $false } } 'The search box hid the endpoint it does match.' 15 | Out-Null
      Set-Text (Find-Id $manager 'CloudEndpointSearch') ''
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'Clearing the search box did not restore the browser.' 30 | Out-Null
      # Re-ordering is a view change, not a navigation: the endpoint stays selected and its files stay on
      # screen. A sort that re-selected would be one round trip to the cloud service per click.
      $sortBox = Find-Id $manager 'CloudEndpointSort'
      $sortExpand = $null
      if (-not $sortBox.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$sortExpand)) { throw 'The endpoint sort picker has no expand pattern.' }
      $sortExpand.Expand(); Start-Sleep -Milliseconds 200
      Select-Ui (Find-Name $sortBox '名称' 5); Start-Sleep -Milliseconds 200
      try { $sortExpand.Collapse() } catch { }
      $sortSelection = $null
      if (-not $sortBox.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$sortSelection)) { throw 'The endpoint sort picker has no selection pattern.' }
      $chosenSort = $sortSelection.Current.GetSelection()[0].Current.Name
      if ($chosenSort -ne '名称') { throw "The endpoint sort picker did not take the choice: '$chosenSort'." }
      Click (Find-Id $manager 'CloudEndpointSortDirection')
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'Re-ordering the endpoint list emptied the file pane.' 30 | Out-Null
      Capture-Element $manager 'remote-file-manager-sftp-searched.png' 'remote-file-manager' 'the endpoint list after searching and re-ordering'
      # "测试全部" goes through the running daemon to the SFTP server this scenario started, so the row has to
      # come back saying it answered — and the status line has to report the run rather than leaving the row
      # reading 未检查, which is what a button wired to nothing would look like.
      Click (Find-Id $manager 'CheckAllCloudEndpoints')
      Wait-Until { try { (Find-NameContaining (Find-Id $manager 'CloudEndpointList') '已连接' 2).Current.Name -like '*已连接*' } catch { $false } } '“测试全部” did not report the reachable endpoint as connected.' 90 | Out-Null
      Wait-Until { try { $null -ne (Find-NameContaining $manager '检查结束' 2) } catch { $false } } 'The status line did not report the result of checking every endpoint.' 90 | Out-Null
      Assert-NoUiFailure 'check-every-endpoint' | Out-Null
      # Grouping and tagging live in this application's own metadata file, so the only proof that the dialog
      # writes them is to reopen it and read the boxes back. The heading and the tag search are what make the
      # two fields worth setting, so both are checked through the list rather than through the store.
      Click (Find-Id $manager 'OrganizeCloudEndpoint')
      $organize = Find-WindowById 'CloudEndpointOrganizationWindow'
      Set-Text (Find-Id $organize 'EndpointGroupBox') 'ui-group'
      Set-Text (Find-Id $organize 'EndpointTagsBox') 'ui-tag, second-tag'
      Capture-Element $organize 'remote-endpoint-organization.png' 'remote-file-manager' 'grouping and tagging the endpoint'
      Click (Find-Id $organize 'EndpointOrganizationOk')
      Wait-Until { try { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudEndpointOrganizationWindow' }) } catch { $false } } 'The grouping dialog did not close after saving.' 30 | Out-Null
      Wait-Until { try { $null -ne (Find-NameContaining (Find-Id $manager 'CloudEndpointList') 'ui-group' 2) } catch { $false } } 'The endpoint list did not show the group heading.' 30 | Out-Null
      Set-Text (Find-Id $manager 'CloudEndpointSearch') 'second-tag'
      Wait-Until { try { $null -ne (Find-NameContaining (Find-Id $manager 'CloudEndpointList') $renamedName 2) } catch { $false } } 'Searching by tag did not find the tagged endpoint.' 30 | Out-Null
      Set-Text (Find-Id $manager 'CloudEndpointSearch') ''
      Wait-Until { try { (Find-Name $manager 'remote-proof.txt' 2).Current.Name -eq 'remote-proof.txt' } catch { $false } } 'Clearing the tag search did not restore the browser.' 30 | Out-Null
      Click (Find-Id $manager 'OrganizeCloudEndpoint')
      $organize = Find-WindowById 'CloudEndpointOrganizationWindow'
      if ((Get-Text (Find-Id $organize 'EndpointGroupBox')) -ne 'ui-group') { throw "The endpoint's group was not persisted: '$(Get-Text (Find-Id $organize 'EndpointGroupBox'))'." }
      if ((Get-Text (Find-Id $organize 'EndpointTagsBox')) -ne 'ui-tag、second-tag') { throw "The endpoint's tags were not persisted: '$(Get-Text (Find-Id $organize 'EndpointTagsBox'))'." }
      Close-Window $organize
      Assert-NoUiFailure 'organize-endpoint' | Out-Null
      Close-Window $manager
      # Build the sftp:// URI directly so the test does not depend on the manager's browse workflow. It uses
      # the renamed endpoint, so a rename that left the credentials behind fails here too.
      $upload = Join-Path $root 'ui-upload'; New-Item -ItemType Directory -Force -Path $upload | Out-Null; [IO.File]::WriteAllText((Join-Path $upload 'created-through-ui.txt'), 'endpoint-ui-proof')
      $sftpUriCreated = "sftp://$renamedName"
      if (-not (Test-Path -LiteralPath $config)) { throw 'SFTP endpoint editor did not persist its rclone configuration.' }
      Select-Mode $main '更新到右侧'; Compare-Ui $main $upload $sftpUriCreated; Wait-Sync $main (Join-Path $share 'created-through-ui.txt'); Assert-File (Join-Path $share 'created-through-ui.txt') 'endpoint-ui-proof'
    }
    'sftp-service' {
      $share = Join-Path $root 'built-in-sftp-share'; New-Item -ItemType Directory -Force -Path $share | Out-Null
      [IO.File]::WriteAllText((Join-Path $share 'service-proof.txt'), 'built-in-sftp')
      $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
      $settings = Open-SettingsCenter
      Click (Find-Id $settings 'SftpServerSettingsButton')
      $sftpSettings = Find-WindowById 'SftpServerSettingsWindow'
      Set-Text (Find-Id $sftpSettings 'SftpListenAddress') '127.0.0.1'
      Set-Text (Find-Id $sftpSettings 'SftpPort') "$port"
      Set-Text (Find-Id $sftpSettings 'SftpRootPath') $share
      Set-Text (Find-Id $sftpSettings 'SftpUserName') 'ui-service'
      Set-Text (Find-Id $sftpSettings 'SftpCacheSize') '1'
      Click (Find-Id $sftpSettings 'SetSftpPassword')
      $passwordDialog = Find-WindowById 'SftpPasswordDialog'
      $servicePassword = 'ui-service-' + [Guid]::NewGuid().ToString('N')
      Set-Password (Find-Id $passwordDialog 'SftpPassword') $servicePassword
      Click (Find-Id $passwordDialog 'SaveSftpPassword')
      Click (Find-Id $sftpSettings 'StartSftpServer')
      Wait-Until {
        try { $tcp = [Net.Sockets.TcpClient]::new(); $tcp.Connect('127.0.0.1', $port); $tcp.Dispose(); $true } catch { $false }
      } 'Built-in SFTP service did not open its configured port.' 30 | Out-Null
      Wait-Until { (Find-Id $sftpSettings 'SftpServerStatus').Current.Name -match '正在监听' } 'Built-in SFTP status did not report the listening endpoint.' 30 | Out-Null
      Capture-Element $sftpSettings 'sftp-service-running.png' 'sftp-server-settings' 'built-in SFTP service running'
      Click (Find-Id $sftpSettings 'StopSftpServer')
      Wait-Until {
        try { $tcp = [Net.Sockets.TcpClient]::new(); $tcp.Connect('127.0.0.1', $port); $tcp.Dispose(); $false } catch { $true }
      } 'Built-in SFTP service did not release its configured port.' 30 | Out-Null
      Wait-Until { (Find-Id $sftpSettings 'SftpServerStatus').Current.Name -match '端口已释放' } 'Built-in SFTP status did not report a clean stop.' 30 | Out-Null
      Close-Window $sftpSettings; Close-Window $settings
    }
    'cloud-transfers' {
      # The standalone transfer window is the only surface that owns upload/download progress,
      # cancellation and 断点续传, and it is reachable from the real shell without any credential:
      # the cloud manager opens with no endpoints configured. This pins the wiring the two windows
      # depend on, which no other scenario touches.
      Click (Find-Id $main 'RemoteEndpointsButton')
      $manager = Find-WindowById 'RemoteEndpointManagerWindow' 30
      # The manager reports aggregate transfer progress, so its bar must exist before anything is queued.
      if (-not (Find-Name $manager '文件传输进度' 10)) { throw 'The cloud manager did not expose its transfer progress bar.' }
      Capture-Element $manager 'cloud-manager-empty.png' 'cloud-manager' 'cloud manager before any endpoint exists'
      Click (Find-Id $manager 'OpenCloudTransfers')
      $transfers = Find-WindowById 'CloudTransferWindow' 30
      # Both TextBlocks expose their live text as the accessible name, so the status is what a screen
      # reader reads and what this scenario asserts.
      $summary = (Find-Id $transfers 'CloudTransferSummary').Current.Name
      if ($summary -notmatch '暂无传输任务') { throw "The transfer window did not start empty: '$summary'." }
      # With nothing queued every action must be off rather than silently doing nothing.
      foreach ($id in @('ResumeCloudTransfers', 'CancelAllCloudTransfers', 'ClearCloudTransfers')) {
        if ((Find-Id $transfers $id 5).Current.IsEnabled) { throw "The transfer window enabled $id with no transfers queued." }
      }
      $hint = (Find-Id $transfers 'CloudTransferHint').Current.Name
      if ($hint -notmatch '拖入' -or $hint -notmatch '拖到') { throw "The transfer window did not explain how a transfer starts: '$hint'." }
      Capture-Element $transfers 'cloud-transfers-empty.png' 'cloud-transfers' 'standalone transfer window with nothing queued'
      Close-Window $transfers
      Wait-Until { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'CloudTransferWindow' }) } 'The transfer window did not close.' 30 | Out-Null
      Close-Window $manager
      Wait-Until { $null -eq (Find-AppWindow { param($window) $window.Current.AutomationId -eq 'RemoteEndpointManagerWindow' }) } 'The cloud manager did not close.' 30 | Out-Null
    }
    'batch-run' {
      $left = Join-Path $root 'batch-left'; $right = Join-Path $root 'batch-right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'batch-proof.txt'), 'batch-window-proof')
      Update-CurrentProfileEndpoints $main ('ui-batch-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) $left $right
      Click (Find-Id $main 'BatchJobsButton')
      $batch = Find-WindowById 'BatchRunWindow'
      if (-not (Find-Id $batch 'BatchRunProfiles')) { throw 'Batch window did not expose its saved-profile queue.' }
      Click (Find-Id $batch 'BatchRunStart')
      Wait-Until { (Find-Id $batch 'BatchRunSummary').Current.Name -match '^完成：1 成功，0 失败。$' } 'Batch window did not report a successful completed queue.' 120 | Out-Null
      Assert-File (Join-Path $right 'batch-proof.txt') 'batch-window-proof'
      Capture-Element $batch 'batch-run-complete.png' 'batch-run' 'saved profile completed through batch window'
      Close-Window $batch
    }
    'profile' {
      $left = Join-Path $root 'profile-left'; $right = Join-Path $root 'profile-right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $profileName = 'ui-profile-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      $script:deletingProfileName = $profileName
      Click (Find-Id $main 'NewProfileButton')
      # Edit through the top header's EditCurrentProfileButton (more deterministic than the context menu).
      Click (Find-Id $main 'EditCurrentProfileButton')
      $editor = Find-WindowById 'ProfileEditorWindow'
      Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'
      foreach ($sectionName in @('常规', '比较', '过滤器', '同步', '版本管理', '性能与可靠性')) {
        Select-Ui (Find-Name $sections $sectionName 5); Start-Sleep -Milliseconds 180
        Capture-Element $editor ("profile-editor-{0}.png" -f $sectionName) 'profile-editor' $sectionName
      }
      Click (Find-Id $editor 'ProfileSave')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已保存|Profile 设置已保存' } 'Profile edit did not report a save.'
      # A cancelled edit must not leak into the in-memory UI nor the persisted profile.
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileName') 'must-not-persist'; Click (Find-Id $editor 'ProfileCancel')
      if ((Find-Id $main 'ProfileList').Current.Name -match 'must-not-persist') { throw 'Cancelled profile edit changed the profile list.' }
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      $item = Find-Name (Find-Id $main 'ProfileList') $profileName 2
      Select-UiOrAncestor $item
      Wait-Until { (Get-Text (Find-Id $main 'LeftPath')) -eq $left } 'Persisted Profile did not load after selecting it.'
      if ((Get-Text (Find-Id $main 'LeftPath')) -ne $left -or (Get-Text (Find-Id $main 'RightPath')) -ne $right) { throw 'Persisted Profile endpoints did not survive application restart.' }
      Delete-Profile-ThroughContextMenu
      Wait-Until { try { -not (Find-Name (Find-Id $main 'ProfileList') $profileName 1) } catch { $true } } 'Deleted Profile remained in the list.'
    }
    'profile-filter' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right, (Join-Path $left '.git')) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'included.txt'), 'included'); [IO.File]::WriteAllText((Join-Path $left '.git\config'), 'must-be-filtered')
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; Select-Ui (Find-Name $sections '过滤器' 5)
      Click (Find-Name $editor '添加常用排除规则'); Capture-Element $editor 'profile-filter-configured.png' 'profile-editor' 'filter rules'; Click (Find-Id $editor 'ProfileSave')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'included.txt'); Assert-File (Join-Path $right 'included.txt') 'included'
      if (Test-Path -LiteralPath (Join-Path $right '.git\config')) { throw 'Profile filter did not exclude .git content from the real sync.' }
    }
    'delete-threshold' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'delete-a.txt'), 'a'); [IO.File]::WriteAllText((Join-Path $right 'delete-b.txt'), 'b')
      $profileName = 'threshold-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Click (Find-Id $main 'EditCurrentProfileButton'); $editor = Find-WindowById 'ProfileEditorWindow'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; Select-Ui (Find-Name $sections '性能与可靠性' 5)
      # The resume threshold only accepts input once the profile stops inheriting the application default.
      $useDefaultResume = Find-Id $editor 'ProfileUseDefaultResume'
      if ((Get-ToggleState $useDefaultResume) -eq [System.Windows.Automation.ToggleState]::On) { Toggle-Ui $useDefaultResume }
      Set-Text (Find-Id $editor 'ProfileResumeMinimum') '1G'
      Set-Text (Find-Id $editor 'ProfileMaxDeletes') '0'; Set-Text (Find-Id $editor 'ProfileMaxDeleteRatio') '0'; Capture-Element $editor 'profile-delete-threshold.png' 'profile-editor' 'performance and destructive safety'; Click (Find-Id $editor 'ProfileSave')
      Select-Mode $main '镜像到右侧'; Compare-Ui $main $left $right; Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror threshold plan was not executable.'
      Click (Find-Id $main 'SyncButton'); $confirm = Wait-Until { try { Find-AppWindow { param($window) $window.Current.AutomationId -eq 'SyncConfirmationWindow' } } catch { $null } } 'Threshold confirm window did not appear' 30
      Set-Text (Find-Id $confirm 'ProfileNameInput') $profileName; Capture-Element $confirm 'sync-delete-threshold-confirmation.png' 'sync-confirmation' 'profile-name destructive confirmation'; Click (Find-Id $confirm 'ConfirmSyncButton')
      Capture-Element (Get-LiveMain) 'sync-delete-threshold-started.png' 'sync-started' 'destructive mirror synchronization'
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'delete-a.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'delete-b.txt')) } 'Profile-name threshold confirmation did not permit mirror deletion.' 60
      Wait-MainReadyAfterSync 60
      Capture-Element (Get-LiveMain) 'sync-delete-threshold-complete.png' 'sync-complete' 'destructive mirror synchronization'
    }
    'settings' {
      $settings = Open-SettingsCenter
      # The General page is selected by default; the previously separate Defaults tab is gone.
      Set-Text (Find-Id $settings 'SettingsConcurrency') '3'; Set-Text (Find-Id $settings 'SettingsTimeTolerance') '7'; Set-Text (Find-Id $settings 'SettingsIncludeRules') '**/*.keep'; Set-Text (Find-Id $settings 'SettingsExcludeRules') '**/*.skip'
      # The resume threshold is a byte size, so it must survive a save/reopen as the size the user typed.
      Set-Text (Find-Id $settings 'SettingsResumeMinimum') '2G'
      Capture-Element $settings 'settings-general-modified.png' 'settings' 'general settings with unsaved changes'
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已应用|程序设置已应用' } 'Settings did not report apply.'
      $settings = Open-SettingsCenter
      if ((Get-Text (Find-Id $settings 'SettingsConcurrency')) -ne '3' -or (Get-Text (Find-Id $settings 'SettingsTimeTolerance')) -ne '7' -or (Get-Text (Find-Id $settings 'SettingsIncludeRules')) -ne '**/*.keep' -or (Get-Text (Find-Id $settings 'SettingsExcludeRules')) -ne '**/*.skip') { throw 'Applied application defaults were not persisted on re-open.' }
      if ((Get-Text (Find-Id $settings 'SettingsResumeMinimum')) -ne '2G') { throw "The resume threshold was not persisted as the size that was entered: '$(Get-Text (Find-Id $settings 'SettingsResumeMinimum'))'." }
      Capture-Element $settings 'settings-general-persisted.png' 'settings' 'persisted general settings'
      # The cloud page's proxy field is the only place the value can be typed, so it has to survive a
      # save/reopen like the general page's numbers do — a value that silently reverts would only ever
      # show up later as a cloud endpoint that cannot connect.
      Select-Ui (Find-Id $settings 'SettingsCloudNav'); Start-Sleep -Milliseconds 200
      Set-Text (Find-Id $settings 'SettingsCloudProxy') 'proxy.invalid:8080'
      Capture-Element $settings 'settings-cloud-proxy.png' 'settings' 'cloud page with a proxy address'
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已应用' } 'Applying the cloud proxy was not reported.'
      $settings = Open-SettingsCenter
      Select-Ui (Find-Id $settings 'SettingsCloudNav'); Start-Sleep -Milliseconds 200
      $persistedProxy = Get-Text (Find-Id $settings 'SettingsCloudProxy')
      if ($persistedProxy -ne 'proxy.invalid:8080') { throw "The cloud proxy was not persisted on re-open: '$persistedProxy'." }
      # Clear it again before moving on. The setting is read once, when this process's rclone daemon
      # starts, so leaving a deliberately unreachable address behind would break every later cloud step
      # that happens to be the first one to need the daemon.
      Set-Text (Find-Id $settings 'SettingsCloudProxy') ''
      Click (Find-Id $settings 'SettingsApply')
      if ((Get-Text (Find-Id $settings 'SettingsCloudProxy')) -ne '') { throw 'Clearing the cloud proxy did not take effect.' }
      Select-Ui (Find-Id $settings 'RunHistoryNav'); Start-Sleep -Milliseconds 200; Capture-Element $settings 'settings-run-history.png' 'settings' 'run history settings page'
      Select-Ui (Find-Id $settings 'LogsNav'); Start-Sleep -Milliseconds 200; Capture-Element $settings 'settings-logs.png' 'settings' 'logs settings page'
      Select-Ui (Find-Id $settings 'SettingsGeneralNav'); Start-Sleep -Milliseconds 200
      Click (Find-Id $settings 'SftpServerSettingsButton')
      $sftpSettings = Find-WindowById 'SftpServerSettingsWindow'
      Capture-Element $sftpSettings 'settings-sftp-server.png' 'sftp-server-settings' 'SFTP server settings without credential changes'
      Close-Window $sftpSettings
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
      Capture-Element $settings 'history-settings-launcher.png' 'settings' 'run history launcher'
      $launcher = Find-Id $settings 'OpenRunHistory' 5
      Click $launcher
      $history = Find-WindowById 'RunHistoryWindow'
      Click (Find-Id $history 'RefreshRunHistory')
      $entry = Wait-Until { $items = (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Run history did not show the real completed run.' 30
      $outcome = Find-Id $history 'RunHistoryOutcome'; $expand = $null; $outcome.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $outcome '成功')
      Wait-Until { (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)).Count -gt 0 } 'Successful-outcome filter hid the completed run.'
      try { $expand.Collapse() } catch { }
      Capture-Element $history 'run-history-success.png' 'run-history' 'completed successful synchronization'
      Close-Window $history
      Close-Window $settings
    }
    'schedule' {
      $scheduledTask = 'FengSync-Test-' + [Guid]::NewGuid().ToString('N')
      # Schedule UI is launched from the SchedulesButton in the main sidebar.
      Click (Find-Id $main 'SchedulesButton')
      $schedule = Find-WindowById 'ScheduleWizard'
      Set-Text (Find-Id $schedule 'ScheduleTaskName') $scheduledTask
      Capture-Element $schedule 'schedule-configured.png' 'schedule' 'schedule wizard before creation'
      Click (Find-Id $schedule 'CreateScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -eq 0 } 'Schedule UI did not create the unique Windows task.' 30
      Click (Find-Id $schedule 'TestScheduleButton')
      Wait-Until { try { (Find-Id $schedule 'ResultText').Current.Name -match '测试运行|请求' } catch { $true } } 'Schedule UI did not request a test run.' 30
      Capture-Element $schedule 'schedule-test-running.png' 'schedule' 'scheduled test requested'
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
    'r2' {
      $r2UiRemote = 'ui_r2_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      $r2Uri = New-R2EndpointThroughUi $main $r2UiRemote "$bucket/$r2Root" $accountId $accessKeyId $secretAccessKey
      $upload = Join-Path $root 'r2-upload'; $download = Join-Path $root 'r2-download'; New-Item -ItemType Directory -Force -Path @($upload, $download) | Out-Null
      [IO.File]::WriteAllText((Join-Path $upload 'r2-proof.txt'), 'r2-roundtrip')
      Select-Mode $main '更新到右侧'; Compare-Ui $main $upload $r2Uri; Wait-Sync $main (Join-Path $upload 'r2-proof.txt')
      Wait-Until {
        $listing = & $rclone lsf $remoteCleanup.Target --config $remoteCleanup.Config --contimeout 10s --timeout 30s 2>$null
        $LASTEXITCODE -eq 0 -and $listing -contains 'r2-proof.txt'
      } 'UI upload did not become visible in the generated Cloudflare R2 test prefix.' 120
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      Select-Mode $main '更新到右侧'; Compare-Ui $main $r2Uri $download
      Wait-Sync $main (Join-Path $download 'r2-proof.txt') 180 300
      Assert-File (Join-Path $download 'r2-proof.txt') 'r2-roundtrip'
    }
    'r2-volume' {
      $r2UiRemote = 'ui_r2_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      $r2Uri = New-R2EndpointThroughUi $main $r2UiRemote "$bucket/$r2Root" $accountId $accessKeyId $secretAccessKey
      $results = [System.Collections.Generic.List[object]]::new()
      foreach ($mode in @(
        [pscustomobject]@{ Name = '双向同步'; Slug = 'two-way' },
        [pscustomobject]@{ Name = '镜像到右侧'; Slug = 'mirror' },
        [pscustomobject]@{ Name = '更新到右侧'; Slug = 'update' }
      )) {
        $case = "$($mode.Slug)-flat-10-files"
        $upload = Join-Path $root (Join-Path 'r2-volume' $case)
        $remoteCase = $r2Uri.TrimEnd('/') + '/' + $case
        New-Item -ItemType Directory -Force -Path $upload | Out-Null
        $fixtureAnchor = Join-Path $upload '.fengsync-fixture-anchor'; [IO.File]::WriteAllText($fixtureAnchor, 'fixture')
        for ($i = 1; $i -le 10; $i++) { [IO.File]::WriteAllText((Join-Path $upload ('batch-{0:D3}.txt' -f $i)), "Cloudflare R2 fixture $i") }
        & $rclone copyto $fixtureAnchor ($remoteCleanup.Target + "/$case/.fengsync-fixture-anchor") --config $remoteCleanup.Config --contimeout 10s --timeout 30s
        if ($LASTEXITCODE -ne 0) { throw "Could not materialize Cloudflare R2 test prefix: $remoteCase" }
        Stop-App $process; $process = $null
        $launch = Start-App; $process = $launch[0]; $main = $launch[1]
        Select-Mode $main $mode.Name; Compare-Ui $main $upload $remoteCase 180
        Wait-Sync $main (Join-Path $upload 'batch-001.txt') 180 600
        Wait-Until {
          $listing = & $rclone lsf ($remoteCleanup.Target + "/$case") --recursive --config $remoteCleanup.Config --contimeout 10s --timeout 30s 2>$null
          $LASTEXITCODE -eq 0 -and @($listing | Where-Object { $_ -match '\.txt$' }).Count -eq 10
        } "Cloudflare R2 did not contain all 10 fixture files: $remoteCase" 180 | Out-Null
        $results.Add($mode.Slug); Write-Output "Cloudflare R2 10-file matrix completed: mode=$($mode.Slug)"
      }
      if ($results.Count -ne 3) { throw 'Cloudflare R2 10-file matrix did not complete every synchronization mode.' }
    }
  }
  Capture-Window $process '99-complete.png' 'main-window' 'completed scenario state'
  $comparisonScenarios = @('ui-shell', 'ui-shell-native', 'ui-shell-software', 'local', 'local-move', 'modes', 'selection', 'sftp-to-local', 'sftp-ui', 'profile-filter', 'delete-threshold', 'history', 'gdrive', 'gdrive-volume', 'r2', 'r2-volume')
  if ($Scenario -in $comparisonScenarios -and -not ($screenshotManifest.Category -contains 'comparison')) {
    throw "Scenario '$Scenario' completed a comparison without recording a comparison screenshot."
  }
  $syncScenarios = @('local', 'local-move', 'modes', 'sftp-to-local', 'sftp-ui', 'profile-filter', 'delete-threshold', 'history', 'gdrive', 'gdrive-volume', 'r2', 'r2-volume')
  if ($Scenario -in $syncScenarios) {
    foreach ($requiredCategory in @('sync-started', 'sync-complete')) {
      if (-not ($screenshotManifest.Category -contains $requiredCategory)) { throw "Scenario '$Scenario' did not record the required $requiredCategory screenshot." }
    }
  }
  $passed = $true
  $scenarioTimer.Stop()
  Write-HarnessTrace ("Passed {0}; completed in {1}" -f $Scenario, $scenarioTimer.Elapsed)
}
catch {
  Write-HarnessTrace ("Failed {0}: {1}" -f $Scenario, $_.Exception.Message)
  throw
}
finally {
  $manifestPath = Join-Path $artifacts 'screenshots.json'
  [IO.File]::WriteAllText($manifestPath, ($screenshotManifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
  Stop-App $process
  Write-HarnessTrace "Cleaning up scenario: $Scenario"
  if ($remoteCleanup) {
    try {
      $config = $remoteCleanup.Config; $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
      & $rclone purge $remoteCleanup.Target --config $config --contimeout 10s --timeout 30s
      if ($LASTEXITCODE -ne 0) { Write-Error "$($remoteCleanup.Label) cleanup failed for generated child $($remoteCleanup.Target)" }
      $remaining = & $rclone lsf $remoteCleanup.Parent --recursive --config $config --contimeout 10s --timeout 30s 2>$null
      if ($LASTEXITCODE -ne 0) { Write-Error "$($remoteCleanup.Label) cleanup verification could not list $($remoteCleanup.Parent)" }
      $prefix = $remoteCleanup.Child.TrimEnd('/') + '/'
      if (@($remaining | Where-Object { $_ -eq $remoteCleanup.Child -or $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count -ne 0) {
        Write-Error "$($remoteCleanup.Label) cleanup verification found residual objects below $($remoteCleanup.Target)"
      }
    }
    finally {
      if ($Scenario -in @('r2', 'r2-volume')) {
        # The UI necessarily writes an obscured credential to its isolated rclone
        # config. It is not diagnostic evidence, so remove both credential-bearing
        # configs even when remote cleanup verification fails.
        Remove-Item -LiteralPath (Join-Path $appData 'rclone\rclone.conf') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $remoteCleanup.Config -Force -ErrorAction SilentlyContinue
        Remove-Item Env:AWS_ACCESS_KEY_ID, Env:AWS_SECRET_ACCESS_KEY, Env:AWS_SESSION_TOKEN -ErrorAction SilentlyContinue
      }
    }
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
