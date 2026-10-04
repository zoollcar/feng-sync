# 云盘管理改进 —— 交接文档

> **给接手这条线的人（或下一轮会话）。**
> 分支：`dev`（领先 `origin/dev` **40** 个已提交 —— 数字含本文最后一次更新，未 push）。工作区**干净**（仅剩未跟踪的 `.arts/`）。
> 最后更新：2026-10-04。
>
> 本文是这条线**唯一的权威文档**：路线图（§5）、需求盘点的判断依据、已实证的事实（§4）与项目约定（§7）都在这里。
> 原先那份独立的《云盘功能缺口盘点与演进计划》已把仍有价值的内容（剩余项的现状/缺口/参考做法、实现约束）
> 合并进本文后删除，需要它的原文可以去 git 历史里找（`git log --diff-filter=D -- docs/`）。

---

## 0. 30 秒速览

| | |
| --- | --- |
| **在做什么** | 把云盘功能缺口盘点里的 P1 剩余 17 项 + P2 全部 21 项 + 技术债 7 项（共 45 项）补齐，拆成 6 个子批次：**0 → A → B → C → D → E**。每个子批次独立跑门禁、独立提交（**只提交不 push**）。 |
| **已提交** | 批次 **0**（技术债 7 项）、**A0**（Provider descriptor 重构）、**A1**（OneDrive/Dropbox/Box/WebDAV + S3 provider 目录）、**A2**（端点重命名）、**A3+A4**（端点搜索/排序 + 测试全部）、**A5**（代理）、**A6**（分组/标签）、**A7**（端点导出/导入）、**B0**（队列泛化：远端→远端复制）、**B1**（浏览器复制/剪切/粘贴 + 跨端点复制）、**B2**（上传/粘贴冲突：`KeepBoth` 与逐文件询问）。**A 组全部收尾，B 组走到 B2。**见 §2。 |
| **下一步** | **B3–B11 文件浏览器**（§5）。B1 已经把「一条剪贴板行 → `EnqueueCopy` 的五个参数」这条路打通了，B2 又把「同名冲突怎么答」抽成一个**上传与粘贴共用**的纯规则（`CloudConflictResolver`）—— 样板就此是：**纯函数定规则 + 假 RC 驱动真窗口 + 需要弹窗的决定走接缝 + 需要落点才上真 rclone**。全量门禁 2026-10-04 再跑一次，`Failed: 0`（**Core 392 / UI 301**，UI 3m）。 |
| **最容易踩的坑** | 挂载选项键名拼错会被 rclone **静默忽略**；`config/create` 对 OAuth 后端会**重新要求浏览器授权**；rclone **不读 `ALL_PROXY`**；重建 WPF 列表会抛一次「无选中」事件（见 §3 的设计要点）；**进程里只能有一个 WPF `Application`，且只有创建它的线程看得见它**（见 §4 的 WPF 表 —— B1 的异步窗口测试就是在这里反复失败的）；**接缝返回 `null` 是「用户取消」，不是「没装接缝」**——用 `??` 兜到真弹窗会在测试里打开一个无人可答的对话框，并且 `Window.Owner` 指向一个从未 `Show()` 过的窗口时直接抛（B2 撞到过一次测试宿主崩溃）。 |

---

## 1. 接手先做的三件事

### ① 门禁照跑，基线已经自证过（2026-10-04）

```powershell
pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline
```

A3–A6 是分批提交的，所以接手时先自证过一次基线；B0 提交前后各跑了一次全量门禁，两次都是 `Failed: 0`
（**Core 390 / UI 264**，UI 阶段约 **3m08s**）。§0 说的 `UiFeatureBehaviorTests` 超时在本环境**没有复现**。
**B1 收尾时又跑了一次：`Failed: 0`，Core 392 / UI 281，UI 阶段 3m01s**（读数随批次上涨，别追死数字）。
**B2 收尾再跑一次：`Failed: 0`，Core 392 / UI 301，UI 阶段 3m** —— B2 只动了 UI 侧的规则与窗口，Core 计数没变。

- 约 **3m30s**（UI 阶段占大头）。调用工具时**超时必须给 600000ms**，否则会被掐断在半路。
- 判定标准：日志里出现 **`Passed!  - Failed: 0` 汇总行**。只看日志尾部会误判 —— 被超时掐断时日志正好停在
  `A total of 1 test files matched the specified pattern.` 之后，看起来像正常进行中。
  ⚠️ 后台跑时还会遇到第二次同样的误判：**Bash 后台任务会在 UI 阶段还在跑的时候就报 completed**（父 pwsh 退出、
  `dotnet test` 孙进程继续），日志尾部暂时只有 Core 那一行。以「UiTests 那行的 `Passed!` 出现」为准，别信任务通知。
- 日志**写 `$env:TEMP`**，不要写进 `.fengsync-test/`：那个目录会被 `Reset-TestArtifacts` 清空，
  文件被占用时脚本 3 秒就退出（`The process cannot access the file ... because it is being used by another process`）。
  另坑：在 bash 工具里执行 pwsh 时，重定向要写成 `> /c/Users/.../x.log 2>&1`；写 `*>` 会被 bash 先展开，
  结果 `Test-All.ps1` 收到 `Configuration=AGENTS.md` 并报「Cannot validate argument on parameter 'Configuration'」。
- 读数会比文档 §3 的旧预期更高（A5/A6/B0 各加了测试）；以 `Failed: 0` 为准，不追死具体数字。
- **单独跑一个测试类**（改窗口代码后的快速回路）时，别把 `dotnet test` 接进管道：`... | tail -20` 会让日志在进程
  结束前一个字都不落盘，看着像卡死。写成 `> "$TEMP/x.log" 2>&1` 再 `tail`。B1 用的过滤器
  `--filter "FullyQualifiedName~CloudBrowserPaste|FullyQualifiedName~CloudTransferWindowBehavior"`（19 条）**1 秒**跑完。

### ② A7、B0、B1、B2 已实现并提交 ✅

