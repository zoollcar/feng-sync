# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目一句话

Feng Sync 是一个面向 Windows 的桌面文件同步工具（.NET 10 + WPF），本地、SFTP、Google Drive、S3 通过随应用分发的 rclone 访问。界面与 CLI 共用同一套比较、执行、安全校验、Journal、基线逻辑。

## 构建与运行

仓库根目录为 `c:\Users\feng\project\feng-sync`，解决方案文件 [FengSync.sln](FengSync.sln)。三个产品项目：

- [src/FengSync.Core/FengSync.Core.csproj](src/FengSync.Core/FengSync.Core.csproj) — 平台无关的同步核心（`net10.0`）。
- [src/FengSync/FengSync.csproj](src/FengSync/FengSync.csproj) — WPF 桌面应用（`net10.0-windows`）；项目内 `Assets/rclone/rclone.exe` 被自动复制到输出，并内置一个 MSBuild target 把 `FengSync.Cli.exe` 同步到 `OutDir`/`PublishDir`。
- [src/FengSync.Cli/FengSync.Cli.csproj](src/FengSync.Cli/FengSync.Cli.csproj) — 无交互 CLI（`net10.0`），入口 [src/FengSync.Cli/Program.cs](src/FengSync.Cli/Program.cs)。

常用命令：

```powershell
dotnet build .\FengSync.sln                                              # 全部项目
dotnet run --project .\src\FengSync                                       # 启动桌面 UI
dotnet build .\FengSync.sln; .\src\FengSync.Cli\bin\Debug\net10.0\FengSync.Cli.exe compare --profile <id|file>
.\src\FengSync.Cli\bin\Debug\net10.0\FengSync.Cli.exe run --profile <id> --non-interactive --json-log
```

应用数据默认在 `%LOCALAPPDATA%\FengSync`，可通过 `FENGSYNC_DATA_DIR` 重定向以隔离测试。rclone 必须在 `Assets/rclone/rclone.exe`；CLI/桌面程序都不依赖 `PATH` 上的 rclone。

## 测试

一键运行（核心 + CLI + 真实 SFTP + WPF UI；检测到 rclone 凭据后才会跑 Google Drive）：

```powershell
pwsh -File .\scripts\Test-All.ps1
pwsh -File .\scripts\Test-All.ps1 -SkipGoogleDrive            # 跳过外部云
pwsh -File .\scripts\Test-All.ps1 -IncludeGoogleDriveVolume    # 加跑 Drive 100 文件 / 100 文件夹压力矩阵
```

按层单独运行（构建后用 `--no-build`）：

```powershell
dotnet test .\tests\FengSync.Tests                          # 核心、CLI、真实 SFTP 协议、性能不变量
dotnet test .\tests\FengSync.UiTests                        # 真实 WPF UI 验收（创建 .fengsync-test 并保存截图）
dotnet test .\tests\FengSync.Tests -c Debug --filter FullyQualifiedName~PlannerTests.SomeMethod   # 单测
```

`FengSync.UiTests` 启动 WPF 进程跑端到端点击流；失败时 `.fengsync-test/` 证据目录会保留，PowerShell 启动脚本位于 [tests/gui/](tests/gui)。`FengSync.Tests/PerformanceInvariantTests.cs` 断言 M0/M2 等不变量（见下文）。

## 架构全景

### 数据流（一次同步的线性管道）

`src/FengSync.Core/ProfileRunner.cs` 是 UI / CLI / 计划任务共享的唯一入口：

1. `EndpointFactory.OpenAsync` 创建一对 `IEndpoint`；只要任一端是 `sftp://`、`gdrive://`、`s3://` 之一，就启动一个私有的 rclone RC 守护进程并由该对端点独占。
2. `PreparedProfileRun.PrepareAsync` 对每端只调用一次 `ScanAsync`，产出 `ComparisonSnapshot`（左/右 `EndpointSnapshot` + 路径字典索引 + baseline）。这是**唯一**允许全树枚举的位置。
3. `ModePlanner` +（双向时）`ThreeWayPlanner` 用快照产出 `SyncPlan`；`SafetyValidator` 串联 `PathTopologyValidator`、`DeletionGuard`、`StorageCapacityChecker`。
4. `SyncExecutorV2` 用 `PlanFreshnessValidator.ValidateStatAsync`（**只 stat 选中源**）、`StatVerifier`（**只 stat 目标路径**）、有界 `Channel<SyncOperation>` 工作池与 `ResourceGovernor` 双资源租约完成复制、删除、回收站/归档。
5. `BaselineStateBuilder.BuildNextState` 直接由 `ComparisonSnapshot` + 已提交操作结果（验证后指纹）构造下一份配对 baseline；任何失败/取消/过滤路径保留旧 baseline。
6. `JournalWalStore` 用追加式 `.events.jsonl` 记录运行进度；`RecoveryCoordinator.FindRecoveryRequiredAsync` 汇总未完成 journal 与 `NeedsRecovery` 事务。
7. `RunHistoryRepository` 写一行结果；UI 在 `Views/RunHistoryWindow.xaml` 展示。

