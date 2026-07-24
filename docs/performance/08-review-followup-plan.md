# 08：评审问题收敛计划

## 背景

本计划收敛性能改造首轮代码评审发现的问题。优先恢复正确性和既定的性能不变量；在这些问题
关闭前，不应将 Snapshot V2、WAL 或设备调度视为可默认启用的完成状态。

## P1：默认运行路径仍会重复全树扫描

### 问题

`ProfileRunner.RunAsync` 已构建 `ComparisonSnapshot`，但仍调用旧 `SyncExecutor`。旧执行器的
新鲜度检查会扫描左右端点；其逐文件验证还会为每个复制操作再次扫描两端。因此默认批处理未
满足 M2 的“执行期禁止全树 `ScanAsync()`”约束。

### 改进步骤

1. 将 `ProfileRunner` 的默认执行接线切换到经过验证的 V2 执行路径。
2. 删除或隔离旧执行器中 `PlanFreshnessValidator.ValidateAsync` 和 `ContentVerifier` 的热路径用法；
   兼容调用必须显式标记为 legacy，并记录结构化诊断。
3. 统一 freshness 与验证的时间容差来源：使用比较快照和端点精度，而非在执行器内硬编码。
4. 新增端到端测试：含多文件复制的 `ProfileRunner.RunAsync` 只允许比较阶段各扫描一次，之后
   只允许选中源与目标的 `StatAsync` 调用。

### 验收

- 默认 Profile 运行中 `DirectoryScans == 2`（左右本地端点各一次，基线存储自身操作除外）；
- 单文件验证不会增加 `DirectoryScans`；
- 旧 UI/CLI 重试路径仍能运行，或明确迁移到同一快照执行 API。

## P1：基线提交使用同步前快照

### 问题

`CommitFromSnapshotAsync` 直接将 comparison snapshot 序列化为下一份 baseline。复制成功后，
目标端在快照中仍可能不存在或具有旧 metadata；下一次双向比较会把已同步的目标错误视为新增
或修改，破坏删除权威。

### 改进步骤

1. 按 `05-baseline-state.md` 定义 `BaselineCommitInput`，输入 comparison snapshot、旧 baseline、
   `OperationRunResult` 与事务状态。
2. 扩展执行结果，记录复制源前后 metadata、目标 publish 后 metadata、实际字节数和 publish 状态。
3. 按操作结果构造 next-state：复制成功时将两侧写为已验证的最终状态；删除成功时移除或写入
   明确的缺失状态；失败、取消、冲突、未选择和过滤路径保留旧 baseline。
4. 只有所有选中操作到达可提交终态时才发布新 session；否则保存 needs-recovery 事务并保持旧
   paired baseline。

### 验收

- “左侧存在、右侧缺失，复制成功”后，重新加载 baseline 时两侧状态相同；
- 随后的无变化双向比较产生零操作；
- 复制失败、取消和未选择操作不会把推测状态写入 baseline；
- 基线提交不调用 `ScanAsync()`。

## P1：WAL writer 生命周期导致事件丢失

### 问题

`JournalWalStore` 在构造函数启动 drain task，但事件流在 `BeginRunAsync` 后才打开。drain 因空流
立即返回，后续 append 只会写入无人消费的 channel，恢复文件没有事件。

### 改进步骤

1. 只在 `BeginRunAsync` 成功写入 header 并打开 events stream 后启动 writer；每个 run 只允许一个
   writer task。
2. `AppendAsync` 在未开始、已完成或 writer 故障时失败，而不是静默入队。
3. 为 `OperationCommitted`、`BaselineCommitted`、`RunCompleted` 提供 awaitable durability barrier：
   调用返回前确保对应事件已写入并 flush。
4. writer 的异常保存为 run fault；后续操作停止并转入 needs-recovery，不能继续假装 journal 可恢复。
5. `CompleteRunAsync` 必须先完成队列、drain/flush events，再原子发布 summary。

### 验收

- Begin → Append → Complete 后 events.jsonl 包含所有事件且 seq 单调；
- commit 边界后强制结束进程，恢复可重建正确的已提交状态；
- flush 故障会中止运行并保留可诊断信息；
- 任意尾行截断可忽略，非尾行损坏、seq 重复或跳跃必须报告为恢复错误。

## P2：V2 调度仍为全部操作创建 Task

### 问题

V2 的 `Task.WhenAll(selectedCopies.Select(...))` 会为每个复制项立即创建任务。资源预算只限制已
创建任务继续执行，无法满足大计划的有界内存与调度要求。

### 改进步骤

1. 以有界 `Channel<SyncOperation>` 替换全部任务创建；初始容量设为 256，并允许通过基准调整。
2. 使用固定数量 worker 消费 small/large copy 队列；worker 内取得 source/target 双资源租约。
3. producer 停止投递、worker 安全清理 temporary，并在取消时等待在途任务达到一致终态。
4. 目录创建先于依赖它的复制，删除队列仅在所有复制成功后开启。

### 验收

- 100,000 个复制操作不会创建 100,000 个等待 Task；
- 最大 in-flight 操作数不超过 channel 容量与 worker 数定义的上限；
- 同资源受预算限制，不同资源可并行，双资源获取无死锁；
- 任一复制失败时删除阶段不启动。

## P2：扫描计数器低报枚举数量

### 问题

同步 `LocalEndpoint.Scan()` 在枚举完成后仅增加一次 `EntriesEnumerated`，不反映实际返回条目数，
会使 M0 诊断和扫描上限测试失真。

### 改进步骤

1. 在每个实际纳入结果的条目处递增计数，确保同步与异步扫描使用同一口径。
2. 明确过滤、系统项、reparse point 与枚举异常是否计入“枚举到”或“纳入快照”，并为两个指标
   选择一致命名；若需要两种含义，新增独立 counter。
3. 将 metrics scope 绑定到一次 run，避免 `AsyncLocal` 在并行 worker 中意外创建未汇总实例。

### 验收

- 含文件、目录和被过滤项的固定树返回与定义一致的 `EntriesEnumerated`；
- 100,000 项数据集的计数不再为 1；
- 计数开启与关闭的端到端性能差异仍低于 M0 门槛。

## 建议实施顺序

1. 先为默认执行扫描次数、baseline 最终状态、WAL 写入/恢复补充失败测试。
2. 修复 WAL 生命周期与 baseline 结果驱动提交，阻断数据恢复和双向语义风险。
3. 接通 V2 默认执行路径，并以端到端计数测试验证不再重复扫描。
4. 将 V2 调度替换为有界流水线，最后修正 metrics 口径并更新基准报告。

每一项应独立提交；涉及持久化格式或默认路径切换的变更须保留旧格式读取能力和回滚诊断。