A7 按 `feat`/`test`/`docs` 三层提交（`60733ae` / `d58cd4a` / `344d3cd`）。A1 的桌面场景缺口在进 B 组前补上
（`fd12512`，见 §3 遗留缺口）。B0 队列泛化同样三层：`af6fc4c` feat / `85160a9` test / `6be98b9` docs
（本节的这条 commit 行与 §2 表末行是 `6be98b9` 之后回填的）。**B1 浏览器粘贴**照旧三层：`3c9322b` feat /
`cd74a61` + `526a634` test / `4656a58` docs（本节的 hash 由 `4656a58` 的下一次回填补上）。
**B2 同名冲突**照旧三层：`dedc2b5` feat / `1649dc6` test / docs 待回填。**A 组 + B0 + B1 + B2 至此收尾。**

### ③ 提交粒度（一直照这个走）

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
| `60733ae` | A7 | `feat(endpoints): export and import endpoints without their credentials` |
| `d58cd4a` | A7 | `test(endpoints): cover the export shape and the import file's validation` |
| `344d3cd` | A7 | `docs: record A7 done and archive A3-A6 in the handoff` |
| `fd12512` | A1 缺口 | `test(ui): walk every provider form in the endpoint editor`（7 张 descriptor 表单全走一遍，含「不得出现别家字段」） |
| `af6fc4c` | B0 | `feat(transfers): copy between two cloud endpoints through the queue` |
| `85160a9` | B0 | `test(transfers): cover the copy payload and where real rclone writes it` |
| `6be98b9` | B0 | `docs: record B0 done and the copy invariants that B1 must keep`（AGENTS.md 不变式 + 本文件） |
| `3c9322b` | B1 | `feat(browser): copy, cut and paste files between cloud endpoints` |
| `cd74a61` | B1 | `test(browser): cover the paste decisions and the calls they make` |
| `526a634` | B1 | `test(browser): cover the cut whose source delete was refused` |
| `4656a58` | B1 | `docs: record B1 done and the paste rules that B2 must keep`（AGENTS.md 不变式 + 本文件） |
| `dedc2b5` | B2 | `feat(browser): keep both files when a cloud name is taken` |
| `1649dc6` | B2 | `test(browser): cover each conflict answer and the renames they authorise` |

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

**顺带修掉一个门禁 flake**（`23a945f`）：`Invoke-UiScenario.ps1` 的 `Capture-Element` 在桌面无关模式下仍先做
「元素必须有可见矩形」的检查，再判断「截图跳过」；一个完成太快的本地同步（进度窗正在收起、矩形已归零）
会让整个套件失败（`local` 场景就这么挂过）。已把该检查移进真正的截图分支。

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

### A7 —— 端点导出/导入（**已提交**：`60733ae` + `d58cd4a` + `344d3cd`）

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

### B0 —— 队列泛化：远端→远端复制（**已提交**：`af6fc4c` + `85160a9` + `6be98b9`）

- `EndpointTransfer.CopyServerSideAsync(source, target, sourcePath, destinationPath, operationId, totalBytes,
  activeTransfers, progress, ensureDestinationAsync, ct)`（`src/FengSync.Core/Execution/EndpointTransfer.cs`）——
  两端都必须是 `RcloneEndpoint`，否则**在任何字节移动之前**抛 `NotSupportedException`；不 staging、不 publish，
  直接把 `destinationPath` 交给 `operations/copyfile`。本地端点被拒是**有意的**：直接写目标名会绕过
  Feng Sync 的临时文件 + 原子发布，一个取消的复制就会留下与完整文件无法区分的截断文件。
- `CloudTransferDirection` 追加 `Copy`（**枚举只能追加**：journal 存的是数字）。
- `CloudTransferTask` 加 `TargetUri` / `TargetPath`，`CloudTransferRecord` 加两个**可选尾字段**（照 `ErrorDetail` 的做法，
  旧 journal 文件照样能读）。⚠️ **与本文原计划的偏差**：B0 原来只写 `TargetUri`，实现时发现跨端点粘贴还需要一个
  **与源路径无关的目标路径**，所以 `TargetPath` 一起做掉了 —— 和 A2 那次一样，属于实现期修正，后续 B1 直接用它。
- `CloudTransferQueue` 加 `EnqueueCopy(源URI, 源路径, 目标URI, 目标路径, 大小)`、`HasCopyTasks`，
  `RunAsync` 的 `Copy` 分支：两端都 `EndpointFactory.CreateWithClient(uri, client)`，**跳过**
  `CopyToStagingAsync` / `PublishAsync` / `PrepareRetryAsync` / `DiscardStagingAsync`（finally 里的丢弃也带 `!isCopy` 守卫）。
  缺目标（只有手改的 journal 文件能造出这种行）在**建端点之前**就失败，不发任何 RC 请求。
- 构造参数加了 `Func<CancellationToken, Task<RcloneRcClient>>? client`（默认仍走 `AppServices.Current.RcloneHost`）——
  这是 `CloudFileManagerService` 早就有的接缝：`RcloneLifecycleHost` 是 sealed 且 `GetClientAsync` **会真起 rclone**，
  没有这个接缝就没法在 UiTests 里断言复制的载荷。
- 任务文案：`DirectionText`=复制、`CanContinueFromStaging`（只有 Download 为真）、`RestartText` 在 Paused 时
  对复制仍显示**重试**、取消文案「已取消，重新复制会从头开始」；`CloudTransferWindow` 的 hint 在 `HasCopyTasks` 时
  追加「云盘之间的复制…只能整个文件重新复制」—— 这是 §5 B0 那条「UI 必须明说」的落点。
- 测试：`tests/FengSync.Tests/ServerSideCopyTests.cs`（真 rclone，两个 alias remote）4 条 —— 目标名精确落位
  （含 `ensureDestinationAsync` 先建父目录、顺序可证）、复制不搬走源、**重试覆盖已存在目标**、失败/源不存在时
  目标目录为空、本地端点两端各拒一次；`tests/FengSync.UiTests/CloudTransferCopyTests.cs`（假 RC）8 条 ——
  `srcFs/srcRemote/dstFs/dstRemote` 与 `_group` 精确断言、`core/stats` 用的是同一个 group、不发
  `movefile`/`deletefile`/任何含 `fengsync.partial` 的载荷、失败时 `Failed`+`ErrorDetail` 带后端原文且不 materialise
  目标、取消时 `job/stop` 真的发出去、journal 往返带回两个端点、旧 journal 仍可读、行文案纯函数；
  `CloudTransferWindowBehaviorTests` +1 STA（有复制才提复制）。

