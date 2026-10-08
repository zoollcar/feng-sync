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

The UI suite is **headless by default**: the application opens its windows outside the visible desktop and
the scenarios drive them through UI Automation, so a run never covers the screen, moves the cursor, or takes
the foreground window. Pass `-ShowOnScreen` to `Test-All.ps1` (or `FENGSYNC_UI_SHOW_ON_SCREEN=1`) to watch a
run with real mouse input instead.

`Test-All.ps1 -Level UiOffline` also enables the real WinFsp mount/read/unmount
test when the `WinFsp.Launcher` service is installed. The test uses a unique
temporary source and mount path, terminates its rclone process, verifies the
mount is detached, and then removes the temporary paths.
