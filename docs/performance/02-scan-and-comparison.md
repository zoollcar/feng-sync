# 02：扫描与比较快照

## 当前问题

`LocalEndpoint.Scan()` 和 `ScanAsync()` 为每个文件同步计算 SHA-256。`PrepareAsync`、
`PlanSnapshot.CaptureAsync`、`PlanFreshnessValidator` 和 baseline 提交又分别扫描。

本模块负责：

- 默认扫描只读取元数据；
- 一次比较只产生一份快照；
- 提供单路径 stat；
- 散列改为惰性能力；
- 保持过滤、内部文件排除和时间容差语义。

## 数据模型

### `FileIdentity`

```csharp
public readonly record struct FileIdentity(
    string? StableId,
    long Size,
    DateTimeOffset ModifiedUtc);
```

本地稳定 ID 第一版可以为空，不阻塞止血改动。后续可用 Windows
`FILE_ID_INFO` 获取 volume serial + file id，用于移动检测和 hash 缓存。

### `EntryFingerprint`

替换当前把 hash 当作常规字段的概念：

```csharp
public sealed record EntryFingerprint(
    long Size,
    DateTimeOffset ModifiedUtc,
    string? StableId = null,
    ContentDigest? Digest = null);
```

`Digest == null` 表示未计算，不能表示内容不可信。

### `EndpointSnapshot`

```csharp
public sealed class EndpointSnapshot
{
    public required EndpointIdentity Endpoint { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required IReadOnlyList<EntrySnapshot> Entries { get; init; }
    public required IReadOnlyDictionary<string, EntrySnapshot> ByPath { get; init; }
}
```

构造时一次性建立 `ByPath`。所有后续算法禁止在 entries 上对路径做 `FirstOrDefault`。

### `ComparisonSnapshot`

包含：

- 左右 `EndpointSnapshot`；
- 已加载 baseline；
- 比较策略和端点时间精度；
- 过滤器版本/摘要；
- snapshot ID；
- 生成的 `SyncPlan`。

## 接口变更

给 `IEndpoint` 增加：

```csharp
Task<EntrySnapshot?> StatAsync(
    string relativePath,
    CancellationToken cancellationToken = default);

Task<ContentDigest> HashAsync(
    string relativePath,
    HashAlgorithmId algorithm,
    IProgress<long>? progress = null,
    CancellationToken cancellationToken = default);
```

`HashAsync` 可以先作为可选接口 `IContentHashEndpoint`，避免所有远程后端伪实现。

扫描 API 改为返回 `EndpointSnapshot`，过渡期保留旧 API 的适配器：

```csharp
Task<EndpointSnapshot> CaptureSnapshotAsync(
    ScanOptions options,
    IProgress<ScanProgress>? progress,
    CancellationToken ct);
```

## 本地扫描实现

第一版：

- 删除 `LocalEndpoint` 扫描循环中的 `Hash(path)`；
- 每个文件只读取 path、attributes、size、last-write-time；
- 保留 `SyncInternalPaths.IsExcludedFromScan`；
- reparse point 行为保持现状；
- 使用 `EnumerationOptions` 明确 inaccessible、attributes 和 recursion 行为；
- 捕获单项错误并通过统一 scan diagnostics 返回，不能悄悄漏项。

第二版：

- 使用 `System.IO.Enumeration.FileSystemEnumerable<T>` 一次枚举返回所需字段；
- 必要时用 Win32 `FindFirstFileExW/FindNextFileW` 获取稳定且低分配的枚举；
- 路径规范化只执行一次；
- 评估字符串池/相对路径分段存储，仅在百万文件基准显示必要时实施。

## 比较策略

新增：

```csharp
public enum ComparisonMode
{
    TimeAndSize,
    SizeOnly,
    Content
}
```

默认 `TimeAndSize`：

1. entry kind 不同 => different；
2. 文件大小不同 => different；
3. 修改时间在 `max(left precision, right precision, user tolerance)` 内 => equal；
4. 时间不同 => modified/newer，交给同步模式判定；
5. 同时间不同大小 => conflict/invalid timestamp；
6. 端点声明时间不可靠时，可对候选项惰性 hash。

`Content` 模式只对两侧均存在且大小相同的文件做流式内容比较；大小不同直接 different。

## 单次快照接线

`PreparedProfileRun` 新增：

```csharp
EndpointSnapshot LeftSnapshot
EndpointSnapshot RightSnapshot
ComparisonSnapshot Comparison
```

`ProfileRunner.PrepareAsync` 扫描一次并传给 planner。`RunAsync` 不再调用
`PlanSnapshot.CaptureAsync`；它从 `ComparisonSnapshot` 选择源 fingerprint。

`PlanSnapshot` 可在兼容期保留名称，但其 `Capture` 必须是纯内存操作。

## Hash 缓存

第二阶段实现 SQLite 缓存：

```text
endpoint_key
stable_id_or_path
size
modified_utc
algorithm
digest
verified_utc
```

失效规则：

- size 或 modified time 变化立即失效；
- 有 stable ID 时 rename 不失效；
- 无 stable ID 时路径变化视为失效；
- 缓存损坏只导致重新计算，不阻断同步；
- 缓存不参与删除权威判断。

## 测试

- 默认扫描 hash 调用次数为 0；
- Content 模式只 hash 候选文件；
- 时间容差边界；
- 同时间不同大小；
- 大小相同时间不同；
- 大小和时间相同但内容不同（默认与 Content 模式分别断言）；
- inaccessible、reparse point、长路径、大小写碰撞；
- 扫描期间文件删除/修改；
- 取消响应；
- 两端各只扫描一次。

## 完成条件

- `PrepareAsync` 之后到运行结束，常规路径不再全树扫描；
- 无变化 100,000 文件比较不读取文件内容；
- planner 和 safety 只消费快照；
- 旧 Profile 默认迁移为 `TimeAndSize`；
- UI 显示当前比较方式并允许选择 Content。

