# 云盘管理改进 —— 交接文档

> **给接手这条线的人（或下一轮会话）。**
> 分支：`dev`（领先 `origin/dev` **24** 个已提交，未 push）。工作区还有一批**未提交**的改动。
> 最后更新：2026-10-03。
>
> 本文是这条线**唯一的权威文档**：路线图（§5）、需求盘点的判断依据、已实证的事实（§4）与项目约定（§7）都在这里。
> 原先那份独立的《云盘功能缺口盘点与演进计划》已把仍有价值的内容（剩余项的现状/缺口/参考做法、实现约束）
> 合并进本文后删除，需要它的原文可以去 git 历史里找（`git log --diff-filter=D -- docs/`）。

---

## 0. 30 秒速览

| | |
| --- | --- |
| **在做什么** | 把云盘功能缺口盘点里的 P1 剩余 17 项 + P2 全部 21 项 + 技术债 7 项（共 45 项）补齐，拆成 6 个子批次：**0 → A → B → C → D → E**。每个子批次独立跑门禁、独立提交（**只提交不 push**）。 |
| **已提交** | 批次 **0**（技术债 7 项）、**A0**（Provider descriptor 重构）、**A1**（OneDrive/Dropbox/Box/WebDAV + S3 provider 目录）、**A2**（端点重命名）。见 §2。 |
| **已完成但未提交** | **A3 端点搜索/排序 + A4「测试全部」**。代码、测试、桌面场景都写完了，单测与 `sftp-ui` 场景**单跑通过**；但**全量门禁还没跑，也还没 commit**。见 §1 与 §3。 |
| **下一步** | ① 跑全量门禁 → ② 门禁绿后提交 A3/A4 → ③ 继续 **A5 代理**。 |
| **最容易踩的坑** | 挂载选项键名拼错会被 rclone **静默忽略**；`config/create` 对 OAuth 后端会**重新要求浏览器授权**；重建 WPF 列表会抛一次「无选中」事件（见 §3 的设计要点）。 |

---

## 1. 立刻要做的三件事

### ① 跑全量门禁

```powershell
pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline
```

- 约 **2m30s**。调用工具时**超时必须给 600000ms**，否则会被掐断在半路。
- 判定标准：日志里出现 **`Passed!  - Failed: 0` 汇总行**。只看日志尾部会误判 —— 被超时掐断时日志正好停在
  `A total of 1 test files matched the specified pattern.` 之后，看起来像正常进行中。
- 日志**写 `$env:TEMP`**，不要写进 `.fengsync-test/`：那个目录会被 `Reset-TestArtifacts` 清空，
  文件被占用时脚本 3 秒就退出（`The process cannot access the file ... because it is being used by another process`）。
- 预期读数：**Core 370（本批不变）/ UI 212（186 + 25 + 1）**。这两个数是**预期**，必须实跑确认。

### ② 提交 A3/A4（门禁绿之后）

用**显式路径** `git add`（**不要 `git add -A`**；`.workbuddy-ai/` 已在 `.gitignore` 里，但仍保持这个习惯）。
建议拆 2 个 commit：

```
feat(endpoints): search, order and check every endpoint from the list
test(endpoints): cover the endpoint list's search, order and batch probe
```

（本文档单独一个 `docs:` commit。）**只提交不 push。**

### ③ 继续 A5 代理

见 §5 的 A5。

---

## 2. 已完成并提交的工作

| commit | 批次 | 内容 |
| --- | --- | --- |
| `88c7a15` | 0 | `chore(tech-debt)` —— 技术债 7 项（24 文件） |
| `b1631c4` | A0+A1 | `feat(endpoints): provider catalog, and OneDrive/Dropbox/Box/WebDAV`（21 文件） |
| `e18462d` | A2 | `feat(endpoints): rename an endpoint by rewriting its rclone.conf section` |
| `324ff7e` | A2 | `test(endpoints): cover endpoint rename, its rollback and profile migration` |
| `23a945f` | A2 | `test(ui): rename through the real shell, and tolerate a collapsed progress window` |

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

---

## 3. 未提交的 A3 + A4

`git status --short` 共 8 项：

```
 M src/FengSync/Services/CloudEndpointStatusService.cs
 M src/FengSync/Views/RemoteEndpointManagerWindow.xaml
 M src/FengSync/Views/RemoteEndpointManagerWindow.xaml.cs
 M tests/FengSync.UiTests/Scripts/Invoke-UiScenario.ps1
 M tests/FengSync.UiTests/UiFeatureBehaviorTests.cs
?? src/FengSync/Services/CloudEndpointList.cs
?? tests/FengSync.UiTests/CloudEndpointCheckAllTests.cs
?? tests/FengSync.UiTests/CloudEndpointListTests.cs
```

