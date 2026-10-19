# 云盘管理改进 —— 交接文档

> **给接手这条线的人（或下一轮会话）。**
> 分支：`dev`（领先 `origin/dev` **N** 个已提交，未 push）。工作区**有未提交的 F 批次改动**：
> i18n 迁移 + 两个**已提交回归**的修复，见 §2 的 F 与 §2 的「F 收尾时暴露的两个已提交回归」。
> 最后更新：2026-10-06。
>
> 本文是这条线**唯一的权威文档**：路线图（§5）、需求盘点的判断依据、已实证的事实（§4）与项目约定（§7）都在这里。
> 原先那份独立的《云盘功能缺口盘点与演进计划》已把仍有价值的内容（剩余项的现状/缺口/参考做法、实现约束）
> 合并进本文后删除，需要它的原文可以去 git 历史里找（`git log --diff-filter=D -- docs/`）。
>
> **E 组（联动、设置与诊断）已收尾**（2026-10-06）。E1–E5 五个入口全部实现并通过门禁：
> - E1：从云盘端点建同步任务（`EditSyncProfile` 接缝 + 既有 `ProfileEditorWindow` 预填云端那侧）
> - E2：双向同步向导（`ShowSyncWizard` 接缝 + `CloudSyncWizardWindow`）
> - E3：诊断导出（`AskDiagnosticsFile` 接缝 + `CloudDiagnosticsReport`，永远不导出 `rclone.conf`）
> - E4：i18n 骨架（`Tr.F` / `{l:Lang}` 绑定 + 资源文件 + `AppLanguageContext` 切换 zh-CN / en）
> - E5：首启引导（`ShowFirstRunGuide` 接缝 + `CloudFirstRunPrompt`，仅在端点数为 0 时出现）
>
> **F 组（i18n 债务清零）已收尾**（2026-10-06）。`MainWindow.xaml.cs` 与 `Views/RemoteEndpointManagerWindow.xaml.cs`
> 里 **392 处**用户可见的中文字面量已全部迁进资源表（新增 **319** 个键，复用 19 个既有键），
> `LanguageCoverageTests.DeferredMigrationFiles` 这份例外清单已删除 —— 现在这两份文件与新文件受**同一条**规则约束，
> WPF shell 里不再有任何用户可见的中文硬编码。见 §2 的 F。
>
> **G 批次（左列收敛）未提交**（2026-10-19）。云盘管理左列删掉「同步」「整理」两行：同步归主窗口的「新建同步」，
> 端点的重命名 / 分组与标签 / 检查连接 / 删除进**端点行右键菜单**，`刷新列表` 进「工具」行。E1/E2 的入口随之下线
> （规则层保留）。见 §2 的 G。
>
> **F 收尾跑门禁时暴露了两个已提交的回归**（都不在 F 的改动范围内，是 E 组之前就进 `dev` 的）：
> 1. **`AppLanguageStartup.ApplySavedLanguage()` 在 UI 线程上 sync-over-async，磁盘上一旦有
>    `FengSync.local.json` 就死锁**。进程活着、无窗口、CPU 0 —— 桌面应用等于起不来。
> 2. **`MainWindow.xaml` 里两个 `TextBox.Text` 绑到只读索引器 `{l:Lang}`，默认 TwoWay 直接
>    `XamlParseException`**（提交基线根本开不了窗口）。
> 两个都已修复并有回归测试；见 §2 的「F 收尾时暴露的两个已提交回归」。

---

## 0. 30 秒速览

| | |
| --- | --- |
| **在做什么** | 把云盘功能缺口盘点里的 P1 剩余 17 项 + P2 全部 21 项 + 技术债 7 项（共 45 项）补齐，拆成 6 个子批次：**0 → A → B → C → D → E**。每个子批次独立跑门禁、独立提交（**只提交不 push**）。 |
| **已提交** | 批次 **0**（技术债 7 项）、**A0**（Provider descriptor 重构）、**A1**（OneDrive/Dropbox/Box/WebDAV + S3 provider 目录）、**A2**（端点重命名）、**A3+A4**（端点搜索/排序 + 测试全部）、**A5**（代理）、**A6**（分组/标签）、**A7**（端点导出/导入）、**B0–B11**（队列泛化 + 文件浏览器 11 项）、**C1–C7**（传输队列：限速/排序/归档/诊断/计划/完成后动作/右键菜单）、**D1–D6**（挂载：高级选项/恢复/容量/重连/缓存/UNC）、**E1–E5**（联动 + 设置 + 诊断 + i18n + 首启引导）、**F**（i18n 债务清零）。见 §2。 |
| **未提交** | **F 批次**（i18n 迁移：2 个 tsv + 2 个 resx + 2 个被迁移的 `.xaml.cs` + `LanguageCoverageTests`），加上 **F 收尾时发现的两个已提交回归的修复**（`SettingsStore` 同步读 + `AppLanguageStartup` 改用它 + `MainWindow.xaml` 两处 `Mode=OneWay` + `AppLanguageTests` 新回归测试）。见 §2。 |
| **下一步** | 门禁绿后按 `feat` / `test` / `docs` 粒度提交（显式路径 `git add`，只提交不 push）。`.arts/`、`.serena/`、`verify.py` 是未跟踪草稿，不是交付物。 |
| **最容易踩的坑** | 挂载选项键名拼错会被 rclone **静默忽略**；`config/create` 对 OAuth 后端会**重新要求浏览器授权**；rclone **不读 `ALL_PROXY`**；`core/bwlimit` 是 daemon 全局而非 per-fs；UNC 挂载点 rclone 收下但回的是 Windows 分配的盘符；`operations/size` 的 `remote` 字段**静默忽略**导致子树字节数变成整个端点；**绑定到 `{l:Lang}` 的属性只要默认模式是 TwoWay（`TextBox.Text`、`ToggleButton.IsChecked`、`Selector.SelectedItem/SelectedValue/SelectedIndex`、`RangeBase.Value`…）就必须显式 `Mode=OneWay`，否则只读索引器抛 `XamlParseException`**（`ComboBoxItem.Content` 只是其中一个实例）；**UI 线程上不许 sync-over-async** —— `App.OnStartup` 里 `.GetAwaiter().GetResult()` 一个带 `await` 的读盘方法会死锁成「进程活着但没窗口」，而且事件日志里什么都没有。 |

---

## 1. 接手先做的三件事

### ① 先跑一次全量门禁，确认 A6 基线是绿的

```powershell
pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline
```

A3–A6 是分批提交的，只有 A3/A4 有「单跑通过」的记录；A5/A6 依赖提交时门禁绿，但**接手时先自证一次基线**，
后续任何红都能明确归因到新改动上。

- 约 **2m30s**。调用工具时**超时必须给 600000ms**，否则会被掐断在半路。
- 判定标准：日志里出现 **`Passed!  - Failed: 0` 汇总行**。只看日志尾部会误判 —— 被超时掐断时日志正好停在
  `A total of 1 test files matched the specified pattern.` 之后，看起来像正常进行中。
- 日志**写 `$env:TEMP`**，不要写进 `.fengsync-test/`：那个目录会被 `Reset-TestArtifacts` 清空，
  文件被占用时脚本 3 秒就退出（`The process cannot access the file ... because it is being used by another process`）。
- 读数会比文档 §3 的旧预期更高（A5/A6 各加了测试）；以 `Failed: 0` 为准，不追死具体数字。

### ② A7 已实现 —— 跑门禁并提交

A7 的代码与测试已完成（见 §2 的 A7，设计与安全规则留档在 §5）。门禁绿后按 §1 ③ 的粒度提交；这是 A 组最后一项，提交后整个 A 组收尾。

### ③ A7 完成后按约定的粒度提交

按层次拆 `feat` / `test`（`docs` 单独），用**显式路径** `git add`（**不要 `git add -A`**）。**只提交不 push。**

---

## 2. 已完成的工作

| commit | 批次 | 内容 |
| --- | --- | --- |
| `88c7a15` | 0 | `chore(tech-debt)` —— 技术债 7 项（24 文件） |
| `b1631c4` | A0+A1 | `feat(endpoints): provider catalog, and OneDrive/Dropbox/Box/WebDAV`（21 文件） |
| `e18462d` | A2 | `feat(endpoints): rename an endpoint by rewriting its rclone.conf section` |
| `324ff7e` | A2 | `test(endpoints): cover endpoint rename, its rollback and profile migration` |
| `23a945f` | A2 | `test(ui): rename through the real shell, and tolerate a collapsed progress window` |
| `5bafb2b` | — | `docs: replace the gap analysis with a single handoff document` |
| `65de367` | A3+A4 | `feat(endpoints): search, order and check every endpoint from the list` |
| `1264812` | A3+A4 | `test(endpoints): cover the endpoint list's search, order and batch probe` |
| `52b50ac` | A5 | `feat(settings): let the user pin a proxy for the rclone processes we start` |
| `8a6995a` | A5 | `test(proxy): cover the setting, the resolver fallback and the shell wiring` |
| `f4c2476` | A6 | `feat(endpoints): file endpoints under groups and tag them` |
| `6d2001d` | A6 | `test(endpoints): cover grouping, tagging and the heading each row carries` |
| （待提交） | A7 | `feat(endpoints): export and import endpoints without their credentials` |
| （待提交） | A7 | `test(endpoints): cover the export shape and the import file's validation` |
| （待提交） | F | `feat(i18n): move the shell's remaining Chinese literals into the resource tables` |
| （待提交） | F | `test(i18n): drop the deferred migration list now that it is empty` |
| （待提交） | F | `docs: record the i18n batch and retire the deferred migration note` |

