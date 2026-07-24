# 04：追加式 Journal 与恢复

## 当前问题

`SyncExecutor.Mark()` 每次状态变化都保存包含所有操作的完整 `SyncJournal`，并在一个全局
锁内执行。文件数增加时产生 `O(N²)` 序列化和写入，并串行化复制完成路径。

## 新格式

目录建议：

```text
<app-data>/journals/
  <run-id>.header.json
  <run-id>.events.jsonl
  <run-id>.summary.json
```

### Header

只写一次：

```json
{
  "formatVersion": 2,
  "runId": "...",
  "createdUtc": "...",
  "profileId": "...",
  "leftIdentity": {},
  "rightIdentity": {},
  "snapshotId": "...",
  "operations": [
    { "id": "...", "path": "...", "kind": "...", "size": 0 }
  ]
}
```

### Event

每行一条：

```json
{"seq":1,"utc":"...","operationId":"...","state":"Started","temporaryPath":"..."}
{"seq":2,"utc":"...","operationId":"...","state":"Committed","bytes":123}
```

事件类型：

- `RunStarted`
- `OperationStarted`
- `TemporaryCreated`
- `OperationCommitted`
- `OperationFailed`
- `OperationCancelled`
- `BaselineStarted`
- `BaselineCommitted`
- `RunCompleted`

不要为纯 UI 进度写 journal。`Transferred` 只有能改变恢复决策时才持久化。

## Writer

单消费者 `Channel<JournalEvent>`：

- producer 只 enqueue，不等待磁盘（channel 满时除外）；
- 每 64 条或 100 ms flush；
- `Committed`、`BaselineCommitted`、`RunCompleted` 要求 durability barrier；
- Windows 上使用同一打开流顺序追加；
- sequence 在 writer 内分配，保证单调；
- flush 失败立即令运行进入失败/需要恢复状态，不能继续假装可恢复。

可配置：

```csharp
public sealed record JournalOptions(
    int BatchSize = 64,
    TimeSpan FlushInterval = default,
    JournalDurability Durability = JournalDurability.CommitBoundaries);
```

## 恢复重放

启动时：

1. 读取 header；
2. 逐行读取 events；
3. 遇到最后一行截断/无效 JSON，只忽略该尾行；
4. 验证 seq 单调和 operation ID 存在；
5. 构造每个操作的最终状态；
6. 检查 temporary 是否存在；
7. 生成 `RecoveryItem`。

恢复动作：

- 删除确定属于该 run 的 orphan temporary；
- 已 publish 但未记 committed 的项通过 stat/metadata 判断；
- 不自动重做删除；
- baseline 未 committed 时保持旧 baseline；
- 无法判断的项要求用户选择重新比较。

## 归并与保留

运行完成：

- 写 `summary.json.tmp`；
- 原子 move 为 `summary.json`；
- header/events 保留到 summary 校验成功；
- 后台按保留策略压缩或删除旧事件；
- 运行历史引用 summary，不依赖 events 永久存在。

## 兼容

- `TaskJournalStore` 先支持读取 v1 完整 JSON 和 v2 WAL；
- 新运行只写 v2；
- v1 恢复项处理完成后按现有逻辑移除；
- 不批量转换用户的旧未完成 journal。

## 测试

- 100,000 操作写入字节随 N 线性增长；
- producer 并发 enqueue 不丢事件；
- 尾行在任意字节截断；
- header 损坏；
- seq 重复/跳跃；
- flush 失败；
- commit event 前后分别强制结束进程；
- baseline commit 中途结束；
- events 完成但 summary 尚未发布；
- v1 兼容读取。

## 完成条件

- 热路径不再序列化完整操作集合；
- journal writer 是唯一文件写入者；
- committed 边界满足现有恢复保证；
- 10 万文件同步中 journal 时间低于总耗时 5%；
- UI 恢复窗口不需要了解 WAL 细节。