| 文件 | 内容 |
| --- | --- |
| `src/FengSync/Services/CloudEndpointList.cs`（新，~65 行） | `CloudEndpointSortColumn { Name, Provider, Health }`；`Matches(account, query)` —— **每个**空白分词都要命中 `name + provider + type + prefix + Display`（所以第二个词是**收窄**而不是放宽）；`Arrange(rows, query, column, descending)` —— 名称**恒为最终 tie-break 且不反转**；`Rank(health)` —— 升序 = 待办清单：`NeedsLogin → Unreachable → Unknown → Checking → Connected` |
| `src/FengSync/Services/CloudEndpointStatusService.cs` | `ProbeConcurrency = 3` + `RunBoundedAsync<T>(items, concurrency, action, ct)`：`SemaphoreSlim` 限流、**单条失败被收集**而不是首错即抛、**取消不算失败且会停下**、`acquired` 标记防止释放未获取的槽位 |
| `src/FengSync/Views/RemoteEndpointManagerWindow.xaml` | 端点 GroupBox 从裸 `ListBox` 改成 2 行 Grid（`Auto` 工具条 + `*` 列表），工具条是 **`WrapPanel`**（左列只有 340px，必须能换行）：`CloudEndpointSearch` / `CloudEndpointSort` / `CloudEndpointSortDirection` / `CheckAllCloudEndpoints`，四个控件都用白名单样式 |
| `src/FengSync/Views/RemoteEndpointManagerWindow.xaml.cs` | `_loaded` / `_browsedEndpoint` / `_rearranging` / `EndpointSortOptions` / `ApplyEndpointView(selectName, browse)` / `FollowSelectionAsync` / `EndpointSearch_TextChanged` / `EndpointSort_Changed` / `EndpointSortDirection_Click` / `CheckAll_Click` / `DescribeCheckAll` / `LoadEndpointsForTest` |
| `tests/FengSync.UiTests/CloudEndpointListTests.cs`（新） | **13** 个纯函数测试（搜索/排序/tie-break/空列表） |
| `tests/FengSync.UiTests/CloudEndpointCheckAllTests.cs`（新） | **12** 个（`RunBoundedAsync` 6 + `DescribeCheckAll` 5 + 并发常量 1） |
| `tests/FengSync.UiTests/UiFeatureBehaviorTests.cs` | +1 个 STA 测试（驱动真实控件）+ 5 个辅助（`EndpointRow`/`VisibleNames`/`SelectedName`/`Click`/`AssertNoDirectoryListing`） |
| `tests/FengSync.UiTests/Scripts/Invoke-UiScenario.ps1` | `sftp-ui` 场景新增：4 个控件矩形断言、搜索命中/不命中（**不命中时文件面板也必须空**）、清空恢复浏览、排序选「名称」并回读、方向按钮后文件面板仍在、点「测试全部」断言行变「已连接」且状态行出现「检查结束」 |

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

### 本批已验证 / 未验证

已验证：

- `dotnet build src/FengSync` → 0 warning / 0 error；`dotnet build tests/FengSync.UiTests` → 0 warning / 0 error。
- `CloudEndpointListTests` + `CloudEndpointCheckAllTests` → **25 passed / 0 failed**。
- `UiFeatureBehaviorTests` → **22 passed / 0 failed**（整类 502ms，说明新增的那个 STA 测试没有起 rclone）。
- `sftp-ui` 桌面场景 → `Passed sftp-ui; completed in 00:00:16.5`。
  单跑命令：`pwsh -NoProfile -File tests/FengSync.UiTests/Scripts/Invoke-UiScenario.ps1 -Scenario sftp-ui -AppPath tests/FengSync.UiTests/bin/Debug/net10.0-windows/FengSync.exe -Workspace .`

未验证：**全量门禁**（见 §1）。另外 A1 遗留一项：`sftp-ui` 的 Provider 遍历目前只有
`Google Drive / S3 Bucket / SFTP`，**未覆盖新增的 OneDrive/Dropbox/Box/WebDAV 编辑表单**。

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

### WPF / 测试

