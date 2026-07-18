# 05 统一同步执行器

## 实施目标

合并当前本地与通用端点执行语义，使 journal、校验、进度、版本管理、取消和结果模型对本地、SFTP、Google Drive 一致。

## 界面设计

- 进度窗口显示总字节、已完成字节、文件数、速度、预计时间、失败数和当前并发项。
- 每项显示等待、传输、校验、提交、失败、取消状态。
- 支持取消；能力允许时后续增加暂停。关闭窗口可选择后台运行，不等同取消。
- 完成页按成功、跳过、失败分类，并支持仅重试失败项。

## 建议代码文件

- 新增 `src/FengSync.Core/Execution/SyncExecutor.cs`：唯一执行编排器。
- 新增 `src/FengSync.Core/Execution/OperationPipeline.cs`：准备、传输、校验、提交阶段。
- 新增 `src/FengSync.Core/Execution/SyncRunResult.cs`：结构化总结果和逐项结果。
- 新增 `src/FengSync.Core/Execution/TransferProgress.cs`：字节级进度事件。
- 新增 `src/FengSync.Core/Execution/ContentVerifier.cs`：按端点能力校验。
- 新增 `src/FengSync.Core/Execution/VersioningService.cs`：统一删除/归档语义。
- 新增 `src/FengSync.Core/Execution/MultipartTransferState.cs`：S3 分段上传的 upload ID、part 与恢复状态。
- 逐步移除 `LocalExecutor.cs` 与 `EndpointExecutor.cs`，其能力迁入上述组件。
- 修改 `ProgressWindow.xaml`、`ProgressWindow.xaml.cs`：消费真实进度和结果。

## 功能流程

执行器接收不可变计划快照，先创建目录，再受控并发复制到临时名，校验后提交，最后处理删除。对象存储不具备原子移动时采用 staging key、服务端复制和条件请求；分段上传状态写入 journal。单项失败按 Profile 策略停止或继续。

## 验收标准

- 本地和远端均执行相同阶段并产生相同格式日志。
- `VerifyCopies` 对所有端点生效，并清楚标注完整 hash 或降级校验。
- 进度来自真实字节数；取消后不留下可见半文件。
- 一个文件失败时能准确展示并按策略处理其余文件。
