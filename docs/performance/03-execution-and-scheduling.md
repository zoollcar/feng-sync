# 03：执行、验证与设备感知调度

## 目标

替换当前“为所有复制创建 Task + 全局 Semaphore”的执行方式，消除执行期全树扫描，并根据
源/目标设备控制并发。

## 第一阶段：执行止血

### 新鲜度检查

`PlanFreshnessValidator` 只遍历选中的复制操作：

```text
for selected copy:
    current = source.StatAsync(path)
    expected = comparison snapshot source entry
    compare kind + size + modified time (+ stable id if available)
```

不检查未选择路径，不扫描目标端整棵树。并发 stat 使用小的有界并发，默认：

- Local：8；
- SFTP：4；
- Drive/S3：4；

最终由端点能力和 Profile 覆盖。

### 复制验证

普通验证：

- 复制前保存源 size/time；
- 写入结束得到实际写入字节数和目标属性；
- 目标 `StatAsync(path)`；
- 验证 size，能设置时间的端点同时验证 time；
- 源端再次 stat，确认复制期间未变化。

强验证：

- 读取源流复制时同步更新 hash；
- 目标端若能返回可信服务端 hash，比较兼容算法；
- 否则只对目标文件读取一次计算 hash；
- 不允许 `ContentVerifier` 调用 `ScanAsync`。

注意当前代码先把 temporary move 成正式路径再验证。新实现需要明确失败语义：

- 普通验证在 publish 前完成可完成的检查；
- 必须读正式目标才能验证时，失败应标记 NeedsRecovery，并保留/恢复旧目标；
- 覆盖已有目标时，结合版本策略或 backup name 保证可回滚。

## 第二阶段：有界流水线

不要为 N 个操作立即创建 N 个 Task。采用 `Channel<T>`：

```text
plan producer
  ├─ directory channel
  ├─ small-copy channel
  ├─ large-copy channel
  └─ delete channel（复制阶段成功后开启）
```

建议：

- channel capacity 256–2048，由基准决定；
- 小文件阈值初始 8 MiB；
- directory worker 1–4 个；
- delete worker 默认 1 个；
- 大文件和小文件各自保证公平性；
- 用户取消后停止领取新任务，正在写入的任务安全清理 temporary。

## 设备键

```csharp
public readonly record struct ResourceKey(
    ResourceKind Kind,
    string Identity);
```

本地：

- 第一版使用 volume root；
- 第二版使用 volume GUID；
- 若可判定物理磁盘，为同一物理磁盘上的多个 volume 共享预算。

远程：

- rclone remote + provider + account/host；
- 同一个 SFTP host 的多个 root 共享连接预算；
- 同一个 Drive remote 的多个目录共享 API 预算。

## 双资源租约

一次复制同时占用 source 和 target 预算：

```csharp
await resourceGovernor.AcquireAsync(
    new[] { source.ResourceKey, target.ResourceKey }
        .Distinct()
        .OrderBy(StableOrder),
    ct);
```

按固定顺序获取避免死锁。相同资源只获取一次。

默认预算建议：

| 资源 | 默认并发 |
|---|---:|
| 未知本地卷 | 1 |
| HDD | 1 |
| SSD | 2 |
| NVMe | 4 |
| SFTP | 4 |
| Google Drive | 4 |
| S3 | 8 |

第一版无需自动识别 HDD/SSD，可用保守默认 + Profile 配置；自动检测在基准证明有效后添加。

## 本地复制

第一阶段继续使用异步 FileStream，但显式配置：

- `FileOptions.Asynchronous | FileOptions.SequentialScan`；
- 合理 buffer（从 128 KiB 起基准）；
- 预分配目标长度；
- copy loop 上报真实字节进度；
- flush 策略由 durability 选项决定。

第二阶段评估 Windows 原生复制：

- `CopyFile2`/`CopyFileEx` 的吞吐、稀疏文件、取消和进度；
- 同卷/跨卷；
- ACL、ADS 和 timestamps；
- 长路径；
- 被占用文件。

只有端到端基准优于 FileStream 且语义可控时启用 native fast path，必须保留 FileStream
fallback。

## 路径依赖

调度器必须编码：

- 子文件复制依赖目标父目录存在；
- 同一路径不能同时 copy/delete/move；
- 目录删除依赖所有子项完成；
- 删除阶段依赖所有复制成功；
- baseline 提交依赖所有已选操作得到终态；
- versioning move 与目标 publish 不能竞争。

建议构造简单 dependency count，不需要通用 DAG 框架。

## 错误与重试

分类：

- transient：超时、429、部分 5xx、临时网络断开；
- conflict：目标存在、源变化、路径类型变化；
- permanent：权限、非法路径、容量不足；
- cancelled。

重试：

- 指数退避 + jitter；
- 本地共享冲突最多短时重试；
- 远程遵守 Retry-After；
- 已写 temporary 的任务重试前先确认并清理；
- 每次重试写 journal event，但不重写完整状态。

## 测试和完成条件

- freshness 只 stat 选中源；
- 每个验证只访问对应文件；
- 10 万操作不会创建 10 万并发 Task；
- HDD 并发 1 不发生多大文件寻道抖动；
- 两设备复制可以并行；
- 双资源获取无死锁；
- 复制失败后删除阶段不启动；
- 取消、崩溃和验证失败不留下可见半文件；
- 所有现有安全和恢复测试通过。