### B1 —— 文件浏览器的复制 / 剪切 / 粘贴（**已提交**：`3c9322b` + `cd74a61` + `526a634` + `4656a58` docs）

- 纯规则在 `src/FengSync/Services/CloudBrowserClipboard.cs`：`CloudClipboardMode`（Copy / Cut —— **两者的全部差别就是粘贴后删不删源**）、
  `CloudClipboardItem(Source, Entry)`（**行必须带着它来自的端点**：用户可以跨到别的端点再粘贴，脱离了 remote 的 listing 路径没有意义）、
  `CloudPasteTarget`、`CloudPastePlan.Targets` / `.Skipped` / `.Conflicts`、
  `CloudPastePlanner.Build(items, destination, destinationDirectory, alreadyThere)`。目标目录的**现有清单是参数**而不是在这里现取 ——
  冲突必须在**排队之前**发现，同时让规则保持纯函数。
- 三条规则的落点：① **文件夹永不粘贴**（队列的单位是一个对象，浏览器的答案一直是「先进入文件夹再选文件」），而且把
  「N 个文件夹没有被复制」**合并成一行**而不是每行重复一次；② **同端点同路径 = 自复制**直接 skip —— rclone 会报成功，
  用户会盯着一趟什么也不会改变的传输；③ 同名判定用 `OrdinalIgnoreCase`，且**目录标记也算占名**
  （没有真目录的 backend 上，文件夹标记占掉的正是粘贴要写的那个 key）。
- **两侧各自剥自己的根，只剥一次**：`CloudFileManagerService.RelativeToEndpoint(account, remotePath)`（由窗口里的私有
  `ToEndpointRelative` 提升为公共服务，窗口 6 处调用点一并改）。`CloudPasteTarget` 因此同时带 `DestinationPath`
  （相对 remote 根，`CloudFileManagerService` 用它）和 `SourceRelativePath` / `DestinationRelativePath`
  （相对端点根，队列与 `EndpointSchemes.BuildUri` 用它）。alias 型 S3 端点的 `RootPath` 是空的，所以没得剥 ——
  这条由 `A_paste_strips_each_endpoints_own_root_and_only_once` 钉住。
- 窗口侧（`RemoteEndpointManagerWindow`）：右键菜单加 **复制 / 剪切**（作用于整个多选），工具条加 `PasteButton`
  （`AutomationId="CloudPaste"`，放在 删除 与 下载 之间）。内部接缝 `SetClipboard` / `PlanPasteAsync` /
  `ExecutePasteAsync` / `ClipboardItems` / `ClipboardMode` / `ShowListing`，构造函数新增
  `internal RemoteEndpointManagerWindow(CloudFileManagerService?, CloudTransferQueue?)` —— 两个 shell 仍走无参形式。
  ⚠️ `PlanPasteAsync` 与 `ExecutePasteAsync` 的形状在 **B2** 改了：前者返回 `CloudPasteOffer(Plan, TakenNames)`，
  后者的 `overwrite: bool` 换成 `placements`（见 §2 的 B2）。
- 执行分工：**复制一律进队列**（`EnqueueCopy`，因此有进度 / 取消 / 重试 / 历史，同端点内的复制也一样）；
  **同端点的剪切走 `operations/movefile`**（`CloudFileManagerService.MoveAsync`，字节不动，和 重命名 是同一个调用），
  所以它既不留重复也不需要二次确认。
- 删除只在**复制真的完成之后**：`DeleteSourceWhenCopied` 等 `_transfers.WaitForAsync(task)`；返回 false（失败/取消）就只报
  「源文件仍然保留」，删除本身抛错就报「已复制但源未删除」而**不报成功**（用户此刻有两个文件，报成功会把重复藏起来）。
  粘贴前的确认用 `CloudDeleteSafety.Describe` + `SupportsTrash`（**能不能撤销问的是源端点**，不是目标端点），
  批量阈值 10 的第二次确认照旧保留。
- ⚠️ **本批抓出的两条生产 bug**（都是测试先红、再改代码）：
  1. **纯复制也会排队删源** —— 第一版无条件调 `DeleteSourceWhenCopied`，把用户以为可加的粘贴变成数据丢失。
     现在 `if (cutting)` 才安排删除，测试用「复制之后 `Assert.Empty(deleted)`、不发 `movefile`/`deletefile`」钉住。
  2. **复制结果被刷新吃掉** —— `BrowseAsync` 会自己占用状态行（「正在读取目录…」）并且**不还原**，
     所以先写结果再刷新时，界面最后停在一个早已结束却没转完的 spinner。现在刷新在前、写状态在后，同一次 dispatcher 操作里完成。
- 测试：`tests/FengSync.UiTests/CloudBrowserPasteTests.cs`（B1 落 14 条，B2 之后该文件 19 条，**真窗口 + 假 RC**）—— 空剪贴板（按钮 off + 「剪贴板是空的」）、
  复制记住来源端点、剪切的预告（「已剪切 2 项」「文件夹不会被粘贴」「无法恢复」）、文件夹永不粘贴、跨端点两侧路径各自成立、
  自复制 skip、冲突计数、两行 = 两条 `operations/copyfile` 且 `srcFs/dstFs` 是**两个不同**端点、冲突行拿到 Skip 时只排非冲突行（B1 时这是 `overwrite: bool`，B2 换成 `placements`）、
  同端点剪切只发一条 `movefile` 并清空剪贴板、跨端点剪切**复制完成后**才删源并写成「已剪切到 files」、拒绝复制时 `Failed` +
  「源文件仍然保留」且删除记录为空、**拒绝删除时报告「已复制但源未删除」并带上原因**（而不是报成功）、
  离开来源端点后粘贴仍落在目标端点的目录里。
  `CloudFeatureBehaviorTests` +3 STA 纯规则（大小写/斜杠不同写法仍算同一个文件、skip 原因合并成一行、两侧各自剥根一次）；
  `ServerSideCopyTests` +2 真 rclone 实证（结论见 §4）；`CloudPaste` 的 `AutomationId` 在窗口测试里直接断言。