### 批次 0 —— 技术债 7 项（`88c7a15`）

`Endpoints.cs` 抽 `ReadPreferredHash(JsonElement)`（`StatAsync` 与 `ScanEntriesAsync` 同口径读 `Hashes`）；
`AppDataPaths.MountCache` + `RcloneDaemon.BuildArguments(configPath, port, cacheDirectory)` 纯函数化（含 `--cache-dir`、`--rc-serve`）；
`Mount/MountOptions.cs` 删死代码、`MountAsync` 返回值 `Task<Guid>` → `Task`；
`FengSync.Cli` / `FengSync.Updater` 补 `<Version>`；`.gitignore` 加 `.claude` 与 `.workbuddy-ai/`；
`RcloneUiError.DescribeDetail` + `CloudTransferRecord.ErrorDetail`；`MountPickerDialog` 盘符分支改走 `MountPointInspector.Validate`；
**12 处动态文本 `TextBlock` 删静态 `AutomationProperties.Name` 并补 `AutomationId`**（`AGENTS.md` 规则）。
新测试：`EndpointSchemesTests`、`RcloneDaemonArgumentsTests`、`ExecutableVersionTests`、`LiveTextAccessibilityTests`，`CloudFeatureBehaviorTests` +4。

### A0 —— Provider descriptor 重构（`b1631c4`）

把原来 12+ 处散落的 switch / `StartsWith` / 硬编码 XAML 面板收敛成两层单一来源：

- **`src/FengSync.Core/EndpointSchemes.cs`** —— 全仓库唯一的 scheme ↔ rclone backend ↔ `EndpointType` 映射表。
  API：`AllSchemes` / `CloudTypes` / `SchemeFor` / `RcloneTypeFor` / `DisplayNameFor` / `IsRemote` / `SchemeOf` /
  `TypeForScheme` / `TypeForRcloneType`（`alias` → S3）/ `IsSelfDescribingRcloneType` / `BuildUri` /
  `RemoteNameOf` / `RenameRemote`。
- **`src/FengSync/Services/CloudProviderCatalog.cs`** —— UI 侧描述符：`CloudFormField(Key,Label,Kind,Required,AutomationId,DefaultValue,Hint,EditLabel)`、
  `CloudProviderDescriptor(Kind,NamePlaceholder,NameAutomationId,RootAutomationId,Fields,RequiresOAuth,SupportsTrash,SupportsPublicLink,DefaultRoot,ConstantParameters,CrossFieldRule)`
  + 派生 `RcloneType`/`UriScheme`/`DisplayName`/`IsS3Compatible`/`MergeParameters`；**7 条描述符，Google Drive 排第一**。
- `ProviderKind` 枚举**已彻底删除**，全部改用 `EndpointType`；`CloudEndpointService.ProviderKind` 不再存在。
- ⚠️ **`EndpointType` 的枚举值只能追加**：`BaselineTransaction` 与 `TransferResumeStateStore` 都持久化 `Type.ToString()`。

### A1 —— 4 个新 Provider（`b1631c4`）

新增 **OneDrive / Dropbox / Box / WebDAV**；`RcloneOAuthFlow.CreateGoogleDriveAsync` → `CreateAsync(remoteName, descriptor, fields, ...)`。
S3 兼容的腾讯 COS / 阿里 OSS / 华为 OBS / 七牛 / 网易：**零代码路径**，rclone 的 s3 backend 自带这些 provider，
只需调 `PreferredS3Providers` 排序。

### A2 —— 端点重命名（`e18462d` + `324ff7e` + `23a945f`）

**实现时推翻了原计划的做法**，因为实证发现「`config/create` → 迁移 → `delete`」走不通：

- 实测（bundled rclone v1.75.0，RC `config/create`）：对 OAuth 后端（drive/onedrive/dropbox/box），
  `config/create` **一定会跑 backend 的配置状态机** —— 返回 `State: "*oauth-confirm,teamdrive,oauth,"`，
  继续驱动会走到 `oauth-islocal → oauth-authorize`（**要浏览器**）。`opt.nonInteractive=true` 不会跳过，只自动取默认值。
  所以「用 `config/create` 克隆一个 OAuth 端点」= **要求用户重新授权**，而不是复制 token。
  对照：`sftp` 的 `config/create` 返回没有 `State` 字段，是干净的。
- 实测：**运行中的 `rclone rcd` 会重新读取 `rclone.conf`** —— 手写/改写一个段之后，下一次 `config/listremotes`
  与 `config/get` 立刻能看到新名字与新内容；随后 daemon 自己调 `config/create` 写回配置时**不会覆盖**手写进去的段。
  → **不需要重启 daemon**。

最终做法：**直接改写 `rclone.conf` 的段**（值逐字节不变）→ 用 daemon 回读验证 → 再迁移两个 JSON → 任一步失败**三个文件一起还原**。

- `src/FengSync.Core/RcloneConfigFile.cs`（新）—— 纯 ini 段手术：`ContainsSection` / `RenameSection(content, old, new, valueOverrides?)` / `RemoveSection`。
  按 `\n` 切分（`\r` 留行尾）以保住 CRLF 与「末尾有没有换行」；**段头只认第 0 列**，所以多行值（PEM）的缩进续行永不被误判成段头。
- `EndpointSchemes.RenameRemote` 用 **Ordinal** 比较：改 `docs` 绝不碰 `docs2`，也不碰 `C:\docs` / `\\server\share\docs`。
- `src/FengSync/Services/CloudEndpointRename.cs`（新）—— `InspectAsync` / `Validate`（纯函数）/ `RewriteProfiles` / `RenameAsync`。
- UI：`RemoteEndpointManagerWindow` 的端点按钮第二行加「重命名」。
  ⚠️ 按钮名叫 **`RenameEndpointButton`**（不是 `RenameButton`）—— 文件浏览器已有一个 `RenameButton`，重名会 `CS0102`。
- 测试：`RcloneConfigFileTests`（Core，10）、`CloudEndpointRenameTests`（16，含真 rclone 的 SFTP 与 S3 alias 端到端 + 两个注入失败的还原用例）、
  `UiFeatureBehaviorTests` +2；`sftp-ui` 桌面场景加了「改名 → 再浏览 → 用新 URI 同步」。
- `AGENTS.md` 里有一条对应的不变式（搜 `Renaming an endpoint cannot go through rclone`），改这块前先读它。

**顺带修掉一个门禁 flake**（`23a945f`）：`Invoke-UiScenario.ps1` 的 `Capture-Element` 曾先做
「元素必须有可见矩形」的检查，再判断「截图跳过」；一个完成太快的本地同步（进度窗正在收起、矩形已归零）
会让整个套件失败（`local` 场景就这么挂过）。该检查已移进真正的截图分支。私有桌面改造后截图不再「跳过」，
但同样的顺序仍然重要：元素矩形归零时 `PrintWindow` 的裁剪会算出零尺寸，所以检查留在分支内部。

### A3+A4 —— 端点搜索/排序 + 「测试全部」（`65de367` + `1264812`）

- `src/FengSync/Services/CloudEndpointList.cs`（新）—— 纯函数：`CloudEndpointSortColumn { Name, Provider, Health }`；
  `Matches(account, query)`（**每个**空白分词都要命中 `name + provider + type + prefix + Display`，第二个词是收窄）；
  `Arrange(rows, query, column, descending)`（名称**恒为最终 tie-break 且不反转**）；`Rank(health)` 升序 = 待办清单
  `NeedsLogin → Unreachable → Unknown → Checking → Connected`。
- `CloudEndpointStatusService` 加 `ProbeConcurrency = 3` + `RunBoundedAsync<T>`（`SemaphoreSlim` 限流、单条失败被收集、取消不算失败、`acquired` 防误释放）。
- UI：`RemoteEndpointManagerWindow` 端点 GroupBox 改为 2 行 Grid（`Auto` 工具条 + `*` 列表），工具条是 **`WrapPanel`**
  （左列仅 340px，必须能换行）：`CloudEndpointSearch` / `CloudEndpointSort` / `CloudEndpointSortDirection` / `CheckAllCloudEndpoints`。
