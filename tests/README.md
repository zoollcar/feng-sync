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