- ⚠️ 断言状态行时**别指望后端原文**：窗口那句经过 `RcloneUiError.Describe(ex, "cloud-delete")`，403 会被映射成
  「云端拒绝了此操作。检查目标目录权限和远程端点授权…」。后端原文只在队列层可断言
  （`CloudTransferTask.ErrorDetail`，`CloudTransferCopyTests` 就是这么钉的）。
- **桌面场景没动**：`Invoke-UiScenario.ps1` 里没有枚举云盘工具条的断言，所以新增工具条按钮不需要改场景脚本。
  日后若要断言「工具条上有哪个按钮」，改动点是 `UiAcceptanceTests.cs` + `Invoke-UiScenario.ps1` 两处（见 §7）。

---

### B2 —— 上传与粘贴的同名冲突：`KeepBoth` 与逐文件询问（**已提交**：`dedc2b5` feat / `1649dc6` test / docs 本行待回填）

- 规则全在 `src/FengSync/Services/CloudConflictResolution.cs`：`CloudConflictChoice {Overwrite, KeepBoth, Skip}`、
  `CloudConflictAnswer(Choice, ApplyToAll)`、`CloudConflictPlacement(Original, Destination, Choice)`、
  `CloudConflictResolver.ChoiceFor / AskFor / Apply`。**上传和粘贴从此共用同一套答案** —— 以前上传只有一次性
  YesNoCancel 弹窗（「全部覆盖 / 全部跳过 / 取消」），粘贴连问都没有。
- 设置项 `CloudUploadConflictPolicy` **追加** `KeepBoth`（append-only 铁律：`Ask=0 / Overwrite=1 / Skip=2 / KeepBoth=3`，
  设置 JSON 用默认 `JsonSerializer`，枚举按数字落盘）。`SettingsWindow` 下拉加第 4 项「保留两者（重命名）」，
  `ConflictIndex` / `ConflictPolicy` 两边同步；越界仍回落 `Ask`（`An_unrecognised_conflict_selection_falls_back_to_asking` 没动）。
- **`Ask` 必须映射成 `null`**：一个写着「每次询问」的设置不能被实现成默认答案，`ChoiceFor` 返回 null 就是「去问人」。
- **`Apply` 按输入顺序每条目的地给一条 placement**（skip 也占一条并写明是 skip）。理由不是美学：上传的行是**按位置**
  和答案配对的，而两个被拖进来的文件夹可以有**同名文件**，这一对指向**同一个远端路径** —— 按名字查表会把两条都指向
  同一个改名结果，第二份覆盖第一份，正是 `KeepBoth` 要避免的那种丢失。
  `Two_files_dropped_onto_one_remote_name_are_renamed_apart_rather_than_onto_each_other` 钉住它。
- **改名只相对自己落进的目录判断空闲**：`Apply` 收 `Func<string, ICollection<string>> takenOf`（目录 → 名单），
  每产生一个改名就把名字**加回**该集合。`CloudFileNames.KeepBothName`：`x.pdf → x (1).pdf`、
  `archive.tar.gz → archive.tar (1).gz`（**扩展名不动**）、大小写不敏感比对、**截到 255 字符**让 backend 收得下这个名字、
  最多试 9999 次后交出最后一个候选（不再无限数下去）。
- 弹窗 `src/FengSync/Views/CloudConflictPrompt.cs`（代码搭的，和 `TextPrompt` 一路，样式经 `TextPrompt.Resource<T>` 取）：
  四个选择 **取消 / 跳过 / 保留两者（存为“…”）/ 覆盖**，其中 **跳过是默认按钮**（回车不得替换文件），
  「把这次选择应用到其余 N 个同名文件」只在 `remaining > 1` 时可选。提问是**整批一次** `AskFor` 而不是每目录一次 ——
  否则拖一棵跨目录的树会每个目录再问一遍，而「应用到全部」的承诺覆盖的是整个 drop。
- 窗口接缝：`PlanPasteAsync()` 现在返回 `CloudPasteOffer(Plan, TakenNames)`（**同一次目录读取**既用来判冲突、又用来给
  改名当名单，不再 `ListAsync` 第二次）；`ExecutePasteAsync(plan, placements)` 取代 `overwrite: bool`；`AskConflict`
  是逐文件提问的接缝，未安装时才走真弹窗。粘贴用 `Indexed(placements)` 按目的地查表（粘贴的目的地唯一），
  上传用位置配对（见上）。
- ⚠️ **本批抓出的两条生产/测试坑**：
  1. **接缝的 `null` = 用户取消整批**，不能被 `??` 读成「没装接缝」：那样会打开真弹窗，而 `async void Paste_Click`
     里的「Cannot set Owner property to a Window that has not been shown previously」把**测试宿主进程直接打崩**
     （是 abort 不是 fail，整条 UI 套件停在半路，日志只剩 6 条读数）。
  2. **改名必须同时改写队列用的端点相对路径**：只改完整路径的话 `dstRemote` 会回到用户想避开的那个名字。
     现在改名落在 `CloudFileManagerService.Parent(relative)` 之下，`A_keep_both_rename_lands_in_the_directory_on_screen…`
     断言的是 `files:` 的 `dstRemote = shared/incoming/sub/a (1).txt`（队列把端点根再拼回去，所以断言里带 `shared/`）。
- 状态行：粘贴 `{N} 个同名文件改名为保留两者`；上传 `已加入传输队列：N 个文件。其中 M 个同名文件改名为保留两者。`。
  取消上传报「已取消上传。」、取消粘贴报「已取消粘贴。」，全跳过分别报「已跳过全部同名文件。」/「已跳过 N 个同名项目。」。
