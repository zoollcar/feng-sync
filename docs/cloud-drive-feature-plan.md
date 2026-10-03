# 云盘功能缺口盘点与演进计划

对照 Google Drive / OneDrive / Dropbox / 百度网盘 / RaiDrive / Air Explorer 等成熟客户端，盘点 Feng Sync「云盘管理」当前已实现的能力与缺口，按功能分组给出结论与优先级。

本文所有「现状」结论均来自代码实测（文件路径与符号名已标注），不是猜测。

---

## 0. 现状快照（评估基准）

| 维度 | 当前实现 | 代码位置 |
| --- | --- | --- |
| 端点类型 | 仅 3 种：Google Drive、SFTP、S3（S3 用 rclone `alias` 做前缀硬边界） | `CloudEndpointService.ProviderKind` |
| 端点操作 | 新建、编辑、删除、刷新、重新登录（仅 GDrive） | `RemoteEndpointManagerWindow.xaml.cs` |
| 文件浏览 | 单栏 GridView 列表：名称 / 类型 / 大小 / 修改时间；进入目录、返回上一级、刷新 | `RemoteEndpointManagerWindow.xaml` |
| 文件操作 | 双击打开、右键「下载…」「打开」、拖入上传、拖出下载 | 同上 |
| 传输 | 单文件粒度任务、并发固定 2、进度/速度、单任务取消、全部取消、清除已完成、断点续传（下载可续、上传重来）、日志跨重启恢复 | `CloudTransferQueue` |
| 挂载 | 盘符或目录两种目标；可挂载/取消挂载；能扫描并区分「本应用启动」与「外部」挂载 | `MountPickerDialog`、`RcloneMountService` |
| 挂载参数 | 全部硬编码：缓存 10 GB、缓存期 1 h、回写 5 s、`CacheMode=writes`、`NoChecksum`、`NoModTime` | `MountOptions`（static class + const） |
| 云盘设置 | **无任何云盘专属设置项**；`ResumeMinimumBytes` 直接复用同步的 `DefaultResumeMinimumBytes` | `ApplicationSettings`、`MainWindow.xaml.cs:459` |

> 上表是写这份文档时的评估基准，刻意保留原样。此后已落地的部分：
> 「端点类型」已由 `CloudProviderCatalog`（描述符）与 `EndpointSchemes`（scheme/backend 映射）取代，
> 现有 Google Drive、OneDrive、Dropbox、Box、WebDAV、SFTP、S3 七种，`CloudEndpointService.ProviderKind` 已删除。
> **A3 端点重命名**已实现（`CloudEndpointRename` + `RcloneConfigFile`）：实现时实证发现「create → 迁移 → delete」
> 走不通 —— 对 OAuth 后端 `config/create` 会重新跑授权状态机，所以改为直接改写 rclone.conf 的段（值逐字节不变），
> 运行中的 `rclone rcd` 会在下一次调用就读到改动，改写后用 `config/get` / `config/listremotes` 回读验证。
> 同时把 `profiles.json` 里 `scheme://<旧名>/...` 的引用一并迁移，并在有活跃挂载时直接阻断。

一个关键结论先说在前面：**`IEndpoint` 里已经具备增删改的能力，只是云盘 UI 一个都没接。**

- `IEndpoint.CreateDirectoryAsync` / `DeleteAsync` / `MoveAsync` / `MoveDirectoryAsync` / `StatAsync` — 全部存在（`Endpoints.cs:385-452`）
- `RcloneRcClient.MoveFileAsync` / `DeleteFileAsync` / `MakeDirectoryAsync` / `RemoveDirectoryAsync` / `ProbeAsync` — 全部存在（`Endpoints.cs:255-259`）
- `CloudFileManagerService` 只用了 `ListDirectoryAsync` 一个方法

也就是说「新建文件夹 / 重命名 / 删除 / 移动」这几项**不需要动 Core 层**，主要是 UI 与安全校验的工作量。

---

