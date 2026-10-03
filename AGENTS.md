# Feng Sync — Agent Guide

## Purpose and scope

Feng Sync is a Windows desktop file-sync application. It synchronizes local folders and cloud endpoints — `sftp://`, `gdrive://`, `onedrive://`, `dropbox://`, `box://`, `webdav://`, and `s3://` — through bundled `rclone`. `EndpointSchemes` is the single map between those URI schemes, the rclone backend types, and `EndpointType`; add a provider there, never as another `StartsWith` in a caller. The WPF app, CLI, and scheduled runs share the same Core planning, safety, execution, history, and baseline code.

Use this file as a fast routing map. Inspect only the relevant subtree and its focused tests before editing; do not scan the whole repository unless the task genuinely crosses components.

## Environment and commands

- Target framework: .NET 10; Windows is required for WPF, Windows Task Scheduler, WMI mount discovery, and UI tests.
- Restore/build: `dotnet build .\FengSync.sln`
- Run desktop app: `dotnet run --project .\src\FengSync`
- Run core/CLI/integration tests: `dotnet test .\tests\FengSync.Tests\FengSync.Tests.csproj`
- Run WPF acceptance tests: `dotnet test .\tests\FengSync.UiTests\FengSync.UiTests.csproj`
- Core-only Windows gate: `pwsh -File .\scripts\Test-All.ps1 -Level Core`
- Full offline Windows suite: `pwsh -File .\scripts\Test-All.ps1 -Level UiOffline`
- Full offline suite, watching the scenarios run on screen: add `-ShowOnScreen` (off by default; see Testing and local artifacts)
- Online suite, including Google Drive: `pwsh -File .\scripts\Test-All.ps1 -Level Online`
- Release-package gate: `pwsh -File .\scripts\Test-ReleasePackage.ps1 ...` (all parameters are mandatory; see the script).

`Test-All.ps1` builds first, then runs Core/CLI/SFTP tests and UI tests. Do not run multiple UI suites concurrently: they manage desktop processes. Google Drive tests are external and credential-dependent; use `-Level UiOffline` for normal local verification and opt into them with `-Level Online`. A default `-Level UiOffline` run leaves the desktop and the pointer alone, so it is safe to start while you keep working; pass `-ShowOnScreen` when you want to watch it.

## Repository map

| Path | Responsibility |
| --- | --- |
| `src/FengSync.Core/` | Domain model and shared sync engine. This is the default starting point for behaviour changes. |
| `src/FengSync.Core/Configuration/`, `Profiles/` | Profile/global settings, validation, migration, persistence. |
| `src/FengSync.Core/Scanning/`, `Baseline/` | Endpoint snapshots and paired baselines for two-way deletion/change detection. |
| `src/FengSync.Core/Execution/` | Freshness checks, bounded concurrent transfers, resume, journaling, atomic publish, verification. |
| `src/FengSync.Core/Safety/` | Endpoint nesting/equality, destructive-plan, and capacity safety checks. |
| `src/FengSync.Core/Mount/`, `SftpServer/` | rclone mount discovery/lifecycle and bundled-rclone SFTP server. |
| `src/FengSync.Core/Updates/` + `src/FengSync.Updater/` | Update metadata/download/install workflow and isolated self-updater. |
| `src/FengSync/` | WPF shell: XAML views, view models, UI services, themes, bundled `Assets/rclone/rclone.exe`. |
| `src/FengSync.DriveHub/` | Second executable entry point (`DriveHub.exe`): hosts the shared cloud drive manager window on its own rclone daemon; a post-build target copies `DriveHub.*` beside `FengSync.exe`. |
| `src/FengSync.Cli/` | JSON-line `compare` and `run` CLI over `ProfileRunner`. |
| `tests/FengSync.Tests/` | xUnit unit, integration, CLI, update, real-SFTP, and core tests. |
| `tests/FengSync.UiTests/` | Windows UI Automation acceptance tests and their PowerShell scenarios. |
| `scripts/` | Full-test runner and release manifest/package checks. |

## Architecture and critical invariants