- 测试：`CloudFeatureBehaviorTests` 纯规则 **12 条**（`ChoiceFor` 三条 policy + `Ask` 留空；`AskFor` 的应用到全部、
  逐条问与剩余计数、取消≠全跳过；`Apply` 的 skip 占位、只换末段、同名两条分开改名、目录各自空闲；
  `KeepBothName` 的编号、扩展名、大小写、255 截断、9999 收敛）+ 设置项往返加 `KeepBoth → 3`；
  `CloudBrowserPasteTests` 真窗口 **5 条**（`KeepBoth` 落在改名后的远端名、改名仍停在屏上目录里、问题看到被占的名字与
  未决条数、应用到全部只问一次且两条各自改名、取消则一条 `copyfile` 都不发且剪贴板还在）。
  ⚠️ 这几条要走 **`PasteButton.RaiseEvent`**（只有真按钮路径才经过 `PlaceDestinations`），需要两个冲突名时给假 RC 传
  `ListedNames`（默认只有 `a.txt`）。
- 旧的 `CloudDeleteSafety.OverwriteFor`（`bool?` 三态）**删掉**，连同它的测试：那是「一次问完全批」时代的形状，
  留着就有第二个答案来源。

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

- ~~**A1 未覆盖的新 Provider 编辑表单**~~ —— **已补（2026-10-04，`fd12512`）**。`sftp-ui` 桌面场景现在把
  `CloudProviderDescriptor` 生成的 7 张表单全部走一遍：选服务 → 进连接步骤 → 断言该 descriptor 声明的每个
  AutomationId 都是**有尺寸的**控件、名称框与根目录框在窗口内可达，并断言**别家 Provider 的字段没出现在这张表单上**
  （编辑器按当前输入写 rclone 参数，残留字段会把另一个后端的键写进这个 remote）。
  S3 走自己的 bespoke 面板，放在最前面（它要先加载 provider 元数据）。新增 Provider 时**必须**在这张表里加一行，
  场景会自动要求它的字段可寻址 —— 这就是「加 Provider 只是加一行」那条约定的现成反例。

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
| **同一个 fs 两端**调 `operations/copyfile`：目标名落在源旁边、**源原样保留**（字节 hash 相同） | B1 的依据：同端点内的复制不需要第二套机制，也不会把 复制 变成 剪切（`ServerSideCopyTests`，真 rclone + alias remote） |
| **同一个 fs 两端**调 `operations/movefile`：目标处出现、**源目录里不再有这个对象** | B1 的依据：同端点的剪切用 move 而不是「复制 + 删源」——字节不动，也就不需要删除确认；`RcloneRcClient.MoveFileAsync` 走 `CallAsync`（载荷里**没有** `_async`，也不返回 jobid），所以它是同步等回复的那类调用，和 `copyfile` 的 `_async` 腿不同。`RemoteEndpointManagerWindow` 的 重命名 早就在用同一条腿 |
| `operations/copyfile` **原生支持跨 fs**；`drive` backend 默认 `use_trash=true` | B8/B9 的地基 |
| **`operations/copyfile` 中途只有 rclone 自己的 `<name>.<id>.partial`**，目标名要等 job 成功才出现；`job/stop` 后目标目录**为空**（日志 `Removing partially written file on error`）；对**已存在**的目标名是直接**覆盖** | B0 的依据：跨端点复制可以不过 Feng Sync 的 staging，失败/取消不会留下半成品，重试前不需要清理。**残余风险**：daemon 被硬杀时 rclone 的 `.partial` 会留在目标目录，Feng Sync 叫不出它的名字，也就清不掉 |
| `operations/copyfile` 走 `_async`+`_group` 时，进度只能从 `core/stats` 读；RC **没有** offset 写，所以远端→远端**不可能续传** | 复制行的按钮只能是「重试」，传输窗必须明说会从头再来 |
| rclone 的 s3 backend 自带 **53 个 provider**（含 `TencentCOS`/`Alibaba`/`HuaweiOBS`/`Qiniu`/`NetEase`），**没有 Backblaze** | 「加国产 S3 兼容」是零代码路径；B2 走 s3 的通用 `Other` provider |
| OAuth 状态机对所有 OAuth backend **完全相同** | Drive/OneDrive/Dropbox/Box 共用一套流程 |
| `rclone mount` 的 fs 串是 `<name>:`，在**挂载时**解析 | 改名时若有活跃挂载 → 直接阻断（A2 已实现） |
| rclone 的 HTTP 客户端**只读 `HTTP_PROXY`/`HTTPS_PROXY`**，**不读 `ALL_PROXY`**；RC 无全局 proxy 透传 | 代理只能靠子进程环境变量传；`RcloneProxyResolver` 把 only-`all_proxy` 镜像进 http/https（A5） |
| 手工 curl 探测 daemon：启动参数是 `--rc-addr/--rc-user/--rc-pass`（**不是** `--rcaddr`），就绪探测调 `core/version`（**没有** `rc/version` 这个方法） | 下一次做 §6 的实证不必再撞一遍 |

### WPF / 测试

