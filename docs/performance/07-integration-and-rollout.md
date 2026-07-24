# 07：集成、兼容与上线

## 推荐实施顺序

每个步骤单独 PR：

1. 指标与基准；
2. `IEndpoint.StatAsync`；
3. 本地扫描取消默认 hash；
4. 单一 comparison snapshot；
5. freshness 单路径检查；
6. verifier 单文件/流式验证；
7. baseline 从结果更新；
8. journal WAL；
9. 有界执行 pipeline；
10. 设备感知预算；
11. rclone stat；
12. rclone batch。

顺序理由：前六项直接移除主要的重复 I/O；状态模块随后消除二次复杂度；调度和远程批处理
必须在数据流稳定后实施。

## Feature flags

过渡期使用应用级开发 flags，不向普通用户长期暴露：

```text
engine.snapshot-v2
engine.lazy-hash
engine.verifier-v2
engine.baseline-v2
engine.journal-wal
engine.device-scheduler
engine.rclone-batch
```

规则：

- 测试环境可以逐项组合；
- release 中按里程碑删除旧路径和对应 flag；
- 不允许长期同时维护两套 planner；
- 持久化格式 flag 关闭后仍要能读取已写的新格式。

## 配置迁移

Profile 增加：

```json
{
  "comparisonMode": "TimeAndSize",
  "strongVerification": false,
  "resourceConcurrency": {},
  "remoteBatching": true
}
```

迁移默认：

- 旧 Profile => `TimeAndSize`；
- 旧 `verifyCopies=true` 映射到普通验证；
- 若用户过去明确依赖 SHA-256 语义，需要提供一次性提示或迁移为 Content/strong；
- MaxConcurrentCopies 暂时作为所有资源的 override，后续版本再迁移为分资源预算。

## 正确性回归

必须覆盖：

- Mirror/Update/TwoWay；
- 首次双向、删除传播和双侧冲突；
- include/exclude；
- 时间容差；
- local-local、local-SFTP、local-Drive、local-S3、remote-remote；
- permanent/recycle/versioning；
- compare 后源变化；
- 覆盖目标变化；
- 磁盘空间不足；
- 取消、失败、崩溃和恢复；
- CLI JSON 与退出码；
- WPF 预览选择与进度。

## 性能回归门槛

CI 使用小型稳定数据集断言相对/结构指标：

- 每端扫描次数；
- hash 字节；
- stat 次数；
- journal 写入条数；
- baseline 扫描次数；
- RC 请求次数；
- 分配量上限。

真实耗时门槛放在专用性能环境，避免普通 CI 噪声：

- 无变化 100K 比较；
- 10K 小文件复制；
- 10 GiB 顺序复制；
- SFTP 100 ms RTT 小文件；
- Drive/S3 请求数量。

目标：

- 无变化默认比较不读取内容；
- 本地比较每端一次枚举；
- 10 万文件无 `O(N²)` 热点；
- 本地大文件吞吐不低于改造前；
- 远程 10K 小文件至少比改造前快 3 倍；
- journal 耗时低于总耗时 5%。

## 发布阶段

### Alpha

- 开发 flag 默认关闭；
- 并行运行旧/新 planner 做结果 diff，但只执行旧结果；
- 收集 snapshot 和 plan 差异，不上传数据。

### Beta

- 本地 local-local 默认新引擎；
- 远程保持旧单项 RC；
- strong verification 保持可选；
- 遇到 capability 不支持时结构化回退。

### Stable 1

- 全端点启用 snapshot/verifier/baseline/journal；
- 删除旧重复扫描路径；
- 保留旧 journal/baseline reader。

### Stable 2

- 启用设备调度和 rclone batching；
- 删除旧执行器；
- 清理 flags、适配层和过期文档。

## 回退

代码回退不能破坏已写状态：

- 新 journal/baseline reader 在至少两个正式版本内保留；
- 新 baseline 格式启用前完成前后版本互读测试；
- 若新执行器关闭，不能使用新 snapshot 继续旧计划；
- 发生未知状态时要求重新比较，不自动删除；
- rollback 不删除用户数据或未确认 temporary。

## 文档与代码审查清单

每个模块完成时更新：

- 接口契约；
- capability 表；
- 状态机；
- 复杂度；
- 指标名称；
- 失败与取消语义；
- migration；
- benchmark before/after；
- 已删除的旧代码路径。

## 项目完成定义

- 本目录 M0–M6 全部完成；
- 旧的全树验证、默认 hash、完整 journal 重写和 baseline 重扫代码已删除；
- FreeFileSync 工作区源码不再是构建或开发依赖；
- `references/FREEFILESYNC_REFERENCE.md` 足以解释所采用设计的来源；
- 所有正确性、恢复、UI、CLI 和性能门槛通过。