`SyncExecutor` 是旧版执行器，`MainWindow.xaml.cs` 仍直接调用它；新流水线应在 `ProfileRunner` 里跑 V2。详见 [docs/performance/](docs/performance) 中 `01-observability-and-benchmarks.md` 起的系列文档。

### 端点抽象

[src/FengSync.Core/Endpoints.cs](src/FengSync.Core/Endpoints.cs)：

- `IEndpoint` — `ScanAsync`、`StatAsync`、`CopyToAsync`、`MoveAsync`、`DeleteAsync`、`CreateDirectoryAsync`。`StatAsync` 默认抛 `NotSupportedException`，强制实现者显式声明支持；调用方在热路径回退 `ScanAsync` 是被禁止的。
- `LocalEndpoint` ([src/FengSync.Core/LocalEndpoint.cs](src/FengSync.Core/LocalEndpoint.cs)) — `Directory.EnumerateFileSystemEntries` + 元数据；额外实现 `IContentHashEndpoint.HashAsync`。系统/Reparse 点直接跳过；`SyncInternalPaths.IsExcludedFromScan` 屏蔽同步内部状态文件。
- `RcloneEndpoint` — 通过 `RcloneRcClient`（强类型包装 `operations/list|listdir|copyfile|movefile|deletefile|mkdir|purge`）走 rclone RC；endpoint URI 形如 `sftp://<remote>/<root>`。`StatAsync` 走父目录 `operations/list`，列表结果取 `Name`/`Path`。
- `IEndpointStateStorage` — `sync.fengdb` 等内部状态文件单独通道，不混入 `ScanAsync`。

凭据只存于 rclone.conf，从不写入 Profile / CLI 参数 / 日志。

### 模型与计划

[src/FengSync.Core/Model.cs](src/FengSync.Core/Model.cs) 定义核心 records：

- `EntryKind`、`Delta`、`SyncMode`（TwoWay / Mirror / Update / Custom — Custom 已被 `FeatureCapabilityService` 禁用）、`VersioningMode`（None / RecycleBin / TimestampedArchive）、`OperationKind`（含 Conflict / Blocked）。
- `Fingerprint` — `Size + ModifiedUtc + Hash?`，`Matches` 默认容差 2s，可由 Profile 覆盖。
- `EntrySnapshot`、`BaselineEntry`、`SyncFilter`（可序列化版本 + 运行时 `FilterRule`）、`VersioningPolicy`、`SyncProfile`、`SyncOperation`（含 `ResolveConflict` / `OverrideCopyDirection` / `DeleteBothSides` 三种用户覆盖）、`SyncPlan`（`CanExecute` 检查冲突与未选择项）。
- `ComparisonSnapshot`、`EndpointSnapshot` 定义见 [src/FengSync.Core/Scanning/SnapshotTypes.cs](src/FengSync.Core/Scanning/SnapshotTypes.cs)。

[src/FengSync.Core/ThreeWayPlanner.cs](src/FengSync.Core/ThreeWayPlanner.cs)：双侧变更+基线才能产生 `OperationKind.Conflict`，首次同步两侧不同也标记冲突；`PathRules.FindBlockers` 把 Windows 非法名（CON 等）提前为 `Blocked`。[src/FengSync.Core/ModePlanner.cs](src/FengSync.Core/ModePlanner.cs) 把 Mirror/Update 委托给基线感知的版本。

### 过滤

[src/FengSync.Core/Filtering.cs](src/FengSync.Core/Filtering.cs)：有序 `FilterRule`，后者覆盖前者；glob 在 `FilterEngine.Glob` 内编译为正则（裸文件名匹配零或多层目录，含 `/` 时为根相对）。`SyncFilter.ToRules` 把 `Include`/`Exclude` 列表转成统一规则序列（出现 Include 时默认先加 `Exclude **`，避免“指定 include 即视为全部排除”的反向语义）。过滤是同步边界，**不是**删除请求——被过滤的旧 baseline 条目在 `ModePlanner` 内仍保留，避免历史路径被当成“缺失”。

### 计划快照与新鲜度

`PlanSnapshot` 同时存在两条构造路径：

