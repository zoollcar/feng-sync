# GUI acceptance tests

`Run-GuiAcceptance.ps1` uses Windows UI Automation rather than Codex desktop controls. It launches Feng Sync with an isolated `FENGSYNC_DATA_DIR`, exercises Profile creation/edit cancellation, fills local endpoints, compares, synchronizes, asserts the copied file, and saves screenshots below `.fengsync-test/gui-acceptance/artifacts`.

Run:

```powershell
pwsh -File tests/FengSync.UiTests/Scripts/Legacy/Run-GuiAcceptance.ps1
```

The optional `-IncludeSftp` stage requires the companion SFTP acceptance setup to start the internal server and prepare a disposable rclone endpoint. It intentionally fails if that prerequisite is absent.

`Run-SftpGuiAcceptance.ps1` is that setup: it starts the same pinned `ssh2` protocol host used by Feng Sync with a disposable PBKDF2 account and share, then runs the visible local-to-SFTP UI workflow. Run it with `pwsh -File tests/FengSync.UiTests/Scripts/Legacy/Run-SftpGuiAcceptance.ps1`.
