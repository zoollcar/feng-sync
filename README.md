# Feng Sync

Feng Sync 是一个面向 Windows 的桌面文件同步工具。它提供可视化的比较与确认流程，也提供适合计划任务的命令行入口；可在本地文件夹、SFTP、Google Drive 与 S3 存储之间同步文件。

项目基于 .NET 10、 WPF、 [rclone](https://github.com/rclone/rclone) 开发。

<img width="1625" height="945" alt="abe3eaf494525922ac2805e19faa85ab" src="https://github.com/user-attachments/assets/27f55cc1-7f2d-4317-842a-4a98b9bcfc0a" />
<img width="1478" height="925" alt="image" src="https://github.com/user-attachments/assets/ee377cf1-b4d6-46b7-8594-b9acfc6e95f0" />

## 已实现功能

### 端点与同步模式

- 本地文件夹，以及 `sftp://`、`gdrive://`、`s3://` 格式的远程端点。
- 同步界面用于创建、测试、浏览、重连和管理 SFTP、Google Drive、S3 连接；Google Drive 授权在浏览器完成。
- S3 端点可以配置**前缀**：保存时除了存放凭据的 s3 remote 外，还会额外生成一个 rclone `alias` 指向 `<bucket>/<前缀>`，端点只能访问该前缀下的内容，越界（`..` 或绝对路径）由 rclone 直接拒绝，浏览、上传、下载、同步、挂载都受同一限制。前缀留空则只能访问该 Bucket。
- 云盘界面用于手动上传下载管理远程端点的文件。
- 云盘管理支持把文件或文件夹拖入文件浏览器上传、把云端文件拖出下载；上传和下载都显示进度、可中途取消，并复用同步引擎既有的暂存/续传机制。
- 云盘管理可独立运行：发行包除 `FengSync.exe` 主入口外还提供 `DriveHub.exe`，双击即可只打开云盘管理窗口，不必启动完整同步界面。
- 云盘端点可导出为 JSON 再导入到另一台机器：导出只含名称、类型、前缀、分组、标签和参数名清单，**不含任何凭据**（凭据始终只留在 `rclone.conf`）；导入时逐个端点打开表单补填凭据，名称冲突会自动改名而不是覆盖。
- 内置sftp服务器，用于方便局域网文件同步。
- 局域网快传：首页「网盘管理」上方的入口打开一个独立窗口，在同一局域网内与手机或另一台电脑互传文件和文件夹。手机扫屏幕上的二维码即在浏览器里打开收发页，也可以手输 6 位配对码或直接填 `IP:端口`；电脑之间通过 UDP 自动发现，发现失败时手输地址的入口始终留在界面上。监听服务只在窗口打开期间存在，关窗即停；发送篮里的内容是对端唯一能读到的东西，文件夹由服务端流式打包成 zip，接收内容默认落在系统「下载」目录且可改，写入复用同步引擎的临时文件与原子改名机制。
- 将远程端点挂载为磁盘。

### 安全与可靠性

- 同步前检查端点相同或嵌套、镜像空源删除、删除数量/比例阈值和本地目标磁盘空间。
- 会覆盖或删除文件时显示受影响文件数、覆盖数、删除数和传输大小，并要求确认；超过删除阈值时可要求输入 Profile 名称确认。
- 执行前重新检查源文件是否在比较后变化，避免按过期计划传输。
- 复制采用临时文件后原子提交；可选复制后校验，传输完成后才执行删除。
- 断点续传：不小于设定大小（全局默认 500 MB，可按 Profile 覆盖）的文件先写入 `<文件名>.fengsync.partial`，中断后下次运行会核对源文件身份与拼接点字节，确认它仍是同一源文件的前缀才从断点继续追加，传输完成后改名/移动为正式文件。若中断正好发生在写入过程中、末段字节不可信，只会丢弃那一小段并回退到最后一个校验通过的位置继续，不会因为几个字节重传整个文件。小于阈值的文件走 rclone 的多流复制，更快，但其半成品文件不是可信前缀，因此不做续传。
- 续传的边界（rclone 的限制，不是可以绕过的配置）：从远程**下载**可以续传；**上传**到远程无法续传，因为 rclone 的 RC 接口既没有按偏移写入也没有 append，上传中断后会丢弃 `.fengsync.partial` 并整体重传；远程到远程的复制不经过本地磁盘，同样无法续传。
- 删除策略支持永久删除、Windows 回收站，或移入带时间戳的本地归档目录；归档可按天数、每文件版本数和总容量清理。
- 局域网快传的凭据规则：配对码单次使用且 120 秒过期，二维码里印的是配对码而**不是**令牌；配对令牌属于凭据，只用当前用户 DPAPI 存放在应用数据目录，绝不进入 JSON、二维码、日志、命令行或测试夹具。陌生设备的第一笔写入必须在窗口里点「同意」，成为受信设备后默认自动接收（可关）。**传输为明文 HTTP，且刻意不做请求签名**：同一网段能嗅探到令牌时，给请求套签名只是把复杂度换成假的安心感；安全边界因此落在别处——单次配对的码、首笔人工确认、可随时撤销的受信列表，以及令牌本身的权限范围（只能写接收目录、只能读本次会话的发送篮，没有任何由调用方拼出的读路径）。窗口打开期间防火墙按端口（TCP 与 UDP 各一条）放行的正是这个服务，因此同一局域网内的设备可以访问该端口。
- 任务日志、未完成事务与基线记录可用于恢复提示；失败不会提交新的双向同步基线。

### 配置与自动化

- Profile 管理：新建、编辑、保存、打开、导入/导出、批处理作业、禁用与删除。
- 全局默认值与 Profile 覆盖值：并发数、复制校验、时间容差、续传最小文件大小、过滤和版本策略。
- 有序的包含/排除规则，支持 glob、文件大小、修改时间、隐藏文件和符号链接条件。
- 运行历史：按结果和时间范围查看计划数、成功/失败数、传输字节和错误详情。
- CLI 支持无交互比较或执行，并输出 JSON 和稳定退出码。
- 可通过 Windows Task Scheduler 创建、测试和删除按 Profile 运行的计划任务；任务参数只存 Profile ID，不包含连接凭据。
- 内置 SFTP 服务器：可在 UI 中配置监听地址/端口、单个共享根目录和密码账号，并支持启动、停止和诊断。服务器由随应用分发的 rclone 提供，不依赖 Node.js 或 npm 模块。
- 局域网快传窗口：显示配对二维码与 6 位配对码（可重新生成），多个网卡地址逐个列出并可点选（二维码跟随所选地址重画），接收目录可改，「收到文件时自动同意」可开关，受信设备可查看并逐个取消信任，端口可修改，并有一键添加防火墙端口规则的按钮（规则按端口而非程序路径，TCP 与 UDP 各一条；仅在规则确实缺失时才提权，因此重复点击无害，代价是可能出现一次 UAC 弹窗）。
- rclone 远程盘挂载管理：可在工具菜单的“远程端点管理”窗口把 Google Drive / SFTP / S3 挂载为 Windows 盘符或目录，扫描系统全部 rclone mount 进程并显示实时状态；正常退出时自动停止 Feng Sync 创建的挂载，异常退出后仍能扫描、显示和手动卸载残留挂载；创建前检测 WinFsp，缺失时给出明确提示。左右端点框上的 ☁ 按钮各自打开专用的端点选择窗口，按按钮所在侧添加到对应端点，不再二次询问左右。

## 快速开始

### 运行桌面程序

先安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)，然后在仓库根目录执行：

