# Feng Sync

Windows WPF 双向同步的可运行基础实现。当前版本完成本地文件夹端点的扫描、三方比较、人工预览/部分选择、临时文件复制、删除延后及双副本基线状态提交；SFTP 和 Google Drive 适配器将以 rclone RC 接口接入。冲突会阻止本轮同步，避免未裁决覆盖。

## 运行

安装 .NET 10 SDK 后执行：

```powershell
dotnet run --project tests/FengSync.Tests
dotnet run --project src/FengSync
```

先填写左右两个已存在的本地文件夹，点击“比较”，检查并勾选操作后再开始同步。首次同步绝不传播删除；双侧变更会形成冲突并阻止执行。
