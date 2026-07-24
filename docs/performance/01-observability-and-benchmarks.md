# 01：可观测性与性能基准

## 目标

先把“慢在哪里”变成可重复的数据。所有后续模块使用同一套指标和数据集，避免只优化单次
手工体验。

## 新增组件

### `SyncRunMetrics`

建议放在 `src/FengSync.Core/Diagnostics/SyncRunMetrics.cs`：

```csharp
public sealed class SyncRunMetrics
{
    public long DirectoryScans;
    public long EntriesEnumerated;
    public long StatCalls;
    public long HashFiles;
    public long HashBytes;
    public long RcRequests;
    public long JournalAppends;
    public long JournalFlushes;
    public long BaselineReads;
    public long BaselineWrites;
    public long BytesRead;
    public long BytesWritten;
}
```

计数使用 `Interlocked`，耗时使用 `Stopwatch.GetTimestamp()`。不要在热路径创建日志字符串。

### 阶段计时

固定阶段名称：

- `endpoint.open`
- `scan.left`
- `scan.right`
- `baseline.load`
- `compare.plan`
- `safety.validate`
- `freshness.validate`
- `transfer`
- `verify`
- `delete`
- `baseline.commit`
- `journal.finalize`

阶段结果写入运行历史扩展字段或单独的诊断 JSON。历史 UI 暂时只展示总耗时，详细字段供
CLI、测试和问题诊断使用。

### rclone 计数

在 `RcloneRcClient.CallAsync` 统一记录：

- operation；
- 请求开始/结束；
- 状态码；
- 请求与响应字节数（可取得时）；
- retry 次数；
- elapsed；
- 不记录凭据、授权头或完整远程路径。

## 基准工程

新增 `tests/FengSync.Benchmarks`，使用 BenchmarkDotNet 测纯算法和可控本地 I/O；端到端
场景使用现有 xUnit/PowerShell 基础设施。

拆成两类：

### 微基准

- 10 万和 100 万 `EntrySnapshot` 建索引；
- 两侧路径集合合并；
- `ModePlanner`；
- `ThreeWayPlanner`；
- baseline 编解码；
- journal 追加和恢复重放；
- 路径规范化与过滤。

微基准不访问真实网络。

### 端到端基准

固定数据集：

| ID | 文件布局 | 用途 |
|---|---|---|
| L-SMALL-100K | 100,000 × 4 KiB，1000 目录 | 元数据与小文件 |
| L-MIXED-10K | 1 KiB–1 GiB 混合 | 通用负载 |
| L-LARGE-10 | 10 × 10 GiB | 顺序吞吐 |
| L-DEEP | 1000 层目录 | 深路径与栈安全 |
| TW-10K | 10,000 基线项，增删改冲突混合 | 双向算法 |
| R-SMALL-10K | 10,000 小文件 | 远程控制面 |
| R-LARGE-10 | 10 个大文件 | 远程吞吐 |

为大数据集提供生成脚本，生成器必须：

- 可指定随机种子；
- 使用稀疏文件选项生成大文件基准，另设真实写入模式；
- 输出 manifest 和内容 hash；
- 只清理自己创建的带 run-id 目录。

## 基准操作

每个数据集测试：

1. 首次比较；
2. 无变化第二次比较；
3. 修改 1% 文件后比较；
4. 执行同步；
5. 同步后无变化比较；
6. 中途取消；
7. 进程强制结束后恢复。

运行至少 3 次，报告中位数和最大值；远程场景同时记录 RTT。

## M0 验收

- 每次运行可以输出阶段耗时；
- 测试能断言 `DirectoryScans` 上限；
- 测试能断言默认比较 `HashBytes == 0`；
- 测试能断言单文件验证不会增加 `DirectoryScans`；
- 测试能断言 journal 写入量近似 `O(N)`；
- 所有计数器关闭时对端到端耗时影响低于 2%。