## A. 端点与账户管理

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| A1 | 登录/授权状态 | 无。GDrive token 过期只能在传输失败时才知道 | 未使用 rclone `config/oauthstatus`；列表不显示「已连接 / 需重新登录」 | OneDrive、Dropbox 在账户旁常驻状态徽标 | **P0** |
| A2 | 容量/配额显示 | 无 | 未使用 rclone `operations/about` | 所有网盘客户端都显示「已用 / 总量」 | **P0** |
| A3 | 重命名端点 | 不支持 | rclone 无改名 API，需要 create → 迁移引用 → delete；涉及 Profile、挂载、metadata、alias backing 四处引用 | 网盘客户端普遍允许改显示名 | P1 |
| A4 | 端点搜索/排序 | 无，纯列表 | 端点多了难找 | 通用 | P1 |
| A5 | 端点健康检查 | 仅编辑窗有「测试连接」 | 列表页无「测试全部」/「一键体检」 | RaiDrive 有连接测试 | P1 |
| A6 | 更多 Provider | 仅 3 种 | rclone 支持 OneDrive、Dropbox、WebDAV、阿里云盘、腾讯云 COS、B2、pCloud、Box、Mega… | 聚合类客户端支持 40+ | P1 |
| A7 | 代理设置 | 无 | rclone 支持 `--proxy` / `HTTP_PROXY`，UI 未暴露 | 企业网络必备 | P2 |
| A8 | 多账户分组/标签 | 无，靠显示名区分 | 同类型多账号难以区分 | 网盘客户端按账户分组 | P2 |
| A9 | 端点导出/导入 | 无 | 只能手工编辑 rclone.conf | 便于迁移与备份（需脱敏） | P2 |

---

## B. 文件浏览与操作（缺口最大的一组）

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| B1 | 新建文件夹 | **无** | `CreateDirectoryAsync` 已存在未接 | 全部网盘 | **P0** |
| B2 | 重命名 | **无** | `MoveAsync`（同目录改名）已存在未接 | 全部网盘（F2） | **P0** |
| B3 | 删除 | **无** | `DeleteAsync` 已存在未接；且无回收站概念（rclone 是硬删除） | 网盘默认「移到回收站」，可还原 | **P0** |
| B4 | 多选 / 批量操作 | 无，所有列表 `SelectionMode="Single"` | 无法批量下载/删除；拖出仅支持单文件 | Ctrl/Shift 多选 + 批量队列 | **P0** |
| B5 | 搜索 | 无搜索框 | rclone `operations/list` 支持递归 + filters | 网盘标配 | **P0** |
| B6 | 排序 | 固定「目录优先 + 名称」 | 不能按大小/时间排序，列头不可点击 | 网盘标配 | **P0** |
| B7 | 面包屑导航 | 只有「返回上一级」+ 路径文本 | 无分段点击跳转、无前进、无地址栏手输路径 | 网盘标配 | **P0** |
| B8 | 复制 / 剪切 / 粘贴 | **无** | `RcloneRcClient.CopyFileAsync` 已存在（且支持跨 fs）未接 | 网盘标配 | P1 |
| B9 | 跨端点复制 | **无** | 同上，能力已在，UI 未接 | Air Explorer 支持云到云 | P1 |
| B10 | 上传冲突策略 | 一次性弹窗：全部覆盖 / 全部跳过 / 取消 | 缺「重命名保留两者」与逐文件询问 | 网盘标配 | P1 |
| B11 | 属性 / 详情 | 无 | 无大小统计、无权限、无校验值 | 网盘有「详细信息」 | P1 |
| B12 | 分享链接 | **无** | rclone 有 `operations/publiclink`，UI 未接 | 网盘核心功能之一 | P1 |
| B13 | 回收站 | 无 | 无软删除；GDrive 可用 `--drive-use-trash` 先移到回收站，S3/SFTP 无此概念 | 网盘标配 | P1 |
| B14 | 键盘操作 | 无 | F5 刷新、Del 删除、F2 重命名、Ctrl+A/C/V 全无 | 通用 | P1 |
| B15 | 默认下载目录 | 无，每次弹 SaveFileDialog | 无「下载到默认目录」 | 网盘有固定下载目录 | P1 |
| B16 | 视图切换 / 缩略图 | 只有列表 | 无网格视图、无图片缩略图 | 网盘有列表/网格切换 | P2 |
| B17 | 内置预览 | 无 | 图片/文本/PDF 需先下载再外部打开 | 网盘有在线预览 | P2 |
| B18 | 收藏 / 最近访问 | 无 | — | 网盘有「我的收藏」「最近」 | P2 |
| B19 | 文件夹大小 | 无 | 需递归统计，无「计算大小」入口 | 网盘可查看文件夹属性 | P2 |
| B20 | 拖出整个文件夹 | 明确不支持（代码里提示用户先进入文件夹） | 只能逐个文件拖 | 网盘/Explorer 支持整目录拖出 | P2 |
| B21 | 版本历史 | 无 | 部分后端（GDrive/S3 版本控制）可支持 | 网盘有「历史版本」 | P2 |