```powershell
dotnet run --project src/FengSync
```

在主界面填写左右端点并点击“比较”。确认预览中的操作后点击“同步”。本地端点填写现有文件夹路径；远程端点可点击路径框旁的云图标创建连接，或直接使用以下 URI：

```text
sftp://<rclone-remote>/<path>
gdrive://<rclone-remote>/<path>
s3://<rclone-remote>/<bucket-or-path>
```

首次双向同步会建立基线；之后才能可靠判断删除和双侧修改。冲突不会自动覆盖。

### 单独打开云盘管理（DriveHub）

发行包中除了主入口 `FengSync.exe`，还有独立的 `DriveHub.exe`，双击即可只打开云盘管理窗口：

```powershell
# 从源码直接运行
dotnet run --project src/FengSync.DriveHub
```

它与主程序共用同一份云端端点配置（rclone.conf），但各自运行独立的 rclone 守护进程和传输队列：单独使用或与主程序同时运行都可以，两者各自管理自己创建的挂载，互不影响。

### 使用命令行

构建后可通过 Profile ID 或导出的 Profile JSON 执行比较/同步：

```powershell
dotnet build FengSync.sln

# 使用已保存的 Profile
.\src\FengSync.Cli\bin\Debug\net10.0\FengSync.Cli.exe compare --profile <profile-id>
.\src\FengSync.Cli\bin\Debug\net10.0\FengSync.Cli.exe run --profile <profile-id> --non-interactive --json-log

# 使用 Profile 文件
.\src\FengSync.Cli\bin\Debug\net10.0\FengSync.Cli.exe compare --profile .\my-profile.fengsync.json
```