- Route normal comparisons/runs through `ProfileRunner`; it is the shared workflow for UI, CLI, and automation. Do not duplicate planning or bypass validation in a new entry point.
- A comparison captures a paired snapshot once. Planning, safety validation, freshness validation, and baseline commits must preserve that contract; avoid adding a broad re-scan to the execution path.
- `ThreeWayPlanner` uses the persisted paired baseline. A missing per-path baseline is first-sync behavior and must not propagate deletions. Unresolved conflicts must remain non-executable until the user resolves them.
- `SyncExecutorV2` is the sole executor; no legacy fallback exists. Preserve its bounded-channel/worker-pool concurrency, per-path freshness checks, temp-file atomic publish, copy verification option, journals, and cancellation behavior.
- Resume is Feng Sync's own behavior, never rclone's: rclone's copy path restarts an interrupted transfer from zero, `--inplace` and multi-thread `--inplace` both re-copy, and the RC API has no offset read or offset write. The engine therefore stages transfers at or above `EffectiveProfileSettings.ResumeMinimumBytes` (default 500 MB) under the deterministic `SyncInternalPaths.TransferTemporary(path)` name — `<name>.fengsync.partial` — and appends the missing tail itself. Preserve the three guards that must all pass before a staging file is continued: the recorded `TransferResumeState` (source size + modification time, in `TransferResumeStateStore`, deliberately in app data rather than beside the user's file), the 64 KiB seam comparison across the append point, and the size/threshold gate. A staging file that fails any guard is deleted, never appended to. The only offset read rclone exposes is `--rc-serve`, which is why `RcloneDaemon` starts it and why it must stay loopback-only behind the daemon's per-run credentials; `RcloneRcClient.OpenRangeAsync`/`ProbeAsync` are its only consumers. Continuation is only possible on the remote→local leg — an upload cannot be resumed and a remote→remote copy never touches local disk — so those paths discard their staging object and restart by design. Two further rules protect the append: a staging file whose seam does not match is *cut back* to the longest verified prefix (`VerifiedPrefixAsync`, bounded to a few write buffers) rather than deleted, because an interruption can tear the last write and losing a whole transfer over those bytes would defeat the feature; and `AppendRemoteAsync` takes that verified offset as a parameter and truncates the file to it before appending, so bytes that were never verified can never survive into the published file. Keep the resumable leg sequential: `--multi-thread-streams` writes chunks concurrently and can leave holes that a later run would wrongly trust as a prefix.
- Do not commit a new baseline after a failed run. Partial commits and recovery state are deliberate safety behavior.
- Endpoint operations must preserve `IEndpoint` capability and path-semantics differences. Treat remote endpoints as potentially case-sensitive and not necessarily able to enumerate empty directories.
- Destructive actions (deletes, overwrites, mirror/update plans, updater replacement) need explicit safety validation and focused regression tests. Keep user confirmation and delete thresholds intact.
- Credentials belong to rclone/app data, never profiles, CLI arguments, fixtures, logs, or source control. `FENGSYNC_DATA_DIR` is the supported isolated-data override for tests.
- An endpoint export carries only what is not a credential: name, type, provider, bucket/prefix, group, tags and the *names* of the rclone parameters. `CloudEndpointPortability`'s export records have no field for a value, and import treats the file as untrusted input — a remote name that could fabricate an `rclone.conf` section is refused. Preserve both properties when changing it.
- The updater has strict path and manifest validation. Retain path-containment checks, hashes, rollback behavior, and its prohibition on using a source checkout as an installation directory.
- Every shell (the main `App` and `DriveHubApp`) installs its services through `AppServices.Install` and implements `IAppServiceHost`. Shared windows and services must resolve dependencies from `AppServices` — never by casting `Application.Current` to a shell type — so any entry point can host them. `IAppServiceHost` deliberately excludes the SFTP server: two shells must not compete for one listen port.
- An S3 endpoint's prefix is enforced by an rclone `alias`, not by the UI: saving writes a hidden `<name>_s3` remote holding the credentials plus an `<name>` alias targeting `<backing>:<bucket>/<prefix>`. Consequences to preserve — the alias carries the user-facing name, so profiles and listings are unchanged; `CloudEndpointMetadata.RootPath` is therefore empty for an alias-backed endpoint (the alias root *is* the prefix, and repeating it would address the prefix twice) while `Prefix` keeps the value for display; `SelectEndpointRemotes` hides the backing store and surfaces no alias it did not create; deleting an endpoint must delete both remotes. S3 backends have no prefix option of their own (`rclone help backend s3`), so this pair is the only way to get a boundary rclone itself refuses to cross.
- Renaming an endpoint cannot go through rclone: there is no rename operation, and re-creating the remote with `config/create` is not a substitute — measured against the bundled v1.75.0, that call runs the backend's configuration state machine, so cloning an OAuth remote walks back into `oauth-confirm → oauth-islocal → oauth-authorize` and demands a fresh browser authorization instead of copying the stored token. `CloudEndpointRename` therefore rewrites the section in rclone.conf (`RcloneConfigFile`), which keeps every value byte-identical, and a running `rclone rcd` reads the change back on its very next call. Three rules hold that together: the rewrite is verified through the daemon before anything else changes (`config/get` must report the same parameters, and `config/listremotes` the new name and not the old one); rclone.conf, `cloud-endpoints.json` and `profiles.json` are backed up and restored together, because a config that still has the old section next to a profile that already points at the new name is worse than no rename; and an endpoint with a live mount is refused outright, since the mount holds the filesystem string `<name>:` that the daemon resolved when it mounted. An enforced-prefix S3 endpoint is two sections and both move, with the alias repointed at the renamed backing store.
- Mount lifecycle state (`RcloneMountService`'s managed set) is per process, not persisted, so each executable owns its own daemon and only stops the mounts it created. Each shipped entry point must therefore be published self-contained; WPF native dependencies do not flow through a `ProjectReference`.
- The VFS cache that backs a mount is daemon-wide, and rclone resolves its cache root **once when the process starts**. The only working way to keep it inside app data is the `--cache-dir` flag on `rclone rcd` (see `RcloneDaemon.BuildArguments` and `AppDataPaths.MountCache`); the per-call `_config: { CacheDir }` override on `mount/mount` is silently accepted and ignored, so never reintroduce it as the mechanism. Two consequences follow: because the cache is shared, unmounting one mount must not delete it (a later mount of the same remote would re-download everything), and `RcloneDaemonArgumentsTests` asserts the flag is present precisely because a missing or misspelled flag fails silently.

## Change workflow

1. Start from the table above and read the relevant production file plus the closest existing test(s); use `rg` by symbol or behavior rather than broad file dumps.
2. Make the smallest cohesive change. Keep nullable reference types enabled and follow nearby C# style (file-scoped namespaces, concise records/classes, async APIs with `CancellationToken` where applicable).
3. Add or update tests in the corresponding suite. For a bug fix, prefer a regression test that demonstrates the original unsafe/incorrect behavior.
4. Run the narrowest affected test project first, then `dotnet build .\FengSync.sln`; run the full suite when changing shared synchronization, safety, configuration, packaging, or UI integration paths.
5. Before handoff, report tests actually run and preserve any unrelated working-tree edits.

## Testing and local artifacts

- UI/integration artifacts live in the ignored `.fengsync-test/` directory. Clear it once at the start of each test-suite run, then retain the current run's evidence until the next run; do not delete it during test teardown.
- `artifacts/` contains generated release packages and is ignored.
- Test fixtures are versioned in `tests/fixtures/`; never place real endpoints, tokens, passwords, private keys, or personal paths there.
- `ResumableDownloadTests` and `ConcurrentStagingReproTests` drive the real bundled `rclone.exe` — a local backend behind an `alias` remote, so bytes genuinely travel through the daemon's RC endpoint. They need no network and no credentials. Do not swap them for a fake client: the behavior under test is exactly what rclone does and does not support.
- Window behavior belongs in in-process tests, not in a desktop scenario: `CloudTransferWindowBehaviorTests`, `CloudTransferBehaviorTests`, `CloudFeatureBehaviorTests`, `ResumeSettingsUiTests`, `S3PrefixBoundaryTests`, `DriveHubEntryPointTests` and the window cases in `UiFeatureBehaviorTests` build the real window on an STA thread (`RunSta`) with the shared theme dictionaries merged (`EnsureApplicationResources`) and drive it through `RaiseEvent`/the queue. They need no foreground session, run in milliseconds, and are the right place for "the buttons do what they say". `Invoke-UiScenario.ps1` scenarios are for what only the real shell can prove: reachability, UIA wiring, and AutomationIds.
- The `Invoke-UiScenario.ps1` scenarios are **desktop-free by default**: each window is parked at `-32000` (`Park-WindowOffScreen`) so nothing is drawn over the user's work, `Activate-TestWindow` is a no-op, and input is delivered through UI Automation patterns, posted `WM_KEYDOWN`/`WM_CHAR`, or `PostMessage`. `-ShowOnScreen` (or `FENGSYNC_UI_SHOW_ON_SCREEN=1`, or `Test-All.ps1 -ShowOnScreen`) restores the historical restore/raise/real-mouse behaviour for watching a run by hand. Two hard limits follow from WPF, and both are why a step may need a keyboard or pattern equivalent: WPF hit-tests mouse messages against the position of the **real pointer**, so a posted click or drag is discarded and cannot be made to work without moving the cursor; and keyboard messages only reach WPF's input system when the window holds keyboard focus, which is why `Send-KeysToWindow` posts keys to the focused element rather than using `SendKeys`. Screenshots are skipped in a desktop-free run (a screen copy would capture the desktop at `-32000`) and recorded as skipped in the manifest. `DesktopFreeUiHarnessTests` fails if a harness function touches the pointer without consulting `$script:showOnScreen`.
- A `TextBlock` that displays live text (a status, a summary, a hint) must carry an `AutomationId` and **no** static `AutomationProperties.Name`. A static name replaces the text as the accessible name, so a screen reader announces the label instead of the content — and the desktop scenario can no longer read the value either. `LiveTextAccessibilityTests` scans the page XAML and fails on a regression; a self-binding `{Binding Text, RelativeSource={RelativeSource Self}}` (as `AboutWindow` uses) is the compliant way to name a block after its own text.
- Build outputs (`bin/`, `obj/`) and `.vs/` are generated files; do not edit or commit them.

## Release notes

- Every shipped executable declares its own version, so a release must bump all four together: `src/FengSync/FengSync.csproj` (main shell), `src/FengSync.DriveHub/FengSync.DriveHub.csproj` (cloud drive manager), `src/FengSync.Cli/FengSync.Cli.csproj` and `src/FengSync.Updater/FengSync.Updater.csproj`. Keep `Version`, `AssemblyVersion`, and `FileVersion` coherent, and keep the three-part number identical across the four — `ExecutableVersionTests` scans the csproj files and fails on a missing, mismatched, or unparsable version. `src/FengSync.Core` is a library and deliberately carries no version of its own.
- The release folder ships multiple entry points: `FengSync.exe` (main shell), `DriveHub.exe` (cloud drive manager) and `FengSync.Updater.exe`. DriveHub is published self-contained and only its four `DriveHub.*` files are copied into the publish folder, because the FengSync publish already provides everything else. `Test-ReleasePackage.ps1` requires `DriveHub.exe` and checks its `ProductVersion`; `DriveHubEntryPointTests` fails if the build stops emitting it.
- Generate `release-manifest.json` with `scripts/New-ReleaseManifest.ps1`; package naming, manifest contents, version, and checksum are verified by `Test-ReleasePackage.ps1`.
- `rclone.exe` is a committed, bundled runtime dependency. Replace it only deliberately and verify WPF, CLI, SFTP, mount behavior, and the `--rc-serve` range read that resume depends on (a `GET /[<fs>:]/<path>` with a `Range` header must answer `206` with a correct `Content-Range`).

## Maintaining this guide

Keep instructions concrete, stable, and command-backed. Update this file when the repository map, verification commands, critical invariant, or release process changes. Put highly specialized rules in a nested `AGENTS.md` beside the subsystem instead of expanding this root guide indefinitely.