---

## C. 传输队列

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| C1 | 暂停 / 继续 | **无，只有取消** | `CloudTransferState` 没有 Paused；暂停后需能原地继续 | 所有下载器都有暂停 | **P0** |
| C2 | 并发数可调 | `MaxConcurrentTransfers = 2` 是 `const` | 无法调整；`ApplicationSettings.DefaultMaxConcurrentCopies` 未接云盘 | 网盘有「同时任务数」 | **P0** |
| C3 | 失败自动重试 | 失败即 `Failed`，需手动点「断点续传」 | `ApplicationSettings.NetworkRetryCount = 3` 未用于云盘队列 | 网盘自动重试 + 退避 | **P0** |
| C4 | 单任务重试按钮 | 只有行内「取消」 | 需全局「断点续传」才能重试单个任务 | 网盘每行有重试 | **P0** |
| C5 | 带宽限速 | 无 | rclone `--bwlimit` 未暴露 | 网盘普遍有「限速」 | P1 |
| C6 | 完成通知 | 无 | `NotifyOnCompletion` 文案是「同步完成时显示通知」，云盘未接 | 网盘有托盘气泡/系统通知 | P1 |
| C7 | 任务优先级 / 排序调整 | 无，FIFO | 无法上移下移 | 下载器支持 | P1 |
| C8 | 传输历史 | 完成后即可被「清除已完成」删掉 | 无历史报表 | 网盘有传输记录 | P1 |
| C9 | 失败诊断入口 | 只有一行错误文本 | 无「复制详情」「查看日志」 | 通用 | P1 |
| C10 | 计划传输 | 无 | 无定时/闲时下载 | 百度网盘「闲时下载」 | P2 |
| C11 | 完成后动作 | 无 | 无「完成后关机/休眠/退出」 | 下载器标配 | P2 |
| C12 | 云到云传输 | **不支持** | `RunAsync` 硬编码 local↔remote 两端，remote→remote 走不通 | Air Explorer 支持 | P2 |
| C13 | 队列窗口右键菜单 | 无 | 无「打开所在文件夹」「复制链接」 | 网盘有 | P2 |

---

## D. 挂载

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| D1 | 挂载高级选项 | **无**，`MountPickerDialog` 只选位置 | 只读模式、卷标、缓存大小/位置、缓存模式、回写间隔、`--dir-cache-time`、`--network-mode` 全不可调 | RaiDrive / RcloneView 有「高级选项」面板 | P1 |
| D2 | 开机自动挂载 / 重启恢复 | **无** | 挂载是进程级，重启即消失，需手动重挂 | 所有网盘客户端都持久化挂载 | P1 |
| D3 | 磁盘空间显示 | 无 | 挂载后 Windows 显示容量不可信，需 `--vfs-disk-space-total-size` 或 `about` | 网盘挂载显示真实配额 | P1 |
| D4 | 挂载失败自动重连 | 无 | 网络抖动后挂载点静默失效 | 网盘自动恢复 | P2 |
| D5 | 只读挂载 | 无 | 无法挂载成只读（防误删） | RaiDrive 支持 | P2 |
| D6 | 缓存管理 UI | 无 | 看不到缓存占用、无法清理 | 网盘有缓存设置 | P2 |
| D7 | 网络位置 / UNC 挂载 | 仅盘符与本地目录 | 无 `\\host\share` 形式 | 部分客户端支持 | P2 |

---

## E. 云盘与同步的联动

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| E1 | 从端点创建同步任务 | 需切到 Profile 编辑器，用 `RemoteEndpointPickerWindow` 选云端端点 | 云盘管理窗内无「用此端点建同步任务」入口 | 网盘有「同步此文件夹」 | P1 |
| E2 | 云端 ↔ 本地一键双向同步 | 可手工建 Profile 实现 | 无一键向导 | OneDrive「备份此文件夹」 | P1 |
| E3 | 按需文件 / 在线文件 | **无** | 无法像 OneDrive 那样「仅在线」占位、用到才下载 | OneDrive Files On-Demand | P2（架构级，工作量大） |

---

## F. 设置、诊断与体验

