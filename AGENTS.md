# Feng Sync — Agent Guide

## Purpose and scope

Feng Sync is a Windows desktop file-sync application. It synchronizes local folders and `sftp://`, `gdrive://`, and `s3://` endpoints through bundled `rclone`. The WPF app, CLI, and scheduled runs share the same Core planning, safety, execution, history, and baseline code.

Use this file as a fast routing map. Inspect only the relevant subtree and its focused tests before editing; do not scan the whole repository unless the task genuinely crosses components.

## Environment and commands

- Target framework: .NET 10; Windows is required for WPF, Windows Task Scheduler, WMI mount discovery, and UI tests.
- Restore/build: `dotnet build .\FengSync.sln`
- Run desktop app: `dotnet run --project .\src\FengSync`
- Run core/CLI/integration tests: `dotnet test .\tests\FengSync.Tests\FengSync.Tests.csproj`
- Run WPF acceptance tests: `dotnet test .\tests\FengSync.UiTests\FengSync.UiTests.csproj`
- Full Windows suite: `pwsh -File .\scripts\Test-All.ps1 -SkipGoogleDrive`
- Release-package gate: `pwsh -File .\scripts\Test-ReleasePackage.ps1 ...` (all parameters are mandatory; see the script).

`Test-All.ps1` builds first, then runs Core/CLI/SFTP tests and UI tests. Do not run multiple UI suites concurrently: they manage desktop processes. Google Drive tests are external and credential-dependent; use `-SkipGoogleDrive` for normal local verification, and enable its volume matrix only with `-IncludeGoogleDriveVolume`.

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
| `src/FengSync.Cli/` | JSON-line `compare` and `run` CLI over `ProfileRunner`. |
| `tests/FengSync.Tests/` | xUnit unit, integration, CLI, update, real-SFTP, and core tests. |
| `tests/FengSync.UiTests/` | Windows UI Automation acceptance tests and their PowerShell scenarios. |
| `scripts/` | Full-test runner and release manifest/package checks. |

## Architecture and critical invariants

- Route normal comparisons/runs through `ProfileRunner`; it is the shared workflow for UI, CLI, and automation. Do not duplicate planning or bypass validation in a new entry point.
- A comparison captures a paired snapshot once. Planning, safety validation, freshness validation, and baseline commits must preserve that contract; avoid adding a broad re-scan to the execution path.
- `ThreeWayPlanner` uses the persisted paired baseline. A missing per-path baseline is first-sync behavior and must not propagate deletions. Unresolved conflicts must remain non-executable until the user resolves them.
- `SyncExecutorV2` is the normal executor. Preserve its bounded-channel/worker-pool concurrency, per-path freshness checks, temp-file atomic publish, copy verification option, journals, and cancellation behavior. `SyncExecutor` is legacy fallback code; do not switch production flow to it casually.
- Do not commit a new baseline after a failed run. Partial commits and recovery state are deliberate safety behavior.
- Endpoint operations must preserve `IEndpoint` capability and path-semantics differences. Treat remote endpoints as potentially case-sensitive and not necessarily able to enumerate empty directories.
- Destructive actions (deletes, overwrites, mirror/update plans, updater replacement) need explicit safety validation and focused regression tests. Keep user confirmation and delete thresholds intact.
- Credentials belong to rclone/app data, never profiles, CLI arguments, fixtures, logs, or source control. `FENGSYNC_DATA_DIR` is the supported isolated-data override for tests.
- The updater has strict path and manifest validation. Retain path-containment checks, hashes, rollback behavior, and its prohibition on using a source checkout as an installation directory.

## Change workflow

1. Start from the table above and read the relevant production file plus the closest existing test(s); use `rg` by symbol or behavior rather than broad file dumps.
2. Make the smallest cohesive change. Keep nullable reference types enabled and follow nearby C# style (file-scoped namespaces, concise records/classes, async APIs with `CancellationToken` where applicable).
3. Add or update tests in the corresponding suite. For a bug fix, prefer a regression test that demonstrates the original unsafe/incorrect behavior.
4. Run the narrowest affected test project first, then `dotnet build .\FengSync.sln`; run the full suite when changing shared synchronization, safety, configuration, packaging, or UI integration paths.
5. Before handoff, report tests actually run and preserve any unrelated working-tree edits.

## Testing and local artifacts

- UI/integration artifacts live in `.fengsync-test/` and are intentionally ignored. Failed runs retain evidence there; do not delete it as routine cleanup.
- `artifacts/` contains generated release packages and is ignored.
- Test fixtures are versioned in `tests/fixtures/`; never place real endpoints, tokens, passwords, private keys, or personal paths there.
- Build outputs (`bin/`, `obj/`) and `.vs/` are generated files; do not edit or commit them.

## Release notes

- The WPF project version is currently declared in `src/FengSync/FengSync.csproj`; keep `Version`, `AssemblyVersion`, and `FileVersion` coherent when cutting a release.
- Generate `release-manifest.json` with `scripts/New-ReleaseManifest.ps1`; package naming, manifest contents, version, and checksum are verified by `Test-ReleasePackage.ps1`.
- `rclone.exe` is a committed, bundled runtime dependency. Replace it only deliberately and verify WPF, CLI, SFTP, and mount behavior.

## Maintaining this guide

Keep instructions concrete, stable, and command-backed. Update this file when the repository map, verification commands, critical invariant, or release process changes. Put highly specialized rules in a nested `AGENTS.md` beside the subsystem instead of expanding this root guide indefinitely.
