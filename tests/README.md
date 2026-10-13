# Tests

- `FengSync.Tests/` — unit, integration, CLI, endpoint, and protocol tests.
- `FengSync.UiTests/` — Windows UI Automation tests and their scripts.
  - `Scripts/Legacy/` contains standalone compatibility scenarios retained for direct use.
- `Shared/` — shared test-harness helpers, including pre-run process cleanup.
- `fixtures/` — versioned profile and input fixtures.

Run the test projects separately so the UI suite can manage its desktop process lifecycle:

```powershell
dotnet test tests/FengSync.Tests/FengSync.Tests.csproj --no-restore
dotnet test tests/FengSync.UiTests/FengSync.UiTests.csproj --no-restore
```

The UI suite runs each scenario on a **private desktop**: the test host creates a desktop object of its own
and starts the scenario host on it, and every process the scenario starts in turn inherits it. Windows on a
desktop that is not being displayed are never composited to a monitor, and that desktop has its own input
queue and its own foreground window — so the application can open real windows, raise them and be clicked
with a real mouse without any of it reaching your session. Measured on this repository: 390 samples across a
full scenario run found zero application windows on the visible desktop and the foreground window unchanged.
Pass `-ShowOnScreen` to `Test-All.ps1` (or `FENGSYNC_UI_SHOW_ON_SCREEN=1`) to run on this desktop instead and
watch a scenario by hand.

Two consequences are worth knowing before changing the harness:

- **Synthesized pointer input cannot work on a private desktop.** Measured with a control run of the same
  probe on both desktops: on the private one `GetCursorPos` and `mouse_event` fail with `ERROR_ACCESS_DENIED`
  and `SetCursorPos` returns false, while on the visible one all three succeed. So the few steps that need a
  real click or hover stay behind `-ShowOnScreen`, and the default path reaches the application through UI
  Automation patterns, posted keys and `PostMessage` — which are delivered to a window's own message queue and
  do not care which desktop it is on.
- **Do not resolve the main window through `Process.MainWindowHandle`.** It is a heuristic — the first visible
  top-level window the process owns — and on a private desktop the application also owns the input-indicator
  overlays, which appear a few seconds in and sit above everything. Use `Get-MainWindowHandle`, which
  identifies the shell structurally. `PrivateDesktopHarnessTests` fails if `.MainWindowHandle` comes back.

Screenshots on a private desktop are rendered by the window itself (`PrintWindow` with
`PW_RENDERFULLCONTENT`) rather than copied from the screen, which does not exist there; the scenarios ask the
application for software rendering, which is a supported production path. `ui-shell-native` deliberately keeps
the native path, so its capture is the window frame only and the manifest says so.

`Test-All.ps1 -Level UiOffline` also enables the real WinFsp mount/read/unmount
test when the `WinFsp.Launcher` service is installed. The test uses a unique
temporary source and mount path, terminates its rclone process, verifies the
mount is detached, and then removes the temporary paths.