- `CaptureAsync` — 旧路径，**会**再次扫描两端，仅给旧 `SyncExecutor` 用。
- `FromComparison` — M1/M2 主路径：从已建好的 `ComparisonSnapshot` 取指纹，禁止再扫描。

`PlanFreshnessValidator` 提供 `ValidateAsync`（legacy 全树扫描）和 `ValidateStatAsync`（按选中复制项并发 `StatAsync`，默认容差本地 2s / 远程 5s）。`StatVerifier` 只对目标和源各一次 `StatAsync`；远程 ID 不稳定时降级而非失败。

### 执行器（V2）

[src/FengSync.Core/Execution/SyncExecutorV2.cs](src/FengSync.Core/Execution/SyncExecutorV2.cs)：

- 目录创建串行前置，避免子文件复制竞争父目录。
- 复制走有界 `Channel<SyncOperation>`（默认容量 256，small-file 阈值 8 MiB）+ `workerCount = min(maxConcurrentCopies, capacity, copyOps.Count)` 个 Task。**禁止**为 N 个操作建 N 个 Task。
- `ResourceGovernor.AcquireAsync` 按固定顺序取 source + target 双资源租约防死锁；默认预算 UnknownLocal 1 / LocalVolume 2 / SFTP 4 / Drive 4 / S3 8。
- 复制：源 → `temporaryPath = path + ".fengsync-" + GUID + ".partial"` → publish 前的 `MoveAsync` → `StatVerifier.VerifyAsync`（仅 target + source 各一次 stat）→ `RequirePostPublishFingerprintAsync` 双向 stat 写入 `OperationRunResult.SourceAfter/TargetAfter`，供 M5 baseline 提交使用。
- 删除：`VersioningMode` → `PermanentDeleteStrategy` / `RecycleBinStrategy` / `ArchiveStrategy(archiveDir)`；归档模式跑 `RetentionCleanupService.CleanupAsync(ArchiveDirectory, ToRetentionPolicy())`。
- 失败后清理 partial 文件；取消时把未启动的项标 `JournalState.Cancelled`。

[src/FengSync.Core/Execution/SyncExecutor.cs](src/FengSync.Core/Execution/SyncExecutor.cs) 为旧版（`MainWindow` 直接调用）；新增功能应在 V2 中实现。

### 安全校验

[src/FengSync.Core/Safety/SafetyValidation.cs](src/FengSync.Core/Safety/SafetyValidation.cs)：同/嵌套端点阻断、空源镜像阻断、`delete.count`/`delete.ratio` 阈值、本地目标磁盘容量。`SyncRiskSummary.Create` 给确认对话框提供 copies / overwrites / deletes / 字节数；`SyncConfirmationPolicy.CanOverrideWithProfileName` 仅在所有 issue 都是删除阈值类时允许“输入 Profile 名”越过阻断。

### 基线 / Journal

- `BaselineTransaction` (Started → Staging → Committed / RolledBack / NeedsRecovery)。`BaselineRepository.CommitFromResultsAsync` 由 `BaselineStateBuilder` 推算下一份配对状态；复制成功的两侧都写成验证后的目标指纹；删除成功的另一侧写为 `null`；**失败/取消/冲突/过滤路径不更新**。无操作的双向运行也会经 `CommitFromSnapshotAsync` 建立 baseline。`EndpointIdentity.From` 故意使用 rclone remote 名（而非 `Profile.Id` 的 GUID），保证重启后端点身份稳定。
- `JournalWalStore` (M4/M5)：`<app>/journals/<run>.header.json + .events.jsonl + .summary.json`。drain 任务在 `BeginRunAsync` 后才启动，避免空流提前退出；提供 `AwaitDurabilityAsync` 在 publish 前等关键事件落盘。`JournalRecoveryReader.LoadIncompleteAsync` 同时识别旧的单文件 `SyncJournal` 和新 WAL；尾行截断容忍、非尾行损坏 / seq 重复 / seq 跳跃会报告为恢复故障。
- `RunHistoryRepository` 独立于 journal，AppendAsync 走 `SemaphoreSlim` + 临时文件 `Move`；空文件 / JSON 损坏返回空集而不抛异常（避免 WPF Loaded 异常退出）。

### 自动化

- `AutomationExitCode` ([src/FengSync.Core/Automation/ExitCode.cs](src/FengSync.Core/Automation/ExitCode.cs)) 是 CLI/计划任务契约：Success / Warning / Failure / Conflict / ConfigurationError / Cancelled。
- `WindowsTaskSchedulerService` 只把 Profile ID 写入 schtasks 命令行（凭据仍在应用数据目录）。
- `BatchScheduler` + `BatchRunner` 处理批量 Profile；`Views/BatchRunWindow.xaml` 提供 UI。
- `RecoveryCoordinator.FindRecoveryRequiredAsync` 把未完成 journal 与 NeedsRecovery 事务合并为 `RecoveryItem` 列表。

