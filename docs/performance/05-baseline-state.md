# 05：双向同步基线状态

## 目标

成功同步后直接从比较快照和操作结果更新 baseline，不再重新扫描左右端点；消除当前
`FirstOrDefault` 嵌套查找，并保留“两侧状态配对后才具有删除权威”的安全设计。

## 输入与输出

新增：

```csharp
public sealed record BaselineCommitInput(
    ComparisonSnapshot Snapshot,
    IReadOnlyDictionary<Guid, OperationRunResult> Results,
    BaselineTransaction Transaction);
```

输出仍为两端配对 session，但内容从已知状态推导。

## 状态更新算法

从旧 baseline 建立：

```csharp
Dictionary<string, BaselineEntry> next
```

按 comparison snapshot 和结果更新：

1. 两侧在比较时相同、且运行期间未涉及该路径：
   - 保留或写入当前相同状态；
2. 复制成功：
   - 目标状态由源的已验证最终 metadata 生成；
   - 左右状态更新为同步后的状态；
3. 删除成功：
   - 按最终两侧缺失状态更新/移除条目；
4. 操作失败或取消：
   - 保留旧 baseline，不使用推测状态；
5. 未选择或冲突：
   - 保留旧 baseline；
6. 被过滤路径：
   - 保留旧 baseline，过滤不是删除；
7. 两侧都确认不存在且无需保留删除历史：
   - 删除 baseline 条目。

执行器必须让 `OperationRunResult` 包含：

- source metadata before/after；
- target metadata after commit；
- actual bytes；
- digest（若计算）；
- publish 是否完成。

## 复杂度

目标：

```text
O(snapshot entries + baseline entries + operation results)
```

禁止：

- baseline 提交调用 `ScanAsync`；
- 对每个路径在 List 上调用 `FirstOrDefault`；
- 对每个条目单独打开 SQLite command/connection；
- 没变化仍发布相同数据库。

## 存储格式

第一阶段保持当前 schema/version，降低迁移风险，只改变数据来源和构建算法。

第二阶段评估：

- 直接二进制流而不是 JSON + gzip；
- string table/path prefix compression；
- SQLite transaction + prepared insert；
- state content hash，若内容未变化跳过发布。

不要在同一 PR 同时改变更新算法和格式。

## 两侧发布

保持配对 session：

1. 加载左右现有 archive；
2. 在本地内存生成 next state；
3. 序列化 lead/follower；
4. 写两个本地候选文件；
5. 完整性校验并验证能 join；
6. 并行上传两侧 temporary；
7. 两侧 candidate 均成功后发布；
8. journal 写 `BaselineCommitted`；
9. 清理旧 session/temporary。

如果只有一侧发布成功：

- 不把新 session 作为删除权威；
- 保留 transaction 为 NeedsRecovery；
- 下次加载只能选择左右共同 session；
- 禁止自动选择较新的孤立 session。

## 无操作运行

首次双向比较两侧完全相同时，仍需要建立 baseline：

- 使用 comparison snapshot 直接生成；
- 不重新扫描；
- 如果从比较到提交超过可配置时间，针对快照中的文件不适合逐项 stat；
- 默认把“无操作基线提交”放在比较流程结束立即执行，或要求运行命令重新比较一次。

现有“成功 no-op 建基线”行为必须保留。

## 过滤语义

对旧 baseline：

- 当前 filter 排除的条目保持原记录；
- filter 再次包含后重新参与比较；
- internal state 文件永远不进入普通 snapshot；
- filter 配置摘要记录在 snapshot 诊断中，不作为 endpoint identity。

## 测试

- 首次相同目录建立 baseline；
- 单向复制成功；
- 删除传播；
- 双侧修改冲突；
- 部分失败不更新失败路径；
- 用户取消；
- 过滤项保留；
- 一侧状态文件丢失；
- 孤立 session；
- 两侧发布间崩溃；
- 10 万和 100 万 baseline 条目复杂度；
- baseline commit 期间 `DirectoryScans` 不增长。

## 完成条件

- baseline commit 不访问普通目录树；
- 构建无嵌套线性路径查找；
- 失败路径保持旧删除权威；
- 两侧共同 session 规则不变；
- 相同内容不重复发布状态文件；
- 旧 baseline 可以无损读取。

