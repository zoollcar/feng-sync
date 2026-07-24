# 06：rclone 批量远程操作

## 范围

本模块不替换 rclone，不实现自有 SFTP/Drive/S3 协议栈。它只减少大量小文件场景中的
逐文件 RC HTTP 往返，并改善端点的单路径查询。

必须在扫描、执行、journal 和 baseline 止血完成后实施，否则远程基准会被本地重复扫描
噪声污染。

## 第一阶段：补齐单路径能力

`RcloneRcClient` 增加：

```csharp
Task<RcloneItem?> StatAsync(string fs, string remote, CancellationToken ct);
Task<RcloneJob> StartJobAsync(string operation, object payload, CancellationToken ct);
Task<RcloneJobStatus> GetJobStatusAsync(long jobId, CancellationToken ct);
```

优先使用 rclone 当前版本提供的单对象 stat/metadata API。若后端/API 不支持：

- listing 限定到父目录且不 recurse；
- 客户端精确匹配文件名；
- 不允许退化为根目录递归 listing；
- 记录 capability downgrade。

## 第二阶段：批量复制

策略接口：

```csharp
public interface IBatchTransferEndpoint
{
    Task<IReadOnlyList<BatchOperationResult>> CopyBatchAsync(
        IReadOnlyList<CopyRequest> requests,
        BatchTransferOptions options,
        CancellationToken ct);
}
```

候选实现：

1. rclone 长生命周期 async job；
2. 生成 `--files-from-raw` 清单，启动一个受控 rclone 子进程；
3. RC 支持的批量 operation。

选择顺序以所捆绑 rclone 版本的真实 API 和基准为准。

## 清单安全

- 路径必须是端点 root 下的规范化相对路径；
- 禁止 `..`、绝对路径、NUL；
- 使用 raw files list，避免 shell 解析；
- 子进程参数使用 `ArgumentList`，不拼接命令字符串；
- 凭据只来自现有 rclone config；
- 临时清单放应用私有临时目录；
- 完成/取消后删除；
- 日志只记录条目数和 path hash，不记录敏感完整路径。

## 批次形成

按以下键分组：

```text
source endpoint
target endpoint
direction
root
overwrite/versioning policy
verification policy
```

初始阈值：

- 少于 32 个文件：单项 RC；
- 32–5000：一个批次；
- 超过 5000：分块，块大小通过基准调整；
- 大文件可以走独立批次，避免小文件进度被阻塞。

目录创建尽量由批量 copy 自动完成。删除批次必须在所有复制批次成功后开始。

## 进度与结果映射

批量 job 的结果必须映射回每个 `OperationId`：

- 若 rclone 提供逐文件 stats/event，实时映射；
- 若只提供批次终态，完成后逐个 stat 目标；
- 不能确认的文件标记 Unknown/NeedsRecovery，不能整体假设成功；
- journal 仍记录每个操作的 committed 边界。

取消：

- 请求 rclone job stop；
- 等待有限时间；
- 必要时终止只属于本次 job 的进程；
- 清理 FengSync temporary；
- 不终止共享 rclone daemon 中其他 Profile 的 job。

## 重试与限流

- 429/Retry-After 由批次协调器处理；
- 不在 rclone 内部重试和 FengSync 外部重试之间形成倍增；
- 明确总重试时间上限；
- 单个失败可以拆出后重试；
- 批次部分成功不得重传已确认提交的项目。

## 测试

- 文件名包含空格、换行、Unicode、前导 `-`；
- 32 阈值上下；
- 批次部分失败；
- job 取消；
- daemon 重启；
- API 429/5xx；
- eventual consistency；
- SFTP/Drive/S3 各自 capability；
- 10,000 小文件 RC 请求数；
- 凭据和路径不进入命令行/日志。

## 完成条件

- 远程 10,000 小文件的控制面请求从每文件多次降到少量批次 + 必要验证；
- 单路径验证不会递归列出整个 root；
- 批次结果能安全映射回 journal 和 baseline；
- 单项 RC fallback 始终可用；
- 不改变 rclone 配置和用户凭据模型。