- 测试：`CloudEndpointListTests`（13）、`CloudEndpointCheckAllTests`（12）、`UiFeatureBehaviorTests` +1 个 STA 测试；`sftp-ui` 场景加 4 个控件矩形断言与搜索/排序/全检回读。

### A5 —— 代理（`52b50ac` + `8a6995a`）

**机制原本就在，缺的只是接线**：`RcloneProxyOptions` / `RcloneProxyResolver.Resolve`（优先级 Application > Environment > WinInet）
与 `RcloneDaemon.StartAsync(..., RcloneProxyOptions?)` 早已接受代理，但没人从设置里读。

- `ApplicationSettings.RcloneProxy`（**注意字段名是 `RcloneProxy`，不是计划里写的 `CloudProxy`**）。命名跟随 rclone 而非页面：
  每个进程只有一个 daemon，代理在**进程启动时固定**，它同时管挂载、SFTP 服务和同步运行 —— 所以设置页文案**没有**
  沿用计划里「仅影响云盘」的说法。
- `RcloneProxySettings.From(settings)`（Core）把设置转成 `RcloneProxyOptions`；`LoadAsync` 读盘后解析。
- `RcloneLifecycleHost.ProxyOptionsProvider`（`Func<CancellationToken, Task<RcloneProxyOptions?>>?`）在启动锁内、
  子进程创建前**只调一次**；provider 抛异常则**记日志并回退到环境**，绝不因此拒绝启动 daemon。
- **两个入口都接线**（`FengSync.App` 与 `DriveHub.App`）—— 各自独立 daemon，接一个不接另一个会静默失效。
- ⚠️ **实证修正**：rclone 的 HTTP 客户端**不读 `ALL_PROXY`**（用死端口逐个验证：`ALL_PROXY` 那轮仍能到达目标，
  `HTTP_PROXY`/`HTTPS_PROXY` 那轮 `proxyconnect tcp` 失败）。`RcloneProxyResolver` 现在会把只有 `all_proxy`
  的值镜像进 `http`/`https`。地址只校验**可达性**（不能含空格、必须有 host），不校验 scheme。
- 测试：`RcloneProxySettingsTests`（Core）、`RcloneProxyResolverTests`（Core）、`CloudProxySettingsUiTests`（UI）；`sftp-ui` 场景加了设置往返。

### A6 —— 分组/标签（`f4c2476` + `6d2001d`）

计划里写的是「给 `CloudEndpointMetadata` 加 `Group`/`Tags`」，实现把**业务逻辑**抽成纯函数而非堆在窗口里：

- `CloudEndpointMetadata` 加 `Group`（空 = 未分组）与 `Tags`（`IReadOnlyList<string>?`）。两者都是**本应用自己的概念**，
  rclone 无处存放，只进 `cloud-endpoints.json`，**绝不进 `rclone.conf`**；旧文件缺这两个字段时按 null 读，向后兼容。
- `src/FengSync/Services/CloudEndpointOrganization.cs`（新）—— `NormalizeGroup` / `NormalizeTags` / `ParseTags`
  （接受全角 `，；`、顿号 `、`、换行）/ `FormatTags` / `GroupLabel`（未分组统一显示为「未分组」）/ `Merge`（保存分组时
  优先用盘上记录，端点是手工加的、没有 metadata 时新建一条，保证「保存分组绝不静默失败」）/ `Preserve`（创建/编辑端点
  重建记录时**保留**用户的分组与标签 —— 否则改一次根目录就把归档抹掉）/ `Describe`（状态文案，单独可测）。
- UI：`CloudEndpointOrganizationWindow`（新）编辑单个端点的分组/标签；`RemoteEndpointManagerWindow` 列表**按组分区**显示，
  搜索也命中标签。
- 测试：`CloudEndpointOrganizationTests`（Core/UI 纯函数 ~195 行）、`CloudEndpointListTests` +116、`UiFeatureBehaviorTests` +95；`sftp-ui` 场景加了组织编辑往返。

### A7 —— 端点导出/导入（**已完成，未提交**）

- `src/FengSync/Services/CloudEndpointPortability.cs`（新）—— 纯逻辑与不可信输入校验：
  `CloudEndpointExport` / `CloudEndpointExportBundle` / `CloudEndpointDraft` / `CloudEndpointImport`；
  `Serialize` / `ToExport` / `Parse` / `ValidateRemoteName` / `ValidateParameterName` / `UniqueName`；
  源生成 context 用 camelCase + `WriteIndented`，文件可读且可 diff。
- **导出只读参数名，不读值**：`CloudEndpointService.LoadParameterNamesAsync(account)` 取
  `GetRemoteParametersAsync` 的 `Keys`（S3 强制前缀端点读隐藏 backing remote），`BuildExportBundleAsync` 组装。
  导出文件的结构里**没有放凭据的字段** —— 安全来自形状，而不是靠调用方自觉。