| 事实 | 影响 |
| --- | --- |
| `UiConsistencyGuardrailTests.PageLocalButtonVisual` 的正则**只对 `<Button>` 元素的属性区**生效 | 给 `TextBox` 设 `Padding` 是安全的；`<Button>` 上一律不能写 `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush` |
| `LiveTextAccessibilityTests` **只扫 `TextBlock`** | `TextBox`/`ComboBox`/`Button` 上的静态 `AutomationProperties.Name` 是允许的；动态文本 `TextBlock` 必须用 `AutomationId` |
| 无桌面 WPF 测试模板 = `RunSta(Action)` + `EnsureApplicationResources()` + **从不 `Show()`** | 毫秒级，不需要前台会话 |
| UI 测试**默认桌面无关**：窗口停 `-32000`，输入走 UIA pattern / posted 消息；`-ShowOnScreen` 恢复前台 | 不得引入合成鼠标移动（`DesktopFreeUiHarnessTests` 会拦） |
| `RcloneLifecycleHost.GetClientAsync` **会真的起 rclone 进程**，而 `IAppServiceHost.RcloneHost` 是 **sealed 具体类**（无法替换） | 「不碰 rclone」的测试必须靠**不触发 `BrowseAsync`**，并用「`StatusText` 没被写过」来证明（`AssertNoDirectoryListing` 就是这个）。B0 给 `CloudTransferQueue` 也加了同款**可选 RC 客户端工厂**构造参数（`CloudFileManagerService` 早就有），于是「队列发了什么载荷」能在假 RC 上断言（`CloudTransferCopyTests`）；没有接缝的那条腿照旧靠不起 daemon 的写法 |
| 桌面场景里 `AutomationProperties.Name` 会**覆盖** `Content` 派生的名字 | 别想用 UIA 的 `Name` 去断言一个「标签会变」的按钮；改断言它的 `SelectionPattern` / 布局 |
| WPF 跨进程 UIA 客户端拿不到 `TextBlock` 的 `TextPattern` | 断言文本要靠 `Name`（因此动态文本 `TextBlock` 必须**去掉**静态 `Name`） |
| **一个进程/AppDomain 只能有一个 `System.Windows.Application`**，而 `Application.Current` **只有创建它的那个线程看得见**；一个还活着（或正在关闭）的实例会让别的线程 `new Application()` 直接抛「Cannot create more than one…」/「The Application object is being shut down」 | B1 的窗口测试每条跑在自己的 STA 线程上，**不能**各自重建 Application。规则：**只在 `Application.Current is null` 时构造并配置它，并记住自己是否拥有它**；拥有它的那个线程负责在结束时 `Shutdown()` + `dispatcher.InvokeShutdown()` 把它释放掉，否则下一个建窗口的测试类会死在 `new Application` 上而不是自己的断言上（`CloudBrowserPasteTests` 的 `MergeApplicationThemes()` 返回 bool 就是为了这个）。`IClassFixture` 共享 Application 的写法**试过且不行**：`Dispose` 里无论 `BeginInvokeShutdown` 还是 `BeginInvoke(Shutdown)` 都 release 不掉，因为 WPF 只在其 dispatcher 关闭时销毁 Application |
| `ShutdownMode` 默认 `OnLastWindowClose`：测试里第一次 `window.Close()` 会**连带结束整个 Application 和正在 pumping 的 dispatcher**，粘贴剩下的续传就永远不会跑；在一个**不属于本线程**的 Application 上设 `ShutdownMode` 会 `VerifyAccess` | 自己建的 Application 一律设 `OnExplicitShutdown`；别人的**只读不写**。窗口可能已被测试自己关掉，收尾 `Close()` 要容许 `InvalidOperationException` |
| `Dispatcher.InvokeAsync` 的续传只在**它自己的线程 pumping 时**才执行；线程停在 `Thread.Join` 里 = 续传永不跑（窗口那句状态行就是续传） | 异步窗口行为必须在 STA 线程里 `Dispatcher.PushFrame(frame)` pumping，任务完成时把 `frame.Continue = false`。**并且要有 watchdog**：`System.Threading.Timer` 到点 `dispatcher.BeginInvoke(() => frame.Continue = false)`，否则一条卡住的粘贴会挂住整个 UI 套件（B1 撞到过一次 >10 分钟无输出的后台跑） |
| **弹窗（`ShowDialog`）在进程内测试里没人能答**，而给 `Window.Owner` 赋一个**从未 `Show()` 过**的窗口会抛 `InvalidOperationException`；这异常在 `async void` 事件处理器里是**未处理异常 → 测试宿主进程崩溃**（`Test host process crashed`，整条 UI 套件被 abort，不是某条测试 fail） | 凡是要问用户的决定都走**可注入接缝**（B2 的 `AskConflict`），弹窗只做接缝外面那层壳；接缝返回 `null` 表示**用户取消**，所以判断「有没有接缝」要用 `AskConflict is { } ask`，**不能用 `??` 兜到真弹窗**（B2 就这么崩过一次测试宿主） |

---

## 5. 剩余路线图（B1 → E）

> 这一节同时是那份 307 行改进计划的替代。**A0–A7、B0、B1 与 B2 已完成**（见 §2），本节的 A5/A6/A7/B0/B1 只留设计与规则的留档。
> **剩余项的顺序与依赖**：B 与 C 共享「队列泛化」，所以 **B0 必须在 B 的跨端点复制之前落**（已落）；
> D 与 A/B/C 完全解耦；E 是收尾集成。**下一批是 B3–B11（B1、B2 已落，见 §2）。**

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

### B0. 队列泛化（**前置**，与批次 C 共用）—— ✅ 已完成，见 §2

代码与测试清单在 §2 的 B0。下面是 **B1 接手时必须知道的三条规则**：

- **复制进队列，不进轻量直连**（§5「已选定的设计决策」第 1 条）：跨端点复制真的走网络字节流，可能很久，
  用户需要进度 / 取消 / 重试 / 历史。B1 只需组装 `EnqueueCopy(源URI, 源路径, 目标URI, 目标路径, 大小)`。
- ⚠️ **远端 → 远端无法 append，会退化为不可续传** —— 设计取舍不是 bug，UI 已经明说
  （`HasCopyTasks` → hint，`RestartText`/`CanContinueFromStaging` → 行内按钮）。**不要**为了「看起来能续传」
  去给它造 staging：rclone 没有 offset 写，造出来的 staging 既校验不了也接不上。
- **journal 的新字段只能是可选尾字段**（`TargetUri` / `TargetPath`），`CloudTransferDirection` 只能追加。
  复制的目标**永远不写进 `LocalPath`** —— 那是处处按本地路径解释的字段，写进去下一版会把它当下载去建目录。
- 要断言「复制到底发了什么」就注入假 RC 客户端（`new CloudTransferQueue(journal, clientFactory)`）；
  要断言「字节到底落在哪」才用 `tests/FengSync.Tests` 的真 rclone + alias remote。两种都在 §2 的 B0 里有现成样板。

### B1–B11. 文件浏览器（缺口最大的一组）

> 需求盘点原本列了 21 项（B1–B21）；其中 P0 的 7 项 —— 新建文件夹、重命名、删除、多选批量、搜索、排序、
> 面包屑 + 地址栏 —— **已在批次 1 落地**。下表是**剩余 11 项**，「现状 / 缺口」与「参考做法」两列是从原盘点文档
> 一并搬过来的，以免那份文档消失后丢掉判断依据。**B1、B2 已落地（见 §2）**，下表从 B3 起都是待做项。
> 给 B6 的一条现成接线：`SetClipboard(Copy/Cut, 多选行)` 和 `Paste_Click` 已经是内部方法，
> Ctrl+C / Ctrl+X / Ctrl+V 只需把键盘事件指到它们，**不要**另造一套剪贴板状态。
> 给 B3/B9 的一条现成接缝：`PlanPasteAsync()` 已经把目标目录的**一次读取**同时用于判冲突和取名，
> 属性/大小统计要读目录时照这个形状走，别再 `ListAsync` 第二次。

