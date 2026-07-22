# 可重复 GUI 实测

`tests/FengSync.UiTests/Scripts/Invoke-UiScenario.ps1` 由 `dotnet test` 调用 Windows UI Automation 启动真实 WPF 程序。每个用例都有独立的 `FENGSYNC_DATA_DIR`、工作目录和截图产物；测试经可见控件操作后，再独立验证磁盘或真实远端副作用。

当前统一场景包括：

- 本地首次双向同步、双端冲突及界面内左右覆盖裁决；
- 更新与镜像模式、删除风险确认；
- 同步前按文件取消选择；
- Profile 新建、编辑、取消和重启后的持久化；
- 程序默认配置应用与重新打开验证；
- 操作菜单的运行历史（由真实同步生成记录）；
- 真实 SFTP 下载，以及经“云端端点管理”创建 SFTP 端点、加入右侧并上传；
- 内置 SFTP Server 设置的启动/停止专用验收；
- 已配置时的 Google Drive 上传/重新启动后下载回环。

```powershell
pwsh -File .\scripts\Test-All.ps1
```

Google Drive 场景会读取当前 Feng Sync 的 rclone 配置，自动检测第一个 Google Drive 连接；测试只会在该连接的 `test/FengSync-Automated-Tests/<run-id>` 下创建随机子目录，并会在结束时删除该子目录：

```powershell
dotnet test .\tests\FengSync.UiTests --filter "Category=External"
```

每次运行会创建独立的 `.fengsync-test/ui` 目录。成功时清理；失败时保留该目录以供查看输出、截图和隔离配置。不会触及普通 Profile、远端账号或 `test` 外的用户文件。

本地调试单个统一场景：

```powershell
dotnet test .\tests\FengSync.UiTests --filter "FullyQualifiedName~Sftp_endpoint_is_created"
```

完整入口会分别汇报构建、核心/CLI/真实 SFTP 协议和 UI 阶段的耗时；仅跳过外部 Drive 时使用：

```powershell
pwsh -File .\scripts\Test-All.ps1 -SkipGoogleDrive
```