- **导入是不可信输入**：`Parse` 逐条校验 —— 版本必须为 `1`；`type` 必须能映射到 `EndpointSchemes` **且**有
  `CloudProviderCatalog.TryFor` 的 descriptor；remote 名拒绝 `:` `/` `\`、前后空白、以 `.` 开头、超 64 字符
  （防段头注入）；参数名只允许字母数字与 `_ -`；同文件重名按大小写不敏感拒掉后来的。坏行丢弃并返回消息，
  不让一行毁掉整个迁移。
- `CloudEndpointService.SaveOrganizationAsync` 在编辑器创建端点后补写分组/标签（编辑器不负责归档）。
- `CloudEndpointEditorWindow` 新增 `CloudEndpointEditorWindow(CloudEndpointDraft)` **预填新建**模式：固定服务、
  预填名称与根目录，名称可编辑、凭据全空，保存走既有 `EnsureRemoteAsync` / `SaveS3Async` 路径。
  `LoadProvidersAsync` 与 `ApplyProviderDefaults` 对 draft 各加了一处保护（不覆盖预填的 provider、不 seed 默认 region）。
- UI：`RemoteEndpointManagerWindow` 工具条加 `导出…` / `导入…`（`ExportCloudEndpoints` / `ImportCloudEndpoints`，
  Tertiary 样式）。导出走 `SaveFileDialog`；导入走 `OpenFileDialog` → 校验 → 逐端点打开编辑器，**冲突名用
  `UniqueName` 自动改名而不是覆盖**，取消即跳过，结束时刷新并汇总“成功/跳过”。
- 测试：`CloudEndpointPortabilityTests`（11 纯函数：导出形状、不含凭据、往返、版本、坏 JSON、注入名、未知类型、
  坏参数名、重名、`UniqueName`）；`UiFeatureBehaviorTests` +2 STA（draft 预填 SFTP 与 S3）；`sftp-ui` 场景的
  工具条矩形断言加上两个新控件。

### 批次 F —— i18n 债务清零

E4 留下的最后一笔债：`MainWindow.xaml.cs` 与 `Views/RemoteEndpointManagerWindow.xaml.cs` 是**最早写的中文界面**，
当时整份文件都是中文字面量，所以 E4 把它们放进 `LanguageCoverageTests.DeferredMigrationFiles` 豁免掉，
只让新代码受规则约束。F 把这份豁免清单**删掉**，并把两份文件里的字面量全部迁进资源表。

- 规模：**392 处**用户可见中文字面量（`MainWindow.xaml.cs` 175、`RemoteEndpointManagerWindow.xaml.cs` 217），
  去重后 **338** 个模板；新增 **319** 个键（`Main.*` 139 个进 `80-main.tsv`，`Mgr.*` 180 个进 `30-cloud-manager.tsv`），
  另外 **19** 个复用既有键（`Settings.Ok`、`Profile.SideLeft/SideRight`、`SyncWizard.LocalFolder`、`Mgr.Delete`、
  `Mgr.Rename`、`Mgr.NewFolder`、`Mgr.MoveToTrash`、`Mgr.Unmount`、`Mgr.FavoriteThisDirectory`、`Mgr.GridView`、
  `Endpoint.Saving`、`Schedule.Deleting`、`Settings.LogTitle`、`Settings.NoIncompleteLogs`、`Main.NoEndpoint`、
  `Main.NoProfileSelected`、`Main.ProfileWorkspace`、`Main.EmptyPickEndpoints`）。
- **踩到的两个坑（都值得记住）**：
  1. **门禁自己的字面量正则会截断带嵌套字符串的插值串**。`LanguageCoverageTests.StringLiteral` 是
     `(?<value>@?"(?:[^"\\]|\\.)*"?)`，遇到 `$"已{(mode == Cut ? "剪切" : "复制")} …"` 会在内层第一个 `"`
     就收尾，于是报告里出现的是**截断片段**。所以**不能拿门禁的违规清单去驱动改写**，要另写一个懂 C# 字面量
     语法的扫描器（常规 / 逐字 / 插值 / 插值逐字，洞里的嵌套字面量递归处理）。
  2. **`string.Format` 的格式说明符必须留在资源串里，不能留在实参里**。`$"{DateTime.Now:yyyy-MM-dd HH:mm}"`
     要变成 `Tr.Format("Main.LastRun", DateTime.Now)` 配 `上次运行 · {0:yyyy-MM-dd HH:mm}`；把 `:yyyy-MM-dd HH:mm`
     留在实参里会直接编译不过。同理 `{fingerprint.Size:N0}` → `Tr.Format(..., fingerprint.Size)` 配 `{3:N0}`。
- **一处不能机械替换**：`OperationSeverity` 原来是 `OperationLabel switch { "上传" => … }` ——
  **switch 的模式标签必须是编译期常量**，换成 `Tr.F(...)` 编译不过。已改成 `OperationLabel == Tr.F("Main.KindUpload") ? … : …`
  的链式比较，语义与原来逐字等价。
- **一处只能靠删掉才成立**：`OperationLabel` 是**显示字符串**，而 `ApplyRowFilter` / `OperationSeverity` 拿它做比较。
  迁移后两侧必须用**同一个键**（都用 `Main.KindUpload` 这一族），否则英文界面下筛选会静默失效。
- 顺带：`localization/*.tsv` 必须保持 **LF**。本机 `core.autocrlf=true`，`git checkout -- <tsv>` 会把工作区文件
  写成 CRLF，而 `LanguageCoverageTests` 只 `TrimEnd('\r')`，空行会变成 `'\r'`（1 列）→ 报
  「a row must have three tab separated columns, found 1」。改写 tsv 后要确认行尾仍是 LF。

### F 收尾时暴露的两个已提交回归

F 的迁移本身是绿的（`LanguageCoverageTests` 5/5），但**把 `DeferredMigrationFiles` 例外清单删掉之后**门禁才真正
开始跑 WPF UI 场景，于是暴露出两个与 F 无关、但一直躺在 `dev` 上的回归。两个都**只在「应用能不能起来」这一步**
失败，症状都是「进程活着但没有窗口」，所以一开始很容易被当成测试基础设施问题。

**① 启动时 sync-over-async 死锁（E 组引入，`c89d9a7`）**

`AppLanguageStartup.ApplySavedLanguage()`（`src/FengSync/Localization/AppLanguageStartup.cs`）在
`App.OnStartup` 里用 `new SettingsStore().LoadAsync().GetAwaiter().GetResult()` 阻塞读设置。`OnStartup` 跑在
dispatcher 帧里，此时 `SynchronizationContext.Current` 已经是 `DispatcherSynchronizationContext` —— 于是
`LoadAsync` 里 `await` 之后的续体被投回**那个正在被 `GetResult()` 阻塞的线程**。

- 关键在 `SettingsStore.LoadAsync` 的第一行：`if (!File.Exists(_path)) return new(new ApplicationSettings());`
  —— 文件不存在时整个方法**同步返回**，没有续体，所以「空数据目录」恰好不炸。磁盘上**有**设置文件时才会
  `await`，于是 100% 死锁。这就是为什么它在开发机上时灵时不灵、而每个真实用户都必然踩到。
- 症状：进程存活、`MainWindowHandle == 0`、`tasklist /V` 的 Window Title 是 `N/A`、CPU 时间 0。事件日志里
  **没有任何崩溃记录**（它没崩，是卡住了）。
- 修法：给 `SettingsStore` 加一个**真正同步**的 `Load()` / `Save()`（`File.ReadAllText` +
  `JsonSerializer.Deserialize`），启动路径改用它。没有用 `Task.Run(...).GetAwaiter().GetResult()` 绕过 ——
  那只是把 sync-over-async 挪到线程池上，注释里写着「blocking on purpose」的地方应该真的同步读。
  迁移 / 备份 / 损坏隔离的规则抽成 `Migrate(...)` 一处，`Load` 与 `LoadAsync` 共用，行为逐字不变
  （`ConfigurationTests` 里那两个备份/迁移用例仍然绿）。
- 回归测试：`AppLanguageTests.The_startup_language_read_returns_on_a_thread_that_owns_a_dispatcher` —— 在
  **装了 `DispatcherSynchronizationContext` 且不 pump** 的 STA 线程上调用 `ApplySavedLanguage()`，用
  `ManualResetEventSlim.Wait(15s)` 兜底，所以失败是一条断言而不是挂住的测试套件。注意**原来那个
  `The_language_survives_a_save_and_a_reload` 抓不到这个 bug**：它是 async 测试，跑在 xunit 的线程池线程上，
  那里没有 dispatcher 上下文，恰好绕开了唯一的失败条件。

**② `TextBox.Text` 绑到只读索引器（更早的 E4 之前就进了 `dev`）**

`MainWindow.xaml` 的 `LeftPath` / `RightPath` 两个 `TextBox` 把 `Text` 绑到 `{l:Lang}`：

```xml
<!-- 错：TextBox.Text 默认 TwoWay，而 AppLanguageContext.Current 是只读索引器 -->
Text="{Binding [Main.ChooseEndpoint], Source={l:Lang}}"
<!-- 对 -->
Text="{Binding [Main.ChooseEndpoint], Source={l:Lang}, Mode=OneWay}"
```

`XamlParseException: 设置属性"System.Windows.Controls.TextBox.Text"时引发了异常。行号为"209"，行位置为"361"`
→ 内层 `InvalidOperationException: 无法对"FengSync.Localization.AppLanguageContext"类型的只读属性"Item"进行
TwoWay 或 OneWayToSource 绑定`。窗口构造时抛在 `Application.Run` 里 → 进程**被终止**（这个才会进事件日志）。

- 这条坑在 §0 的「最容易踩的坑」里原本只写了 `ComboBoxItem.Content`。**实际规则更宽**：凡是绑定到
  `{l:Lang}` 的属性，只要它的**默认绑定模式是 TwoWay**（`TextBox.Text`、`ToggleButton.IsChecked`、
  `Selector.SelectedItem/SelectedValue/SelectedIndex`、`RangeBase.Value`…），都必须显式 `Mode=OneWay`。
  已全仓核对过一遍：541 处 `{l:Lang}` 里，绑定 `Text=` 的 `TextBox` 只有这两个，其余 `TextBox` 上的
  `{l:Lang}` 都是 `AutomationProperties.Name` / `ToolTip`（默认 OneWay），所以没有第三个。
- 排查手法值得记住：**这类崩溃只在事件日志里看得见**（`Get-WinEvent -LogName Application` 里
  `.NET Runtime` 提供程序 + `Application: FengSync.exe` + `XamlParseException`），UI 场景日志只会说
  「Main window shell did not become ready」，因为 `Start-App` 等的是 `$p.MainWindowHandle`。

**一个把上面两条都放大的测试基础设施问题**：`Invoke-UiScenario.ps1` 里
`$launch = Start-App; $process = $launch[0]` —— `Start-App` 一旦在 `Wait-Until` 里抛（应用没起来），
`$launch` 就没被赋值，`$process` 保持 `$null`，`finally` 里的 `Stop-App $null` 直接返回，**应用成为孤儿**。
而它继承了 xunit 宿主重定向的 stdout 管道，于是 `ReadToEndAsync` 永远等不到 EOF，整个 UI 阶段会**挂到
15 分钟超时才报错**（日志停在 `A total of 1 test files matched the specified pattern.`）。看到这个症状时先查
`tasklist` 里有没有残留的 `FengSync.exe`，不要先怀疑测试本身。

### 批次 G —— 云盘管理左列收敛（**未提交**，2026-10-19）

用户要求把左列的按钮带收掉一截：同步归主窗口，整理归端点行的右键菜单。

- **「同步」一行删除**：`同步向导` / `创建同步任务` 两个按钮连同接缝（`EditSyncProfile` / `ShowSyncWizard` /
  `AskEndpoint`）一起下线 —— 主窗口已有「新建同步」，这扇窗不再重复提供。**规则层保留**：`CloudSyncTask` 与
  `CloudSyncWizardWindow` 仍在仓库里、仍由 `CloudSyncTaskTests` 覆盖纯规则，但这扇窗里已经没有控件能点到它们。
  要恢复就把入口接回去；要清理是另一次独立决定（删掉它们要连 `Task.*` / `SyncWizard.*` 27 个键一起删）。
- **「整理」一行删除**：`重命名… / 分组与标签… / 删除` 与 `检查连接` 搬进**端点行的右键菜单**
  （`BuildEndpointMenu`，代码搭的，AutomationId `CloudEndpointMenu`，四项分别是
  `CloudEndpointMenuCheck/Rename/Organize/Delete`）。右键先选中指针下那一行（`PreviewMouseRightButtonDown`），
  否则「删除」作用的是用户离开时选中的那一行；没有选中行时不弹菜单（`e.Handled = true`）。删除项带端点名
  （`Mgr.DeleteEndpointMenuItem`），与一控件之隔的文件行 `删除…` 区分开 —— 两者删的东西完全不同。
- **`刷新列表` 移进「工具」行**（新 AutomationId `RefreshCloudEndpoints`）。它与 `测试全部` 问的是同一类问题
  （「外面有什么」），而且是这一行里唯一不改变任何东西的命令。
- **`检查连接` 与 `测试全部` 不是同一个东西**，所以两个都留着：前者只探选中那一个端点、把结果写回那一行；
  后者按 `ProbeConcurrency = 3` 探当前列表里的**全部**并给一行汇总。把前者收进右键菜单，是因为列表很长时
  「只问这一个」才是便宜的那个问题。
- 门禁连带：`LanguageCoverageTests` 要求「每个键都被用到」，随行的 **20 个 `Mgr.*` 键**已从
  `30-cloud-manager.tsv` 删除并重新生成两份 `.resx`（1781 → 1761 条）。`CloudEndpointTaskEntryTests` 删掉
  E1/E2 的七个用例、保留 E3/E5；`UiFeatureBehaviorTests` 里两处按钮断言改成断言行菜单；
  `sftp-ui` 场景改成**右键开菜单再点「重命名…」/「分组与标签…」**，并给 `RefreshCloudEndpoints` 加了矩形断言。
- 左列的宽度注释也重写了：MinWidth 仍是 420，但「工具」行现在是五个按钮，窄端最多折成两行 —— 原来
  「四行各自一行」的实测结论已经不成立。

### E5 的模态首启引导 vs. UI 场景（测试夹具问题，不是回归）

上面两条修完之后 `cloud-transfers` 仍然红，但换了个位置：**「The transfer window did not close.」**（跑三次都红；
如果把 `Close-Window` 加长诊断，有时变成「The cloud manager did not close.」）。查下来是 E5 新加的
`CloudFirstRunPrompt` 造成的：

- `MaybeShowFirstRunGuideAsync()` 在管理器初始化最后调用，`ShouldShow(guideSeen, endpointCount)` 为真时
  走 `AskFirstRunGuide()` → `new CloudFirstRunPrompt { Owner = this }.ShowDialog()`，**是模态的**。
- 而 `cloud-transfers` 的设定正是「**云盘管理器在没有任何端点时打开**」——E5 之前这个状态没有对话框，所以这个
  场景一直能用；E5 之后它必然弹一个模态框出来。
- 现象很有迷惑性：`WindowPattern.Close()`（`Close-Window` 用的）**调用成功、不抛异常**，但窗口在 30 秒里纹丝不动。
  期间 UIA 读窗口属性一直是通的（UI 线程在泵消息），所以不像卡死。逐层排除后确认变量只有一个：
  **把 `CloudGuideSeen` 置为 true、不弹这个模态框，场景 2.1 秒全绿**，两个窗口都干净关闭。
- 判定：模态框本身是 E5 的设计（它有自己的 `CloudFirstRunGuideTests` 与 `ShowFirstRunGuide` 接缝），
  **不是产品缺陷**；缺陷在测试夹具——它给应用造了一个「首次运行」的数据根，而这几条场景讲的都不是新手引导。
- 修法：夹具写 `FengSync.local.json` 时一并种下 `"CloudGuideSeen":true`，并在脚本里写清为什么。这既让场景
  回到「已经引导过的用户」这个常态，也顺手把 `sftp-ui` 从「靠 `InvokePattern` 绕过模态去点一个被禁用的管理器」
  变成了正常交互。
- 顺带把 `Close-Window` 的 `catch { }` 改成往 harness.log 记一行「请求关闭了哪个窗口 / 请求本身是否失败」。
  原来它把原因吞掉，于是失败只能表现为 30 秒后的一句「did not close」，这一条日志本该一开始就有。

---

## 3. A3/A4 的设计要点与遗留缺口

代码已提交，文件与测试清单见 §2 的 A3+A4。这里只留**日后改这块必须知道的三件事**和**一处尚未补的缺口**。

### ⚠️ 三个必须知道的设计要点（最容易踩）

1. **`_browsedEndpoint` 守卫的第一版是错的**（本批自己抓出来的）：重建 `_accounts` 会先抛一次
   **「无选中」**的 `SelectionChanged`，把 `_browsedEndpoint` 清成 `null`，于是**每次击键仍然会重新列一次远端目录**
   —— 一次网络往返。修法：重建期间用 `_rearranging` 静音事件，由 `ApplyEndpointView` 在重建**结束后**
   自己调 `FollowSelectionAsync()` 决定要不要浏览。事件处理器只负责**用户手势**。
2. **空结果必须显式 `ClearListing()`**：`_accounts.Clear()` 之后 `SelectedIndex = -1` 在**已经**是 -1 时**不触发事件**，
   所以「过滤命中 0 行」要自己清空文件面板并把 `_currentPath` 置空；否则会出现「列表空、面板还显示上一个端点的文件」。
3. **`CheckAllButton.IsEnabled = _accounts.Count > 0`**（跟随**可见行**，不是 `_loaded`），`CheckAll_Click` 取 `_accounts` **快照**
   （搜索框在跑的过程中是活的，过滤变了不能让进度读数和总结统计到另一批）。`DescribeCheckAll(0,0,0)` 有单独分支
   —— 「0 个端点全部连接正常」是一句真话，但意思正好相反。

### 遗留缺口

- **A1 未覆盖的新 Provider 编辑表单**：`sftp-ui` 桌面场景的 Provider 遍历目前只有
  `Google Drive / S3 Bucket / SFTP`，**未覆盖新增的 OneDrive/Dropbox/Box/WebDAV**。建议在 A7 之后、进 B 组之前补上
  （这是「加 Provider 请照抄既有模式」这条约定的现成反例）。

---

## 4. 已实证的事实（不要再猜）

### rclone（v1.75.0，bundled，tags=cmount）

| 事实 | 影响 |
| --- | --- |
| `config/create` 对 OAuth 后端**必跑**配置状态机（`State: "*oauth-confirm,teamdrive,oauth,"`），`opt.nonInteractive=true` 不跳过 | 克隆 OAuth 端点 = 要求重新授权；重命名只能改写 conf |
| 运行中的 `rclone rcd` **会重新读取** `rclone.conf`，且**不覆盖**手写进去的段 | 改 conf 不需要重启 daemon |
| `File.Move(tmp, path, true)`（换 inode）与原地写一样能被拾取 | 原子写 conf 是安全的 |
| config RC 命令集**没有 rename** | 见上 |
| curl 调 RC **必须带** `-H "Content-Type: application/json"` | 否则报 `Didn't find key "name" in input` |
| 挂载选项键名**大小写不敏感**，但**未知键被静默忽略**（挂载照常成功） | 拼错 = 静默失效，唯一防线是断言发出去的 JSON 键名 |
| `readOnly` 属于 **`vfsOpt`** 而非 `mountOpt` | D 组必读 |
| `_config:{CacheDir}` 被**静默忽略**；只有 `rclone rcd --cache-dir` 生效（进程启动时解析一次） | 缓存目录不能靠 RC 改 |
| duration 字段拒绝纯数字，接受字符串 `"5s"` / `"1h"` | |
| `config/create` 同名是**整段替换**；写回 obscured 值**必须** `opt.noObscure=true` | 否则二次加密静默损坏密码 |
| `mountPoint` 路径**必须不存在**；卸载要用反斜杠形式 | |
| `operations/copyfile` **原生支持跨 fs**；`drive` backend 默认 `use_trash=true` | B8/B9 的地基 |
| rclone 的 s3 backend 自带 **53 个 provider**（含 `TencentCOS`/`Alibaba`/`HuaweiOBS`/`Qiniu`/`NetEase`），**没有 Backblaze** | 「加国产 S3 兼容」是零代码路径；B2 走 s3 的通用 `Other` provider |
| OAuth 状态机对所有 OAuth backend **完全相同** | Drive/OneDrive/Dropbox/Box 共用一套流程 |
| `rclone mount` 的 fs 串是 `<name>:`，在**挂载时**解析 | 改名时若有活跃挂载 → 直接阻断（A2 已实现） |
| rclone 的 HTTP 客户端**只读 `HTTP_PROXY`/`HTTPS_PROXY`**，**不读 `ALL_PROXY`**；RC 无全局 proxy 透传 | 代理只能靠子进程环境变量传；`RcloneProxyResolver` 把 only-`all_proxy` 镜像进 http/https（A5） |

### WPF / 测试

| 事实 | 影响 |
| --- | --- |
| `UiConsistencyGuardrailTests.PageLocalButtonVisual` 的正则**只对 `<Button>` 元素的属性区**生效 | 给 `TextBox` 设 `Padding` 是安全的；`<Button>` 上一律不能写 `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush` |
| `LiveTextAccessibilityTests` **只扫 `TextBlock`** | `TextBox`/`ComboBox`/`Button` 上的静态 `AutomationProperties.Name` 是允许的；动态文本 `TextBlock` 必须用 `AutomationId` |
| 无桌面 WPF 测试模板 = `RunSta(Action)` + `EnsureApplicationResources()` + **从不 `Show()`** | 毫秒级，不需要前台会话 |
| UI 测试**默认跑私有桌面**：`CreateDesktop` + `CreateProcessW(lpDesktop)`，应用照常开真实窗口、抢自己那个桌面的前台；`-ShowOnScreen` 才回到当前桌面 | 不得重新引入窗口停靠（`-32000`/`SetWindowPos`/`ShowWindow`）或应用侧 `TestWindowPlacement`；不得用 `Process.MainWindowHandle` 找主窗口（`PrivateDesktopHarnessTests` 三条都会拦） |
| `RcloneLifecycleHost.GetClientAsync` **会真的起 rclone 进程**，而 `IAppServiceHost.RcloneHost` 是 **sealed 具体类**（无法替换） | 「不碰 rclone」的测试必须靠**不触发 `BrowseAsync`**，并用「`StatusText` 没被写过」来证明（`AssertNoDirectoryListing` 就是这个） |
| 桌面场景里 `AutomationProperties.Name` 会**覆盖** `Content` 派生的名字 | 别想用 UIA 的 `Name` 去断言一个「标签会变」的按钮；改断言它的 `SelectionPattern` / 布局 |
| WPF 跨进程 UIA 客户端拿不到 `TextBlock` 的 `TextPattern` | 断言文本要靠 `Name`（因此动态文本 `TextBlock` 必须**去掉**静态 `Name`） |

---

## 5. 剩余路线图（A7 → E）

> 这一节同时是那份 307 行改进计划的替代。**A0–A6 已完成**（见 §2），本节的 A5/A6 已移入 §2。
> **剩余项的顺序与依赖**：A7 独立；B 与 C 共享「队列泛化」，所以 **B0 必须在 B 的跨端点复制之前落**；
> D 与 A/B/C 完全解耦；E 是收尾集成。**A7 已完成，下一批是 B0。**

### A7. 端点导出/导入（原 A9）—— ✅ 已完成，见 §2

**实现已完成**（见 §2 的 A7）；下面是设计与安全规则的留档。地基齐了：`CloudEndpointMetadataStore` 已有原子写（temp + `File.Move(..., true)`）与 `Gate` 串行化；
`RcloneConfigFile` 已有第 0 列段头的解析基础。**复用它们，不要新造写盘逻辑。**

**导出什么**（非敏感子集，**值一律留空**）：
- `cloud-endpoints.json` 的记录本身不含凭据：`RemoteName` / `Type` / `Provider` / `Bucket` / `Subdirectory` /
  `AliasOf` / `Group` / `Tags` 直接可导。
- 每个端点的**参数名清单**：从 `rclone.conf` 的段里读**键名**。⚠️ `sftp.pass` 是 obscured、`s3.secret_access_key` 是明文
  —— **两种都只写键名，不写值**。
- 文件头带 `Version` + 生成时间，方便将来演进与拒绝不认识的版本。

**导入怎么做**：让用户逐端点补参数，再走**既有的创建路径**（`CloudEndpointEditorWindow` / `CloudEndpointService`），
由它把值写进 `rclone.conf`。两条硬约束：**导入不得直接写 `rclone.conf` 的凭据段**；
**导入的 JSON 不可信** —— 远端名与路径要校验，防止 `:` 段头注入或 `../` 越界。

**测试点**（照 §7 铁律）：
- 断言导出内容**不含** conf 里的密码/secret/token/obscured 串（用已知口令的 SFTP 与 S3 端点跑）。
- 参数名清单含 `host`/`user`/`pass`，但 `pass` 的值为空。
- 导入被篡改的 JSON（多出段头、远端名带 `:`）应被**拒绝**而不是写坏 conf。
- 往返：分组/标签/别名（S3 强制前缀）都保留。

### B0. 队列泛化（**前置**，与批次 C 共用）

- `CloudTransferDirection` 加 `Copy`。
- `CloudTransferTask` / `CloudTransferRecord` 加 `string? TargetUri`（承载目标远端，**不挪用 `LocalPath`**）。
- `CloudTransferQueue.RunAsync` 加分支：`Copy` 时 source/target **都从 URI 建端点**，
  调新增的 `EndpointTransfer.CopyServerSideAsync`，并**跳过**
  `CopyToStagingAsync` / `PublishAsync` / `PrepareRetryAsync` / `DiscardStagingAsync`。
- ⚠️ **远端 → 远端无法 append，会退化为不可续传** —— 这是设计取舍不是 bug，**UI 必须明说**。

### B1–B11. 文件浏览器（缺口最大的一组）

> 需求盘点原本列了 21 项（B1–B21）；其中 P0 的 7 项 —— 新建文件夹、重命名、删除、多选批量、搜索、排序、
> 面包屑 + 地址栏 —— **已在批次 1 落地**。下表是**剩余 11 项**，「现状 / 缺口」与「参考做法」两列是从原盘点文档
> 一并搬过来的，以免那份文档消失后丢掉判断依据。

| 本计划 | 原编号 | 内容 | 现状 / 缺口 | 参考做法 |
| --- | --- | --- | --- | --- |
| B1 | B8–B9 | 复制 / 剪切 / 粘贴 + 跨端点复制 | `RcloneRcClient.CopyFileAsync` **已存在且原生支持跨 fs**，云盘 UI 一个都没接 | 网盘标配；Air Explorer 支持云到云 |
| B2 | B10 | 上传冲突细化 | 现在只有一次性弹窗「全部覆盖 / 全部跳过 / 取消」，缺 **`KeepBoth`（重命名保留两者）** 与**逐文件询问** | 网盘标配 |
| B3 | B11 | 属性 / 详情 | 无大小统计、无权限、无校验值（`ReadPreferredHash` 已抽好） | 网盘有「详细信息」 |
| B4 | B12 | 分享链接 | rclone `operations/publiclink` 存在，UI 未接 | 网盘核心功能之一 |
| B5 | B13 | 回收站 | 无软删除概念；GDrive 可 `--drive-use-trash`，S3/SFTP **无回收站，只能硬删** | 网盘默认「移到回收站」，可还原 |
| B6 | B14 | 键盘快捷键 | F5 / Del / F2 / Ctrl+A / Ctrl+C / Ctrl+V 全无 | 通用 |
| B7 | B16–B17 | 网格视图 / 缩略图 / 内置预览 | 只有列表；图片、文本、PDF 都要先下载再用外部程序打开 | 网盘有列表/网格切换与在线预览 |
| B8 | B18 | 收藏 / 最近访问 | 无 | 网盘有「我的收藏」「最近」 |
| B9 | B19 | 文件夹大小 | 需要递归统计，没有「计算大小」入口 | 网盘可在文件夹属性里看到 |
| B10 | B20 | 拖出整个文件夹 | **明确不支持**（代码里提示用户先进入文件夹） | 网盘 / Explorer 支持整目录拖出 |
| B11 | B21 | 版本历史 | 无；部分后端（GDrive、开了版本控制的 S3）可能支持 | 网盘有「历史版本」 |

B 组还有一条**只在这里出现**的约束：**并发数从 `const` 改为可配时**，要保证 `SemaphoreSlim` 重建不破坏在途任务；
`ActiveCount` 与 `MaxConcurrentTransfers` 在 `CloudTransferQueue.RunAsync` 里是**当作进度参数**传给
`EndpointTransfer.CopyToStagingAsync` 的，所以改并发会**同时影响续传判定**。
（B 组其余约束与 §7 的通用铁律重合，不再重复。）

### C1–C7. 传输队列

| # | 内容 | 关键改动 |
| --- | --- | --- |
| C1 | 限速（C5） | 用 RC `core/bwlimit`（已实证存在）设全局速率；设置页加「上传/下载限速」；队列在任务开始/结束时应用。**先实证作用域是全局还是 per-fs** |
| C2 | 优先级 / 排序（C7） | 队列现为 FIFO + waiter；加显式 `Order` 并让 `AcquireSlotAsync` 按序取；行内上移 / 下移 |
| C3 | 传输历史（C8） | 新增 `CloudTransferHistoryRepository`（照 `RunHistoryRepository` 的 **append-only** 模式）；`ClearFinished` 之前先归档 |
| C4 | 失败诊断（C9） | 消费批次 0 的 `CloudTransferRecord.ErrorDetail`；传输窗加「复制详情」「查看日志」 |
| C5 | 计划传输（C10） | 设置「仅在此时间段传输」；实现为**队列在窗口外暂停**，不引入系统级调度器 |
| C6 | 完成后动作（C11） | 队列 drained 时（复用已有的 `BatchCompleted` 事件）执行关机 / 休眠 / 退出，**需二次确认** |
| C7 | 队列右键菜单（C13） | 「打开所在文件夹」「复制链接」（依赖 B4 的 `publiclink`） |

### D1–D6. 挂载

**先记住这串键名 —— 拼错会被静默忽略，这是 D 组唯一的防线：**

- `mountOpt`：`volumeName`、`networkMode`、`allowNonEmpty`、`allowOther`、`defaultPermissions`
- `vfsOpt`：`diskSpaceTotalSize`、`readOnly`、`cacheMode`、`cacheMaxSize`、`cacheMaxAge`、`cacheMinFreeSpace`、
  `cachePollInterval`、`writeBack`、`dirCacheTime`、`pollInterval`、`noChecksum`、`noModTime`、`noSeek`、`umask`
- ⚠️ **`readOnly` 在 `vfsOpt` 而不是 `mountOpt`**；`--volname` / `--network-mode` 在 `mountOpt`。

| # | 内容 | 关键改动 |
| --- | --- | --- |
| D1 | 高级选项（D1/D5） | `MountOptions`（`src/FengSync.Core/Mount/MountOptions.cs`，现为 static class + const）保留 `CacheRoot`/`CacheDirectoryFor` 静态 helper，新增**可序列化**的 `MountOptionsSettings` record；`CreateVfsOptions(settings)` + 新增 `CreateMountOptions(settings)`；`RcloneMountService` 里 `mountOpt = new { }` 改为透传。`MountPickerDialog` 加「高级选项」折叠区 |
| D1b | **`MountTarget` 不要改** | 不改 record（避免波及 `MountPickerDialog`、`RcloneMountServiceTests`、`MountPointInspectorTests`、`CloudEndpointEditTests` 四个测试文件），新增 `MountRequest(MountTarget Target, MountOptionsSettings? Options = null)` + `MountAsync(MountRequest)` 新重载，旧 `MountAsync(MountTarget)` 转发 |
| D2 | 开机自动挂载（D2） | 新建 `AppDataPaths.Root/mounts.json` + `MountRecordStore`（`RemoteName/Provider/MountPoint/Kind/RootPath/Options/AutoMount`，**不存凭据**）。恢复策略：**默认只提示「上次有 N 个挂载未恢复」+ 一键恢复**，每条记录 `AutoMount` **默认关**。钩子放 `App.OnStartup` 与 DriveHub 入口 —— **两个 exe 各自独立 daemon**，都要能恢复 |
| D3 | 真实容量（D3） | 挂载时用 `operations/about(fs)` 取 `total` 写 `vfsOpt.diskSpaceTotalSize`（int64 字节）。局限要写进 UI：**挂载时固定，配额变化后不更新**；`about` 返回空（S3/SFTP 常见）时**不传该键，绝不写 0** |
| D4 | 自动重连（D4） | 挂载点失效检测（`ScanAsync` 已有扫描）→ **提示重挂，不做静默后台重连** |
| D5 | 缓存管理（D6） | 显示 `MountOptions.CacheRoot` 占用并提供清理；`CacheMaxSizeBytes = 10GB` 改为可配 |
| D6 | UNC 挂载（D7） | **先实证** rclone `mountPoint` 是否接受 `\\host\share`；不可行就明确标注不支持 |

D 组的测试要点：`MountOptionsSettings` → RC 载荷的**键名断言**（防「拼错静默失效」的唯一手段）；
`MountRecordStore` 往返 + 不含敏感字段；恢复流程对「WinFsp 未装」「网络不通」的分支。

### E1–E5. 联动、设置与诊断（**已收尾**，2026-10-06）

| # | 内容 | 关键改动 |
| --- | --- | --- |
| E1 | 从端点创建同步任务 | ⚠️ **入口已随批次 G 下线**（主窗口的「新建同步」承担同一件事），规则层 `CloudSyncTask.OfferFor` 保留：拼云端那一侧（默认填「浏览器所在目录」，不是端点根）；原先的保存走 `ProfileStore` 的读—加—写，不覆盖 |
| E2 | 一键双向同步向导 | ⚠️ **入口已随批次 G 下线**，`CloudSyncWizardWindow` + `CloudSyncTask.WizardOfferFor` 保留（少一格、云端那侧不是端点、两格相同、两端都是远端全部在存盘**前**拒绝） |
| E3 | 诊断导出 | ✅ `CloudDiagnosticsReport.Compose`（纯函数），`CloudDiagnosticsReport.RedactProxy` 只脱敏代理的 userinfo；**永远不导出 `rclone.conf`**；端点段只写名字/类型/Provider/前缀（值一律留空，承接 A7 的导出形状） |
| E4 | i18n | ✅ `Tr.F` / `{l:Lang}` 绑定，资源文件 `Strings.resx` + `Strings.en.resx` 由 `localization/*.tsv` 经 `scripts/New-StringsResx.py` 生成；`AppLanguageContext` 进程级单例，**设置页可在 zh-CN 与 English 之间切换**；中文为中性集合（缺英文时回退到一直存在的措辞） |
| E5 | 首启引导 | ✅ `CloudFirstRunGuide.ShouldShow` 仅在「`CloudGuideSeen == false` **且**端点数为 0」时返回真（已建过端点的人不会被吵）；`CloudFirstRunPrompt` 的「不再提示」与「稍后再说」是两种结果，前者写 `CloudGuideSeen`，后者什么都不写 |

**E 组的接缝**：`AskDiagnosticsFile` / `ShowFirstRunGuide` / `MaybeShowFirstRunGuideAsync` / `AskLocalFolder` ——
任何一个没装都走真窗，装了却答 `null` 一律是用户取消（**不是**「没装接缝」，`??` 兜到真弹窗会在测试里挂一个无人可答的模态框）。
`EditSyncProfile` / `ShowSyncWizard` / `AskEndpoint` 三个已随批次 G 的「同步」行一起删除。

**Deferred migration debt —— ✅ 已清零（批次 F，2026-10-06）**：`LanguageCoverageTests.DeferredMigrationFiles` 这份例外清单**已删除**，`MainWindow.xaml.cs` 与 `Views/RemoteEndpointManagerWindow.xaml.cs` 里 392 处中文字面量已迁进资源表。现在 shell 里**任何**用户可见的中文硬编码都会被门禁报出来，没有豁免名单。做法与两个坑见 §2 的「批次 F」。

### 明确降级 / 不做（避免假装完成）

| 项 | 结论 |
| --- | --- |
| E3 按需文件（Files On-Demand） | **不做**。真正的按需文件需要文件系统驱动 + 占位符语义，`rclone mount` 无法表达。替代：提供「不缓存、直读」挂载模式（`vfsOpt.cacheMode = "off"`），在 UI 说明代价（每次读都走网络） |
| B11 版本历史（B21） | **降级**：先实证 rclone 是否暴露版本列表；不可得则只给网页端入口并说明原因 |
| B7 缩略图（B16） | **限范围**：仅图片、按需下载到缓存；不做视频缩略图 |

### 已选定的设计决策（可推翻，推翻请说明）

1. 跨端点复制**进传输队列**（而非轻量直连路径）—— 跨端点复制真实走网络字节流，可能很久，用户需要进度/取消/重试/历史；同端点复制也以队列任务呈现，保证「所有云盘操作都能在传输窗看到」的一致性。
2. D2 恢复**默认只提示 + 一键恢复**，`AutoMount` 默认关 —— 自动挂载拖慢启动、WinFsp 可能未装、网络可能不通，静默失败更难排查。
3. A6 首批 Provider 选 **OneDrive / Dropbox / WebDAV / Box** —— 覆盖主流；S3 兼容的 COS/OSS/B2 复用现有 S3 路径，只加 descriptor 行。
4. A3 重命名遇**活跃挂载则阻断**，提示先卸载 —— 挂载的 fs 串是 `old:`，改名后挂载点静默失效，自动重挂风险高于收益。
5. **先做 Provider descriptor 重构再加 Provider** —— 12+ 处硬编码使出错面随 Provider 数量线性增长。
6. `MountTarget` record **不改**，新增 `MountRequest` 包裹 —— 避免波及 4 个既有测试文件。

### 风险与验证清单

| 风险 | 怎么验证它真的对 |
| --- | --- |
| `SettingsWindow` 的 AutomationId switch 漏注册 → 静默不保存 | 每个新设置项都跑 UI 场景：改值 → OK → 重开断言已持久化；对照五处清单（见 §7） |
| 新设置字段漏加 `ConfigurationValidator` 规则 → 不被夹取 | 加规则 + 单测：越界值应被拒绝 |
| 新增 `<Button>` 撞样式白名单 | 跑 `UiConsistencyGuardrailTests`；需要新样式就先加进 `DesignTokens.xaml` 并同步白名单 |
| **挂载选项键名拼错被 rclone 静默忽略** | 唯一手段：断言发往 `mount/mount` 的 JSON 键名；关键项（`readOnly`/`diskSpaceTotalSize`）加「负值应报错」探针 |
| 服务端复制无 staging，失败留半成品 | 实证 `operations/copyfile` 失败后目标不存在；用假 RC 断言失败时无目标对象 |
| 端点重命名迁移中途失败 | 单测覆盖每步注入失败，断言 conf 与两个 JSON 回到原状；S3 场景断言 `<old>_s3` 不残留 |
| 跨端点 move 的 delete 失败 → 重复文件 | 单测模拟 delete 抛错，断言提示「已复制但源未删除」而不是报成功 |
| 键盘快捷键劫持地址栏/搜索框输入 | UI 场景：在 `AddressBox` 里按 Del/F2，断言不触发删除/重命名 |

---

## 6. 实现前必须先实证的点（不准猜，先验证再写）

1. `use_trash=false` 的 alias 方案在 v1.75 的 RC 路径下是否可用（**B5 永久删除**）。
2. ✅ **已实证（A5，2026-10-03）**：RC **没有**全局 proxy 透传；rclone 的 HTTP 客户端**只读 `HTTP_PROXY`/`HTTPS_PROXY`，
   **不读 `ALL_PROXY`**（死端口法验证：`ALL_PROXY` 那轮仍到达目标，`HTTP_PROXY`/`HTTPS_PROXY` 那轮 `proxyconnect tcp` 失败）。
   代理只能靠**子进程环境变量**传；`RcloneProxyResolver` 已把只有 `all_proxy` 的值镜像进 http/https。实现见 §2 的 A5。
3. `operations/publiclink` 对各 Provider 的支持矩阵与失败形态（**B4**）。
4. rclone `mountPoint` 是否接受 UNC 路径（**D6**）。
5. rclone 是否暴露版本列表（**B11**）。
6. `core/bwlimit` 的作用域是全局还是 per-fs（**C1**）。

实证的方法照 A2 的做法：直接 curl 打本机 `rclone rcd` 的 RC（记得带 `Content-Type: application/json`），
把结论写进 `AGENTS.md` 的不变式列表，而不是留在会话里。

---

## 7. 项目约定与陷阱（每个子批次都要遵守）

### 门禁与提交

- 每个子批次结束后跑 `pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline`
  （约 2m30s，**工具超时给 600000ms**）；必须看到 `Passed!  - Failed: 0` 汇总行。
- 提交粒度按层次（`feat` / `test` / `docs`），**用显式路径 `git add`**，每个子批次 1–2 个 commit，**只提交不 push**。
- `AGENTS.md` / `README.md` 随行为变化同步更新。

### UI 护栏（`UiConsistencyGuardrailTests`）

- 每个 `<Button>` 必须显式使用白名单样式：`Primary/Secondary/Tertiary/Danger/Icon/DangerIcon/EndpointSwap/ChangeSummaryFilter`。
- 页面 XAML（`src/FengSync/**/*.xaml`）**不得覆盖** `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush`；
  内联 hex 只能出现在 `Themes/DesignTokens.xaml`；页面禁用 `Button.Template`。
- 动态文本 `TextBlock` 用 `AutomationId`，**不要**静态 `AutomationProperties.Name`。

### 设置项三件套（漏一件就静默失效）

新增设置字段必须**同时**改：

1. `ApplicationSettings`（`src/FengSync.Core/Configuration/`）
2. `ConfigurationValidator`（否则值**不被夹取**）
3. `SettingsWindow` 的 **五处**：`SettingsWindow.xaml`、`EditablePages`、`NavigateTo`、
   `BuildSettings`、`ApplyTextValue` 的 AutomationId switch（漏注册 → **静默不保存**）。

### 其他铁律

- **破坏性操作**必须走 `CloudFileOperationSafety` 校验 + 二次确认，并保留批量阈值（10）。
- **不要在云盘路径上重写同步引擎**：上传/下载继续走 `CloudTransferQueue`（它复用同步引擎的 staging/resume/journal）；
  删除/移动/复制通过 `IEndpoint` / `RcloneRcClient` 调用，不自己拼 rclone 命令行。
- **改并发数要同时看续传**：`ActiveCount` 与 `MaxConcurrentTransfers` 在 `CloudTransferQueue.RunAsync` 里是
  **当作进度参数**传给 `EndpointTransfer.CopyToStagingAsync` 的，所以调并发会影响续传判定；
  并发数从 `const` 改为可配时还要保证 `SemaphoreSlim` 重建不破坏在途任务。
- **凭据只留在 `rclone.conf`**：任何导出/持久化都不得写入 secret/token。
- **`EndpointType` 枚举值只能追加**（`BaselineTransaction` 与 `TransferResumeStateStore` 持久化 `Type.ToString()`）。
- 新增 UI 场景要动**两处**：`tests/FengSync.UiTests/UiAcceptanceTests.cs` + `Scripts/Invoke-UiScenario.ps1`
  （`ValidateSet` 参数 + `switch` 分支）。
- 测试写法照抄既有模式：in-process 行为测试进 `tests/FengSync.UiTests`（照 `CloudFeatureBehaviorTests` 的
  `Add(queue, ...)` 直接操作集合，**避免启动真实 rclone**）；需要真 rclone 的进 `tests/FengSync.Tests`。

---

## 8. 关键文件索引

| 文件 | 作用 |
| --- | --- |
| `src/FengSync.Core/EndpointSchemes.cs` | **唯一的** scheme ↔ rclone backend ↔ `EndpointType` 映射表（含 `RenameRemote` / `RemoteNameOf` / `BuildUri`） |
| `src/FengSync/Services/CloudProviderCatalog.cs` | UI 侧 Provider 描述符（7 条）；加 Provider = 加一行 + 字段 |
| `src/FengSync.Core/RcloneConfigFile.cs` | 纯 ini 段手术（重命名端点的地基） |
| `src/FengSync/Services/CloudEndpointRename.cs` | 端点重命名：校验 → 备份 → 改写 → 回读验证 → 迁移 → 失败一起还原 |
| `src/FengSync/Services/CloudEndpointList.cs` | 端点列表的搜索与排序纯函数（批次 A3） |
| `src/FengSync/Services/CloudEndpointOrganization.cs` | 端点分组/标签的纯函数（批次 A6）；`Merge` / `Preserve` 是「保存归档绝不静默失败」的两处关键 |
| `src/FengSync/Services/CloudEndpointPortability.cs` | 端点导出/导入的纯逻辑与不可信输入校验（批次 A7）；导出结构里没有放凭据的字段 |
| `src/FengSync/Views/CloudEndpointOrganizationWindow.xaml(.cs)` | 编辑单个端点的分组/标签（批次 A6） |
| `src/FengSync.Core/Configuration/RcloneProxySettings.cs` | 把 `ApplicationSettings.RcloneProxy` 转成 `RcloneProxyOptions`（批次 A5） |
| `src/FengSync.Core/Rclone/Lifecycle/RcloneLifecycleHost.cs` | `ProxyOptionsProvider`：启动锁内、子进程创建前只读一次代理（批次 A5） |
| `src/FengSync/Services/CloudEndpointStatusService.cs` | 端点体检：`ProbeAsync` / `Classify` / `RunBoundedAsync` / `FormatBytes` |
| `src/FengSync/Services/CloudEndpointService.cs` | 端点 CRUD 与 URI 构造的门面（转发到 catalog + schemes） |
| `src/FengSync/Views/RemoteEndpointManagerWindow.xaml(.cs)` | **三合一**窗口：端点管理 + 挂载管理 + 文件浏览器 |
| `src/FengSync/Views/CloudEndpointEditorWindow.xaml(.cs)` | 新建/编辑端点表单（按 descriptor 生成，非 S3 部分无手写面板） |
| `src/FengSync/Services/CloudTransferQueue.cs` | 云盘传输队列（staging / resume / journal / 并发 / 重试） |
| `src/FengSync/Services/CloudFileManagerService.cs` | 文件浏览器服务层（列表/搜索/重命名/删除/移动/新建目录） |
| `src/FengSync.Core/Mount/MountOptions.cs` | 挂载参数（D 组的改造对象） |
| `src/FengSync.Core/Rclone/Transport/RcloneProxyConfiguration.cs` | 代理（A5 的现成机制） |
| `src/FengSync/Services/AppServices.cs` | `IAppServiceHost` / `AppServices.Install`（**两个 exe 共用服务宿主**） |
| `src/FengSync.DriveHub/` | 第二个入口 `DriveHub.exe`（独立 daemon） |
| `tests/FengSync.Tests/` | Core 层 + 需要真 rclone 的测试（门禁里的 Core 阶段） |
| `tests/FengSync.UiTests/` | in-process WPF 行为测试 + 桌面场景（`UiAcceptanceTests` + `Scripts/Invoke-UiScenario.ps1`） |
| `scripts/Test-All.ps1` | 门禁入口（`-Level Core|UiOffline|Online`，`-ShowOnScreen`） |
| `docs/handoff-cloud-drive-improvements.md` | **本文**：这条线唯一的权威文档（路线图 + 实证事实 + 约定） |
| `AGENTS.md` | 项目不变式清单（改行为前先读，改完补一条） |
