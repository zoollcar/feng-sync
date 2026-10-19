# Feng Sync

Feng Sync 是一个面向 Windows 的桌面文件同步工具。它提供可视化的比较与确认流程，也提供适合计划任务的命令行入口；可在本地文件夹、SFTP、Google Drive 与 S3 存储之间同步文件。

项目基于 .NET 10、 WPF、 [rclone](https://github.com/rclone/rclone) 开发。

<img width="1625" height="945" alt="abe3eaf494525922ac2805e19faa85ab" src="https://github.com/user-attachments/assets/27f55cc1-7f2d-4317-842a-4a98b9bcfc0a" />
<img width="1780" height="941" alt="Screenshot 2026-10-19 113300" src="https://github.com/user-attachments/assets/e743455c-6bdc-4dc0-bb25-1d21032d30d5" />
<img width="1525" height="1010" alt="Screenshot 2026-10-19 113428" src="https://github.com/user-attachments/assets/0b745ae5-a34b-4344-9f9f-118cdb5bccab" />


## 已实现功能

### 端点与同步模式

- 本地文件夹，以及 `sftp://`、`gdrive://`、`s3://` 格式的远程端点。
- 同步界面用于创建、测试、浏览、重连和管理 SFTP、Google Drive、S3 连接；Google Drive 授权在浏览器完成。
- 云盘界面用于手动上传下载管理远程端点的文件。
- 云盘管理支持把文件或文件夹拖入文件浏览器上传、把云端文件拖出下载；上传和下载都显示进度、可中途取消，并复用同步引擎既有的暂存/续传机制。
- 云盘管理可独立运行：发行包除 `FengSync.exe` 主入口外还提供 `DriveHub.exe`，双击即可只打开云盘管理窗口，不必启动完整同步界面。
- 内置sftp服务器，用于方便局域网文件同步。
- 局域网快传。
- 将远程端点挂载为磁盘。

### 安全与可靠性

- 同步前检查端点相同或嵌套、镜像空源删除、删除数量/比例阈值和本地目标磁盘空间。
- 会覆盖或删除文件时显示受影响文件数、覆盖数、删除数和传输大小，并要求确认；超过删除阈值时可要求输入 Profile 名称确认。
- 执行前重新检查源文件是否在比较后变化，避免按过期计划传输。
- 复制采用临时文件后原子提交；可选复制后校验，传输完成后才执行删除。
- 断点续传：不小于设定大小（全局默认 500 MB，可按 Profile 覆盖）的文件先写入 `<文件名>.fengsync.partial`，中断后下次运行会核对源文件身份与拼接点字节，确认它仍是同一源文件的前缀才从断点继续追加，传输完成后改名/移动为正式文件。若中断正好发生在写入过程中、末段字节不可信，只会丢弃那一小段并回退到最后一个校验通过的位置继续，不会因为几个字节重传整个文件。小于阈值的文件走 rclone 的多流复制，更快，但其半成品文件不是可信前缀，因此不做续传。
- 续传的边界（rclone 的限制，不是可以绕过的配置）：从远程**下载**可以续传；**上传**到远程无法续传，因为 rclone 的 RC 接口既没有按偏移写入也没有 append，上传中断后会丢弃 `.fengsync.partial` 并整体重传；远程到远程的复制不经过本地磁盘，同样无法续传。
- 删除策略支持永久删除、Windows 回收站，或移入带时间戳的本地归档目录；归档可按天数、每文件版本数和总容量清理。
- 局域网快传的凭据规则：配对码单次使用且 120 秒过期，二维码里印的是配对码而**不是**令牌；配对令牌属于凭据，只用当前用户 DPAPI 存放在应用数据目录，绝不进入 JSON、二维码、日志、命令行或测试夹具。陌生设备的第一笔写入必须在窗口里点「同意」，成为受信设备后默认自动接收（可关；这个开关同时管两条路——别人推过来的字节，和群里说过、由本机自己去取的文件）。**传输为明文 HTTP，且刻意不做请求签名**：同一网段能嗅探到令牌时，给请求套签名只是把复杂度换成假的安心感；安全边界因此落在别处——单次配对的码、首笔人工确认、可随时撤销的受信列表，以及令牌本身的权限范围（只能写接收目录、只能读本机自己放进群记录并且确实握在手里的那些内容，没有任何由调用方拼出的读路径，也没有一份供人枚举的可分享清单——群记录本身就是唯一的列表；在网页上点「接收」多出来的一条权限也只是让本机去群里拉一件它说过的文件，走的仍是同一条落盘规则）。群记录只在内存里，随窗口关闭而消失，磁盘上不存在一份可以被人翻出来的群历史。窗口打开期间防火墙按端口（TCP 与 UDP 各一条）放行的正是这个服务，因此同一局域网内的设备可以访问该端口。
- 任务日志、未完成事务与基线记录可用于恢复提示；失败不会提交新的双向同步基线。

### 配置与自动化

- Profile 管理：新建、编辑、保存、打开、导入/导出、批处理作业、禁用与删除。
- 全局默认值与 Profile 覆盖值：并发数、复制校验、时间容差、续传最小文件大小、过滤和版本策略。
- 有序的包含/排除规则，支持 glob、文件大小、修改时间、隐藏文件和符号链接条件。
- 运行历史：按结果和时间范围查看计划数、成功/失败数、传输字节和错误详情。
- CLI 支持无交互比较或执行，并输出 JSON 和稳定退出码。
- 可通过 Windows Task Scheduler 创建、测试和删除按 Profile 运行的计划任务；任务参数只存 Profile ID，不包含连接凭据。
- 内置 SFTP 服务器：可在 UI 中配置监听地址/端口、单个共享根目录和密码账号，并支持启动、停止和诊断。服务器由随应用分发的 rclone 提供，不依赖 Node.js 或 npm 模块。
- 局域网快传窗口：中间是群聊，可以发文字，也可以用「发文件…」「发文件夹…」或直接把文件拖进窗口来发内容——发出去的只是一条「本机有这个文件」的消息，谁要谁在那一条下面点「接收」，进度和落地位置都画在那一条上；陌生设备直接推过来的字节仍在窗口顶部列出并要点「同意」。右侧是配对二维码与 6 位配对码（可重新生成）、多个网卡地址（二维码跟随所选地址重画）、群里已配对的设备（可逐个取消信任）、UDP 发现的电脑和手输 `IP:端口` 的入口。顶部一条依次是这台电脑的名字（改名会写进本机身份文件并在群里说一句）、自动接收开关、接收目录、端口与一键防火墙按钮（规则按端口而非程序路径，TCP 与 UDP 各一条；仅在规则确实缺失时才提权，因此重复点击无害，代价是可能出现一次 UAC 弹窗）。收发历史在右下角可查。
- rclone 远程盘挂载管理：可在工具菜单的“远程端点管理”窗口把 Google Drive / SFTP / S3 挂载为 Windows 盘符或目录，扫描系统全部 rclone mount 进程并显示实时状态；正常退出时自动停止 Feng Sync 创建的挂载，异常退出后仍能扫描、显示和手动卸载残留挂载；创建前检测 WinFsp，缺失时给出明确提示。左右端点框上的 ☁ 按钮各自打开专用的端点选择窗口，按按钮所在侧添加到对应端点，不再二次询问左右。

## 快速开始

### 运行桌面程序

先安装 [.NET 10 SDK](https://dotnet.microsoft.com/download) 和 [Node.js 22](https://nodejs.org/)，然后在仓库根目录执行：

```powershell
dotnet run --project src/FengSync
```

Node 只在构建时需要：局域网快传的手机页面是 `web/` 下的 React + Tailwind 工程，`dotnet build` 会先跑 `npm ci` 和 `npm run build`，再把产物嵌进程序集。运行时不依赖 Node，也不需要另外部署一份网页文件。

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

## 项目结构

```text
src/FengSync.Core   同步规划、执行、安全校验、端点、配置、历史、自动化与 SFTP 服务核心
src/FengSync.Lan    局域网快传的服务端：Kestrel 监听、配对与受信状态、只在内存里的群记录与它的轮询取件、接收落盘、UDP 发现与防火墙规则；不经 rclone，也不被 Core 或 CLI 引用
src/FengSync        Windows WPF 桌面程序
src/FengSync.DriveHub  独立的云盘管理入口（DriveHub.exe），复用主程序的窗口与服务
src/FengSync.Cli    无人值守的比较与同步命令行程序
tests               xUnit 核心/集成测试和 PowerShell GUI 验收测试
```
