# FengSync 性能改进开发方案

## 决策与范围

本方案只改进现有 FengSync，不迁移到 FreeFileSync，也不以 FreeFileSync 建立新的产品分支。

保留：

- .NET 10、WPF、CLI 和现有解决方案结构；
- Profile、过滤、安全确认、计划任务、运行历史和恢复界面；
- `IEndpoint` 的端点抽象方向；
- rclone 的 SFTP、Google Drive、S3 认证和传输能力；
- 复制到临时文件后发布、复制完成后才删除、失败不提交基线等安全语义。

重构：

- 扫描与比较快照；
- 执行前新鲜度检查和复制验证；
- 复制调度；
- journal 持久化；
- 双向同步基线提交；
- 大批量远程操作。

FreeFileSync 仅作为算法和调度参考。需要的源码摘录已保存在
[`references/FREEFILESYNC_REFERENCE.md`](references/FREEFILESYNC_REFERENCE.md)，开发不依赖工作区
`FreeFileSync` 目录。

## 文档地图

按下列顺序开发：

1. [`01-observability-and-benchmarks.md`](01-observability-and-benchmarks.md)
   - 建立性能计数器、测试数据集和当前版本基线。
2. [`02-scan-and-comparison.md`](02-scan-and-comparison.md)
   - 取消默认全文件散列，建立单次扫描快照和单文件 `StatAsync`。
3. [`03-execution-and-scheduling.md`](03-execution-and-scheduling.md)
   - 消除执行期全树扫描，实现有界、设备感知调度和单文件验证。
4. [`04-journal-and-recovery.md`](04-journal-and-recovery.md)
   - 把反复重写完整 JSON 改为追加式 WAL。
5. [`05-baseline-state.md`](05-baseline-state.md)
   - 从执行结果增量提交双向基线，不再重新扫描。
6. [`06-rclone-batching.md`](06-rclone-batching.md)
   - 在本地内核稳定后，减少远程小文件逐项 RC 往返。
7. [`07-integration-and-rollout.md`](07-integration-and-rollout.md)
   - 集成顺序、兼容策略、回归矩阵和发布门槛。
8. [`08-review-followup-plan.md`](08-review-followup-plan.md)
   - 收敛首轮实现评审发现的执行接线、基线、WAL、调度和指标问题。

## 总体目标架构

```text
EndpointPair.Open
      │
      ├── 左端单次枚举 ──┐
      └── 右端单次枚举 ──┤
                          ▼
                 ComparisonSnapshot
                  │       │       │
                  │       │       └── baseline view
                  │       └────────── safety/capacity
                  └────────────────── plan
                          │
                  只检查选中路径 Stat
                          │
                  DeviceAwareScheduler
                          │
                  OperationResultSet
                    │             │
                    ▼             ▼
              append-only WAL   增量 baseline
```

强约束：

- 一次比较中每个端点最多进行一次全树枚举；
- 执行前、每文件验证和基线提交禁止调用全树 `ScanAsync()`；
- 默认比较不得读取普通文件内容；
- 操作数增加时不得出现 `O(N²)` 的扫描、查找、序列化或写盘；
- 所有性能优化必须保留现有删除保护、冲突处理和失败恢复语义。

## 建议项目结构

第一阶段不强制拆项目，先在 `FengSync.Core` 内完成止血改动。接口稳定后再拆：

```text
src/FengSync.Engine/
  Abstractions/
  Scanning/
  Comparison/
  Execution/
  State/

src/FengSync.Core/
  Profiles/
  Safety/
  History/
  Automation/
  EndpointAdapters/
```

拆分触发条件：

- `ComparisonSnapshot`、`IEndpoint.StatAsync` 和新执行器接口已稳定；
- 本地、rclone 和测试端点均通过同一契约测试；
- UI/CLI 不再直接依赖旧 `LocalEndpoint` 的具体类型。

## 跨模块开发规则

- 每个性能 PR 必须附前后基准结果；
- 先加计数器和失败测试，再改实现；
- 不在同一个 PR 同时更换扫描语义、执行调度和基线格式；
- 新旧持久化格式必须有向前迁移或兼容读取；
- 默认功能行为变化必须通过 Profile 配置版本迁移；
- 任何回退路径都必须产生结构化诊断，不能静默降级。

## 里程碑

| 里程碑 | 内容 | 退出条件 |
|---|---|---|
| M0 | 可观测性 | 能准确报告扫描次数、散列字节、RC 请求和 journal 写入 |
| M1 | 扫描止血 | 默认比较不读取文件内容，每端只枚举一次 |
| M2 | 执行止血 | 执行前和验证不做全树扫描 |
| M3 | 状态止血 | journal 与 baseline 不再出现二次复杂度 |
| M4 | 调度优化 | HDD/SSD/远程端点使用独立并发预算 |
| M5 | 远程批处理 | 大批量远程同步控制面请求显著下降 |
| M6 | 稳定发布 | 全量正确性、恢复、性能和 UI 测试通过 |