### 内置 SFTP 服务

[src/FengSync.Core/SftpServer/](src/FengSync.Core/SftpServer)：

- `node-sftp-host.cjs` + `package.json`/`package-lock.json`（固定 `ssh2` 版本）作为嵌入式 Node.js 协议主机；MSBuild 与 [ci-release.yml](.github/workflows/ci-release.yml) 都会按 lockfile 安装到 `SftpServer/`。
- `SftpServerHostedService` 启停 Node 进程，监听配置由 `SftpServerSettingsStore` + `SftpServerSettingsWindow.xaml` 提供，账号/虚拟文件系统/审计日志/认证速率限制各自独立。
- `App.xaml.cs` 启动时如果配置 `Enabled && StartWithApplication` 就拉起 SFTP；退出时停止。

### UI 结构

[src/FengSync/](src/FengSync)：

- `MainWindow.xaml` + `.cs`：主界面（左右端点输入框、比较/同步按钮、计划 DataGrid、状态栏、Profile 列表）。`MainWindow.xaml.cs` 直接调用 `SyncExecutor` 与 `BaselineRepository`，尚未切换到 `ProfileRunner` + `SyncExecutorV2`，新增 UI 流程时应优先接到 `ProfileRunner`。
- `Views/`：CloudEndpointEditor/Manager、ProfileEditor、Recovery、RunHistory、ScheduleWizard、Settings、SftpServerSettings、SyncConfirmation、BatchRun。
- `ViewModels/`：仅放小型的可绑定模型（`ProfileEditorViewModel`、`FeatureOptionViewModel`、`ProfileSectionViewModel`）。
- `Services/`：`CloudEndpointService`（处理 gdrive 浏览器授权）、`ProfileDialogService`。

## 关键不变量与约定

参考 [docs/performance/01-observability-and-benchmarks.md](docs/performance/01-observability-and-benchmarks.md) 起共 8 篇计划文档；[docs/performance/README.md](docs/performance/README.md) 是总览。`SyncRunMetricsHub` ([src/FengSync.Core/Diagnostics/SyncRunMetrics.cs](src/FengSync.Core/Diagnostics/SyncRunMetrics.cs)) 的 counter 必须保持单调正确：

- 一次比较期间左右端点**最多各枚举一次**；执行前、复制验证、基线提交**禁止**调用 `ScanAsync()`。
- 默认比较不读取文件内容；hash 由 `IContentHashEndpoint` 在显式选 `ComparisonMode.Content` 时按需计算。
- 操作数增长时禁止 `O(N²)` 扫描、查找、序列化或写盘——`PlanSnapshot.FromComparison`、`BaselineStateBuilder.BuildNextState`、WAL、`Channel`-化 worker 都是为此存在。
- `EngineFeatureFlags` ([src/FengSync.Core/Configuration/EngineOptions.cs](src/FengSync.Core/Configuration/EngineOptions.cs)) 控制各子系统启停：`snapshot-v2`、`lazy-hash`、`verifier-v2`、`baseline-v2`、`journal-wal`、`device-scheduler`、`rclone-batch`。改默认行为前确认影响面并保留旧路径。
- 阶段名 `SyncPhaseNames` 是诊断契约，不要重命名。

## 阶段计时 / 测试矩阵

`PhaseTimer` 在热路径用 `Stopwatch.GetTimestamp()`，不分配字符串。`PerformanceInvariantTests` 断言：

- `DirectoryScans == 2`（左右各一次，baseline 自身操作不计）；
- 默认比较 `HashBytes == 0`；
- 单文件验证不增加 `DirectoryScans`；
- journal 写入量近似 `O(N)`。

新增指标或阶段名时同步更新这些测试。

## 发布

[.github/workflows/ci-release.yml](.github/workflows/ci-release.yml) 在 `v*` tag push 触发：先 build → `dotnet publish ... -r win-x64 --self-contained` → 在 publish 目录 `npm ci --omit=dev --prefix SftpServer` 拷入固定 ssh2 依赖 → `Compress-Archive` 成 `FengSync-<ver>-win-x64.zip` → 上传 artifact 并由 `softprops/action-gh-release` 创建/更新 Release。tag 必须是 semver（如 `v0.1.11`）。`src/FengSync/FengSync.csproj` 的 `Version`/`AssemblyVersion`/`FileVersion` 由发布 job 覆盖，本地不需要手改。