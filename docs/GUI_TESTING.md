# 可重复 GUI 实测

`tests/gui/Invoke-GuiSmoke.ps1` 使用 Windows UI Automation 启动真实 WPF 程序，并保存每一阶段的屏幕截图。它覆盖：创建并编辑 Profile、比较、本地同步、进度窗口生命周期和落盘结果。

```powershell
dotnet build FengSync.sln
pwsh -File .\tests\gui\Invoke-GuiSmoke.ps1
```

SFTP 场景要求测试前创建一个 rclone SFTP remote（测试 remote 和测试目录必须隔离）。传入实际 URI 后，脚本将从本地复制到该远端并等待 GUI 的完成状态：

```powershell
pwsh -File .\tests\gui\Invoke-GuiSmoke.ps1 -SftpUri 'sftp://fengsync_gui/' -RequireSftp
```

每次运行会创建独立的 `.fengsync-test/gui-<timestamp>` 输入目录，并把截图写到 `artifacts/gui-smoke`；不会删除现有 Profile、远端账号或用户文件。
