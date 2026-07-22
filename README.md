# Feng Sync

Feng Sync 是一个面向 Windows 的桌面文件同步工具。它提供可视化的比较与确认流程，也提供适合计划任务的命令行入口；可在本地文件夹、SFTP、Google Drive 与 S3 存储之间同步文件。

项目基于 .NET 10 和 WPF 构建，远程端点通过随应用分发的 rclone 访问。界面和 CLI 使用同一套比较、风险校验、执行、日志与基线逻辑。

## 已实现功能

### 端点与同步模式

- 本地文件夹，以及 `sftp://`、`gdrive://`、`s3://` 格式的远程端点。
- 内置界面用于创建、测试、浏览、重连和管理 SFTP、Google Drive、S3 rclone 连接；Google Drive 授权在默认浏览器完成。
- 双向同步：以两端基线识别新增、修改与删除；同一文件被两侧同时修改时产生冲突，必须先在界面中裁决。
- 镜像：以左端为准同步到右端，包括删除右端多余内容。
- 更新：仅将左端新增或变更复制到右端，保留右端独有内容。
- 比较预览、逐项勾选、临时忽略，以及手动指定“左侧覆盖右侧”或反向覆盖。

“自定义”同步模式在界面中可见，但尚未实现，已禁用。

### 安全与可靠性

- 同步前检查端点相同或嵌套、镜像空源删除、删除数量/比例阈值和本地目标磁盘空间。
- 会覆盖或删除文件时显示受影响文件数、覆盖数、删除数和传输大小，并要求确认；超过删除阈值时可要求输入 Profile 名称确认。
- 执行前重新检查源文件是否在比较后变化，避免按过期计划传输。
- 复制采用临时文件后原子提交；可选复制后校验，传输完成后才执行删除。
- 删除策略支持永久删除、Windows 回收站，或移入带时间戳的本地归档目录；归档可按天数、每文件版本数和总容量清理。
- 任务日志、未完成事务与基线记录可用于恢复提示；失败不会提交新的双向同步基线。

### 配置与自动化

- Profile 管理：新建、编辑、保存、打开、导入/导出、批处理作业、禁用与删除。
- 全局默认值与 Profile 覆盖值：并发数、复制校验、时间容差、过滤和版本策略。
- 有序的包含/排除规则，支持 glob、文件大小、修改时间、隐藏文件和符号链接条件。
- 运行历史：按结果和时间范围查看计划数、成功/失败数、传输字节和错误详情。
- CLI 支持无交互比较或执行，并输出 JSON 和稳定退出码。
- 可通过 Windows Task Scheduler 创建、测试和删除按 Profile 运行的计划任务；任务参数只存 Profile ID，不包含连接凭据。
- 内置 SFTP 服务器：可在 UI 中配置监听地址/端口、账号、密码或公钥、共享目录与权限，并支持启动、停止和诊断。服务器运行依赖 Node.js 与固定版本的 `ssh2` 模块。

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

应用数据默认保存在用户本地应用数据目录，也可以通过 `FENGSYNC_DATA_DIR` 指定隔离的数据目录（尤其适合测试）。Profile 不保存远程密码、私钥或 OAuth 信息；远程连接由 rclone 配置管理，运行时凭据不写入 Profile、CLI 参数或运行日志。

对于内置 SFTP 服务，请仅监听受信任的网络地址，并为账号设置独立的共享目录和合适权限。

## 测试

在当前 Windows 开发电脑一键运行完整测试（核心、CLI、真实 SFTP、真实 WPF UI，以及检测到凭据后的 Google Drive）：

```powershell
pwsh -File .\scripts\Test-All.ps1
```

可按层单独运行：

```powershell
dotnet test .\tests\FengSync.Tests                 # 核心、CLI 与真实 SFTP 协议
dotnet test .\tests\FengSync.UiTests                # 本地、SFTP、Google Drive 的真实 WPF UI
pwsh -File .\scripts\Test-All.ps1 -SkipGoogleDrive  # 暂不执行外部云端
```

GUI 测试会创建隔离的 `.fengsync-test` 测试数据并保存截图。更详细的手动 GUI 冒烟测试说明见 [docs/GUI_TESTING.md](docs/GUI_TESTING.md)。

Google Drive 回环测试会自动检测当前 Feng Sync 的 rclone 配置；发现 Google Drive 凭据后，它只会在该远端的 `test/FengSync-Automated-Tests/<run-id>` 创建临时目录，UI 上传并下载验证后只清理该子目录。未发现凭据时，该外部场景会显式跳过。失败时，本地 `.fengsync-test` 证据目录会被保留。

## 项目结构

```text
src/FengSync.Core  同步规划、执行、安全校验、端点、配置、历史、自动化与 SFTP 服务核心
src/FengSync       Windows WPF 桌面程序
src/FengSync.Cli   无人值守的比较与同步命令行程序
tests              xUnit 核心/集成测试和 PowerShell GUI 验收测试
docs               GUI 测试说明、架构审查与实施设计记录
```

## 相关文档

- [GUI 测试说明](docs/GUI_TESTING.md)
- [架构抽象层审查](docs/ARCHITECTURE_ABSTRACTION_REVIEW.md)
- [实施设计索引](docs/implementation/README.md)