| 本计划 | 原编号 | 内容 | 现状 / 缺口 | 参考做法 |
| --- | --- | --- | --- | --- |
| B1 | B8–B9 | 复制 / 剪切 / 粘贴 + 跨端点复制 | ✅ **已完成**（`3c9322b` + `cd74a61` + `526a634`，见 §2）。跨端点复制进队列（`EnqueueCopy`），同端点的剪切走 `operations/movefile`，文件夹一律 skip 并说明原因，源删除只在它自己的复制完成之后才发生 | 网盘标配；Air Explorer 支持云到云 |
| B2 | B10 | 上传冲突细化 | ✅ **已完成**（`dedc2b5` + `1649dc6`，见 §2）。`KeepBoth`（`x.pdf → x (1).pdf`，相对目标目录的空闲名）与**逐文件询问 + 应用到全部**都齐了，上传与粘贴走同一个 `CloudConflictResolver`；`每次询问 / 直接覆盖 / 跳过同名文件 / 保留两者（重命名）` 四项进设置 | 网盘标配 |
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

### E1–E5. 联动、设置与诊断

| # | 内容 | 关键改动 |
| --- | --- | --- |
| E1 | 从端点创建同步任务（E1） | 云盘管理窗加入口，`ProfileEditorWindow`（public）预填该端点一侧后打开 |
| E2 | 一键双向同步向导（E2） | 向导生成 `SyncProfile`，模式默认双向；复用 `RemoteEndpointPickerWindow` |
| E3 | 诊断导出（F5） | 导出应用日志 + 设置（**脱敏**）+ 环境信息（WinFsp、rclone 版本）；**不导出 `rclone.conf`** |
| E4 | i18n（F6） | 全中文硬编码 → 先抽资源键（`Strings.resx` 或 XAML `x:Static`），**只做 zh-CN 一份，不做语言切换 UI** |
| E5 | 首启引导（F7） | 首次进入云盘管理时引导连接 Google Drive |

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
| ✅ **已核（B0）**：服务端复制无 staging，失败留半成品 | 实证过（见 §4：`operations/copyfile` 中途只有 rclone 自己的 `.partial`，`job/stop` 后目标目录为空，成功才有目标名）；`ServerSideCopyTests` 用真 rclone 钉住「失败 / 源不存在时目标为空」，`CloudTransferCopyTests` 用假 RC 钉住「失败时没有 publish、没有 deletefile、载荷里不出现 staging 名」。残余：daemon 被硬杀会留下 rclone 自己的 `.partial`，Feng Sync 清不掉 —— B1 的复制 UI 不要把「目标目录有个不认识的 .partial」当成本程序的 bug |
| 端点重命名迁移中途失败 | 单测覆盖每步注入失败，断言 conf 与两个 JSON 回到原状；S3 场景断言 `<old>_s3` 不残留 |
| 跨端点 move 的 delete 失败 → 重复文件 | ✅ **已覆盖（B1）**：`A_cut_whose_delete_was_refused_reports_the_duplicate_instead_of_a_success` 用假 RC 拒绝 `operations/deletefile`，断言状态行是「已复制但源未删除」+ 原因，而不是「已剪切」 |
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

7. ✅ **已实证（B0，2026-10-04）**：跨端点 `operations/copyfile` **不会**留下可续传的中途状态 —— 作业运行中目标只有
   rclone 自己的 `<name>.<id>.partial`，目标名要等作业成功才出现；`job/stop` 之后目标目录为空
   （rclone 日志 `Removing partially written file on error`）；对已存在的目标名是直接**覆盖**（重试前不需要清理）。
   结论已写进 `AGENTS.md` 的不变式列表，实现与测试见 §2 的 B0。**残余**：daemon 被硬杀时 rclone 的 `.partial` 会留下，
   Feng Sync 叫不出它的名字，也就清不掉。
   探测手法照 A2：起 `rclone rcd --rc-addr=127.0.0.1:<port> --rc-user=<u> --rc-pass=<p> --rc-serve`（**不是** `--rcaddr`），
   就绪探测调 `core/version`（**没有** `rc/version` 这个方法），curl 必须带 `Content-Type: application/json`。

8. ✅ **已实证（B1，2026-10-04，真 rclone + 两个 alias remote，`ServerSideCopyTests`）**：**同一个 fs 两端**的
   `operations/copyfile` 把目标名落在源的**旁边**并且**保留源**（hash 相同）；**同一个 fs 两端**的 `operations/movefile`
   让对象出现在目标处、**源目录里不再存在**。两条一起决定了 B1 的呈现：同端点内的 复制 不需要第二套机制，
   同端点的 剪切 用 move 而不是「复制 + 删源」，因此它不留重复、也不需要删除确认。结论已写进 `AGENTS.md` 的
   粘贴不变式和上面 §4 的 rclone 表。

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
- **代码里现搭的对话框**（`TextPrompt`、`CloudConflictPrompt`）写在 `.cs` 里，页面 XAML 的扫描看不到它们，
  所以那三条得作者自己守住，而这两处确实守着：样式一律经 `TextPrompt.Resource<T>` 从合并字典取
  （`Primary/Secondary/Tertiary/DangerButtonStyle`、`CaptionTextStyle`，内联 hex 为零）、可操作的控件
  （按钮、复选框、输入框）都有 `AutomationId`、**Enter 落在不改变任何东西的那颗按钮上**
  （冲突框的 `IsDefault` 是「跳过」，destructive 确认框的 `IsDefault` 是「取消」）。

### 设置项三件套（漏一件就静默失效）

新增设置字段必须**同时**改：