| 事实 | 影响 |
| --- | --- |
| `UiConsistencyGuardrailTests.PageLocalButtonVisual` 的正则**只对 `<Button>` 元素的属性区**生效 | 给 `TextBox` 设 `Padding` 是安全的；`<Button>` 上一律不能写 `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush` |
| `LiveTextAccessibilityTests` **只扫 `TextBlock`** | `TextBox`/`ComboBox`/`Button` 上的静态 `AutomationProperties.Name` 是允许的；动态文本 `TextBlock` 必须用 `AutomationId` |
| 无桌面 WPF 测试模板 = `RunSta(Action)` + `EnsureApplicationResources()` + **从不 `Show()`** | 毫秒级，不需要前台会话 |
| UI 测试**默认桌面无关**：窗口停 `-32000`，输入走 UIA pattern / posted 消息；`-ShowOnScreen` 恢复前台 | 不得引入合成鼠标移动（`DesktopFreeUiHarnessTests` 会拦） |
| `RcloneLifecycleHost.GetClientAsync` **会真的起 rclone 进程**，而 `IAppServiceHost.RcloneHost` 是 **sealed 具体类**（无法替换） | 「不碰 rclone」的测试必须靠**不触发 `BrowseAsync`**，并用「`StatusText` 没被写过」来证明（`AssertNoDirectoryListing` 就是这个） |
| 桌面场景里 `AutomationProperties.Name` 会**覆盖** `Content` 派生的名字 | 别想用 UIA 的 `Name` 去断言一个「标签会变」的按钮；改断言它的 `SelectionPattern` / 布局 |
| WPF 跨进程 UIA 客户端拿不到 `TextBlock` 的 `TextPattern` | 断言文本要靠 `Name`（因此动态文本 `TextBlock` 必须**去掉**静态 `Name`） |

---

## 5. 剩余路线图（A5 → E）

> 这一节同时是那份 307 行改进计划的替代。**剩余项的顺序与依赖**：A5/A6/A7 互不依赖；
> B 与 C 共享「队列泛化」，所以 **B0 必须在 B 的跨端点复制之前落**；D 与 A/B/C 完全解耦；E 是收尾集成。

### A5. 代理（原 A7）

- `ApplicationSettings.CloudProxy`（URL 或空）。
- **机制已经存在，不用新造**：`src/FengSync.Core/Rclone/Transport/RcloneProxyConfiguration.cs` 里的
  `RcloneProxyOptions` / `RcloneProxyResolver.Resolve(application, environment, winInet)`
  （优先级 **Application > Environment > WinInet**）；`RcloneDaemon.StartAsync(..., RcloneProxyOptions?)` 与
  `RcloneEnvironment.Prepare(...)` 都已接受它，注入 `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`/`NO_PROXY`
  并设 `GODEBUG=netdns=go` + `FENGSYNC_RCLONE_PROXY_SOURCE`。
- **缺的只是接线**：谁读 `ApplicationSettings.CloudProxy` 并把它传给 `RcloneDaemon.StartAsync`。
- 设置页必须注明「**仅影响云盘**」（同步路径的代理是另一套）。
- 设置项三件套别忘了（见 §7）。
- 遗留的实证点：RC 是否有全局 proxy 透传（`_config` 支持哪些键），还是必须走环境变量。

### A6. 多账户分组/标签（原 A8）

`CloudEndpointMetadata` 加 `Group` / `Tags` 字段，列表页**按组分区**显示；分组信息存 `cloud-endpoints.json`。
注意 `CloudEndpointMetadata` 是 `System.Text.Json` 序列化的（`CloudEndpointJsonContext`），加字段要保持**向后兼容**
（旧文件没有这两个字段时要能读）。

### A7. 端点导出/导入（原 A9）

导出 `cloud-endpoints.json` 的**非敏感**子集 + 每个端点的**参数名清单**（值一律留空，绝不写 secret/token/obscured 值）；
导入时让用户补参数。测试要断言**导出内容不含 conf 里的密码串**。
（铁律：凭据只留在 `rclone.conf`。）

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
| 服务端复制无 staging，失败留半成品 | 实证 `operations/copyfile` 失败后目标不存在；用假 RC 断言失败时无目标对象 |
| 端点重命名迁移中途失败 | 单测覆盖每步注入失败，断言 conf 与两个 JSON 回到原状；S3 场景断言 `<old>_s3` 不残留 |
| 跨端点 move 的 delete 失败 → 重复文件 | 单测模拟 delete 抛错，断言提示「已复制但源未删除」而不是报成功 |
| 键盘快捷键劫持地址栏/搜索框输入 | UI 场景：在 `AddressBox` 里按 Del/F2，断言不触发删除/重命名 |

---

## 6. 实现前必须先实证的 6 个点（不准猜，先验证再写）

1. `use_trash=false` 的 alias 方案在 v1.75 的 RC 路径下是否可用（**B5 永久删除**）。
2. RC 是否有全局 proxy 透传（`_config` 支持哪些键），还是必须走环境变量（**A5**）。
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
| `src/FengSync/Services/CloudEndpointList.cs` | 端点列表的搜索与排序纯函数（本批新增） |
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