CLI 始终输出一行 JSON。退出码区分成功、冲突、配置错误、执行失败和取消，便于脚本或计划任务处理。

## 数据与凭据

应用数据默认保存在用户本地应用数据目录，也可以通过 `FENGSYNC_DATA_DIR` 指定隔离的数据目录。Profile 不保存远程密码、私钥或 OAuth 信息；远程连接由 rclone 配置管理，运行时凭据不写入 Profile、CLI 参数或运行日志。

对于内置 SFTP 服务，请仅监听受信任的网络地址；每个服务实例暴露一个可读写共享根目录和一个密码账号。

## 测试

测试入口按依赖范围分为三个级别：

```powershell
# 1. 核心、CLI、真实 SFTP 协议；不启动 WPF UI。
pwsh -File .\scripts\Test-All.ps1 -Level Core

# 2. 级别 1 + 本地与 SFTP 的 WPF UI；不访问线上服务商（默认）。
pwsh -File .\scripts\Test-All.ps1 -Level UiOffline

# 3. 级别 2 + 所有标记为 External 的线上服务 UI 测试。
pwsh -File .\scripts\Test-All.ps1 -Level Online
```

也可以单独运行 `dotnet test .\tests\FengSync.Tests` 或 `dotnet test .\tests\FengSync.UiTests`。线上服务 UI 用 `FENGSYNC_TEST_ONLINE_SERVICES=1` 启用；`-Level Online` 会自动设置并在结束后清除该环境变量。

UI 测试默认**跑在私有桌面上**：测试宿主自己创建一个桌面对象，并在那个桌面上启动场景宿主，场景再启动的所有进程都会继承它。没有被显示的桌面不会被合成到任何显示器上，而且它有自己独立的输入队列和前台窗口——所以应用可以打开真实窗口、把窗口提到前台、接受真实鼠标点击，而这一切都到不了你的会话。本仓库实测：一次完整场景运行的 390 次采样里，可见桌面上属于应用的窗口数为 0，前台窗口全程未变。

```powershell
# 默认：每个场景跑在自己的私有桌面上，不显示、不抢鼠标、不抢前台
pwsh -File .\scripts\Test-All.ps1 -Level UiOffline

# 需要盯着看时：跑在当前桌面上，窗口正常显示，使用真实鼠标输入
pwsh -File .\scripts\Test-All.ps1 -Level UiOffline -ShowOnScreen
```

改动测试脚本前有两点值得先知道：

- **合成鼠标输入在私有桌面上根本无法工作**（实测对照：私有桌面上 `GetCursorPos` / `mouse_event` 返回 `ERROR_ACCESS_DENIED`、`SetCursorPos` 返回 false；可见桌面上三者都成功）。所以少数确实需要真实点击/悬停的步骤只在 `-ShowOnScreen` 下执行，默认路径改用 UI Automation 模式、投递按键和 `PostMessage`——它们直接进入窗口自己的消息队列，与窗口在哪个桌面无关。
- **不要用 `Process.MainWindowHandle` 找主窗口**。它只是个启发式规则（进程拥有的第一个可见顶层窗口），而私有桌面上应用还拥有输入指示器覆盖窗口，它们会在启动几秒后出现并压在一切之上。请用 `Get-MainWindowHandle`，它按窗口本身的特征识别主窗口；`PrivateDesktopHarnessTests` 会在 `.MainWindowHandle` 回来时报错。

私有桌面上没有屏幕可截，截图改由窗口自己渲染（`PrintWindow` + `PW_RENDERFULLCONTENT`），并让应用走受支持的软件渲染路径；`ui-shell-native` 有意保留原生渲染，其截图只有窗口框架，清单里会注明。

## 项目结构

```text
src/FengSync.Core   同步规划、执行、安全校验、端点、配置、历史、自动化与 SFTP 服务核心
src/FengSync.Lan    局域网快传的服务端：Kestrel 监听、配对与受信状态、接收落盘、UDP 发现与防火墙规则；不经 rclone，也不被 Core 或 CLI 引用
src/FengSync        Windows WPF 桌面程序
src/FengSync.DriveHub  独立的云盘管理入口（DriveHub.exe），复用主程序的窗口与服务
src/FengSync.Cli    无人值守的比较与同步命令行程序
tests               xUnit 核心/集成测试和 PowerShell GUI 验收测试
```