1. `ApplicationSettings`（`src/FengSync.Core/Configuration/`）
2. `ConfigurationValidator`（否则值**不被夹取**）
3. `SettingsWindow` 的 **五处**：`SettingsWindow.xaml`、`EditablePages`、`NavigateTo`、
   `BuildSettings`、`ApplyTextValue` 的 AutomationId switch（漏注册 → **静默不保存**）。

### 其他铁律

- **破坏性操作**必须走 `CloudFileOperationSafety` 校验 + 二次确认，并保留批量阈值（10）。
- **不要在云盘路径上重写同步引擎**：上传/下载继续走 `CloudTransferQueue`（它复用同步引擎的 staging/resume/journal）；
  **远端→远端复制也进同一个队列**（B0），走 `EndpointTransfer.CopyServerSideAsync`，不要在 UI 里直接拼
  `RcloneRcClient.CopyFileAsync`；删除/移动仍通过 `IEndpoint` / `RcloneRcClient` 调用，不自己拼 rclone 命令行。
  B1 照的就是这条：**粘贴的跨端点复制一律 `EnqueueCopy`**，唯一允许直接调 move 的是**同端点内的剪切**（字节不动，没有可续传/进度可言）。
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
| `src/FengSync.Core/Execution/EndpointTransfer.cs` | **唯一的** staging-aware 传输原语（同步引擎与云盘浏览器共用）。`CopyToStagingAsync`+`PublishAsync` 是可续传的上传/下载腿；`CopyServerSideAsync` 是两端都远端、**不 staging 不 publish** 的复制腿（B0） |
| `src/FengSync/Services/CloudTransferQueue.cs` | 云盘传输队列（staging / resume / journal / 并发 / 重试 / **远端→远端复制 `EnqueueCopy`**）；构造参数里的可选 RC 客户端工厂是「不起 daemon 也能断言载荷」的接缝 |
| `src/FengSync/Services/CloudFileManagerService.cs` | 文件浏览器服务层（列表/搜索/重命名/删除/**同端点移动 `MoveAsync`**/新建目录）；`RelativeToEndpoint` 是「剥的是**自己**端点的根、只剥一次」的唯一体（B1） |
| `src/FengSync/Services/CloudBrowserClipboard.cs` | 粘贴规则的纯函数（B1）：`CloudClipboardMode` / `CloudClipboardItem`（行 + 它的来源端点）/ `CloudPasteTarget` / `CloudPastePlan` / `CloudPasteOffer(Plan, TakenNames)`（B2：目标目录**读一次**同时供判冲突与改名）/ `CloudPastePlanner.Build`。不碰 WPF 也不碰 rclone，所以落点与 skip 原因能直接测 |
| `src/FengSync/Services/CloudConflictResolution.cs` | 同名冲突唯一的答案处（B2）：`CloudConflictChoice` / `CloudConflictAnswer(Choice, ApplyToAll)` / `CloudConflictPlacement(Original, Destination, Choice)` / `ChoiceFor`（设置 → 整批答案，`Ask` 给 null）/ `AskFor`（逐条问 + 应用到全部）/ `Apply`（**按输入顺序一条一答**，改名相对各自目录的空闲名） |
| `src/FengSync/Views/CloudConflictPrompt.cs` | 冲突问题本身（B2）：取消 / **跳过（默认按钮）** / 保留两者（预览名）/ 覆盖 + 「应用到其余 N 个」。测试用窗口的 `AskConflict` 接缝代替它 |
| `src/FengSync.Core/Mount/MountOptions.cs` | 挂载参数（D 组的改造对象） |
| `src/FengSync.Core/Rclone/Transport/RcloneProxyConfiguration.cs` | 代理（A5 的现成机制） |
| `src/FengSync/Services/AppServices.cs` | `IAppServiceHost` / `AppServices.Install`（**两个 exe 共用服务宿主**） |
| `src/FengSync.DriveHub/` | 第二个入口 `DriveHub.exe`（独立 daemon） |
| `tests/FengSync.Tests/ServerSideCopyTests.cs` | 真 rclone（两个 alias remote）钉住跨端点复制的**落点**：目标名精确、源不消失、重试覆盖、失败不留对象、本地端点被拒（B0） |
| `tests/FengSync.UiTests/CloudTransferCopyTests.cs` | 假 RC 钉住复制的**载荷**：`srcFs/srcRemote/dstFs/dstRemote`+`_group`、`core/stats` 进度、取消发 `job/stop`、不许出现 movefile/deletefile/staging 名、journal 往返与旧文件兼容（B0） |
| `tests/FengSync.UiTests/CloudBrowserPasteTests.cs` | **真窗口 + 假 RC**钉住 B1 的浏览器侧决定：剪贴板带着来源端点、两侧各自剥根、文件夹与自复制的 skip、同端点剪切只发 `movefile`、跨端点剪切**复制完成后**才删源（拒绝复制 → 源保留且无删除；拒绝删除 → 报告重复而不是成功）。B2 之后**冲突不再是断言 `overwrite=false`**，而是走 `AskConflict` 这个窗口接缝（**必须置**，否则测试会弹真模态框而挂住）钉住落在改名后的远端名、改名停在屏上目录、问题看到被占的名字与剩余数量、一次回答覆盖其余、取消不排队。假 RC 的 `ListedNames`（默认恰好一个同名文件）决定「目录里已有什么」，`PlanAsync` / `Place` 是把窗口接缝接上的小工具；`RunSta` 的 pumping + 单 `Application` 所有权 + watchdog 是这类**异步窗口测试**的样板（见 §4 的 WPF 表） |
| `tests/FengSync.Tests/` | Core 层 + 需要真 rclone 的测试（门禁里的 Core 阶段） |
| `tests/FengSync.UiTests/` | in-process WPF 行为测试 + 桌面场景（`UiAcceptanceTests` + `Scripts/Invoke-UiScenario.ps1`） |
| `scripts/Test-All.ps1` | 门禁入口（`-Level Core|UiOffline|Online`，`-ShowOnScreen`） |
| `docs/handoff-cloud-drive-improvements.md` | **本文**：这条线唯一的权威文档（路线图 + 实证事实 + 约定） |
| `AGENTS.md` | 项目不变式清单（改行为前先读，改完补一条） |
