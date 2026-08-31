# Tests

- `FengSync.Tests/` — unit, integration, CLI, endpoint, and protocol tests.
- `FengSync.UiTests/` — Windows UI Automation tests and their scripts.
  - `Scripts/Legacy/` contains standalone compatibility scenarios retained for direct use.
  - Online tests cover Google Drive and Cloudflare R2. R2 uses the dedicated
    `feng-sync-e2e-test` bucket by default, creates a unique prefix for every
    scenario, purges it in `finally`, and fails if a post-cleanup listing finds
    residual objects.
- `Shared/` — shared test-harness helpers, including pre-run process cleanup.
- `fixtures/` — versioned profile and input fixtures.

Run the test projects separately so the UI suite can manage its desktop process lifecycle:

```powershell
dotnet test tests/FengSync.Tests/FengSync.Tests.csproj --no-restore
dotnet test tests/FengSync.UiTests/FengSync.UiTests.csproj --no-restore
```

`Test-All.ps1 -Level UiOffline` also enables the real WinFsp mount/read/unmount
test when the `WinFsp.Launcher` service is installed. The test uses a unique
temporary source and mount path, terminates its rclone process, verifies the
mount is detached, and then removes the temporary paths.

Run the online suite with runtime-only credentials (never commit these values):

```powershell
$env:FENGSYNC_TEST_R2_ACCOUNT_ID = '<Cloudflare account ID>'
$env:FENGSYNC_TEST_R2_ACCESS_KEY_ID = '<R2 access key ID>'
$env:FENGSYNC_TEST_R2_SECRET_ACCESS_KEY = '<R2 secret access key>'
# Set this only for temporary R2 credentials:
$env:FENGSYNC_TEST_R2_SESSION_TOKEN = '<R2 session token>'
pwsh -File .\scripts\Test-All.ps1 -Level Online
```

`FENGSYNC_TEST_R2_BUCKET` may override the default bucket. The credential must
have Object Read & Write permission scoped to that bucket. The `Online` gate
fails during preflight when required R2 variables are missing, so it cannot
report a false-green result that silently omits S3-compatible coverage.