| # | 功能 | 现状 | 缺口 | 参考做法 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| F1 | 云盘专属设置页 | **完全没有** | 无默认并发、默认限速、云盘专属续传阈值、默认上传冲突策略、默认下载目录 | 网盘都有「传输设置」页 | **P0** |
| F2 | 默认下载目录 | 无 | 每次下载都弹对话框 | 网盘有固定下载目录 | P1 |
| F3 | 传输完成通知 | 无（同 C6） | 无系统通知/托盘提示 | 通用 | P1 |
| F4 | 右键菜单完整性 | 只有文件的「下载/打开」 | 目录无右键菜单；无「删除/重命名/新建文件夹」 | 网盘右键菜单很完整 | P1 |
| F5 | 诊断导出 | 有日志，但云盘无导出入口 | 无「导出诊断包」「复制错误详情」 | 通用 | P2 |
| F6 | 多语言 | 全部中文硬编码 | 无 i18n | 通用 | P2 |
| F7 | 首启引导 | 有提示文案 | 无「一键连接 Google Drive」引导流程 | 网盘有连接向导 | P2 |

---

## 建议实施顺序

### 批次 1 — P0：把「网盘基本操作」补全
用户装上第一天就会碰到的硬缺口，且多数不需要动 Core 层。

1. **文件操作三件套**：新建文件夹、重命名、删除（B1–B3）
2. **多选 + 批量**：多选、批量下载、批量删除（B4）
3. **导航与检索**：面包屑 + 地址栏、前进/后退、排序、搜索（B5–B7）
4. **传输可控**：暂停/继续、并发数可调、失败自动重试、单任务重试按钮（C1–C4）
5. **端点可见性**：登录状态、容量显示（A1–A2）
6. **云盘设置页**：先落一个最小页，承载上面的并发/重试/续传阈值（F1）

### 批次 2 — P1：补齐「像样网盘」的体验
复制/粘贴与跨端点复制（B8–B9）、分享链接（B12）、回收站（B13）、属性（B11）、键盘快捷键（B14）、默认下载目录（B15）、上传冲突策略细化（B10）、挂载高级选项 + 开机自动挂载 + 真实容量（D1–D3）、完成通知（C6）、传输历史（C8）、端点重命名/搜索/体检（A3–A5）、更多 Provider（A6）、同步联动入口（E1–E2）。

### 批次 3 — P2：差异化能力
网格/缩略图/预览（B16–B17）、收藏/最近（B18）、版本历史（B21）、限速（C5）、计划传输（C10）、云到云（C12）、按需文件（E3）、代理（A7）、i18n（F6）。

---

## 实现时必须注意的约束（来自 AGENTS.md 与现有测试）

1. **破坏性操作必须走安全校验**。删除、覆盖属于破坏性动作，需要有显式安全校验与定向回归测试，并保留用户确认与删除阈值。S3/SFTP 没有回收站，删除是不可逆的，确认强度要高于 GDrive。
2. **删除语义要按后端区分**。GDrive 可以「先移到回收站」（rclone `--drive-use-trash`）；S3/SFTP 只能硬删。B13 需要按 provider 分支，不能一刀切。
3. **UI 一致性护栏**。`UiConsistencyGuardrailTests` 要求每个 `<Button>` 显式使用白名单里的语义样式（Primary/Secondary/Tertiary/Danger/Icon），且页面 XAML 不得覆盖 Height/Padding/Background。新增的右键菜单与批量工具条会直接撞这条护栏。
4. **并发数从 `const` 改为可配时**，要保证 `SemaphoreSlim` 重建不破坏在途任务；`ActiveCount` 与 `MaxConcurrentTransfers` 在 `CloudTransferQueue.RunAsync` 里被当作进度参数传给 `EndpointTransfer.CopyToStagingAsync`，改并发会同时影响续传判定。
5. **云到云（C12）需要改 `RunAsync` 的端点构造**（现在硬编码一侧为 `LocalEndpoint`），并且远端之间无法 append，会退化为不可续传 —— 这是设计取舍，不是 bug，需要明确告知用户。
6. **挂载选项要透传**。`MountOptions` 目前是 static class + const，D1 需要把它改成可传入的配置对象，并让 `RcloneMountService.MountAsync` 接收；同时注意 `MountTarget` 的既有结构。
7. **凭据仍然只留在 rclone.conf**。A9 的端点导出必须脱敏，绝不能把 secret/token 写进导出文件。
8. **不要在云盘路径上重写一遍同步引擎**。上传/下载继续走 `CloudTransferQueue`，它复用同步引擎的 staging/resume/journal；新增的删除/移动/复制应通过 `IEndpoint` 调用，不要自己拼 rclone 命令行。
