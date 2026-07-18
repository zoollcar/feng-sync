# Feng Sync 抽象层审查：需要什么，以及暂时不需要什么

## 1. 判断原则

抽象不是为了让类更多，而是隔离已经存在或近期确定会出现的变化。Feng Sync 采用以下门槛：

1. 已存在两套重复实现，并且行为正在分叉；
2. 已确定存在三种以上实现，或下一阶段明确会增加实现；
3. 该边界涉及数据安全、凭据或事务，需要强制所有入口遵守同一规则；
4. 单元测试若不隔离该边界，就必须依赖磁盘、网络或 WPF。

不满足其中任一项时，优先使用普通类、私有方法或组合，而不是先创建接口。

## 2. 必须建立的抽象

### 2.1 端点 Provider 与端点能力

**结论：必须，优先级 P0/P1。**

当前主窗口通过 URI 前缀和 `EndpointType` 枚举判断 Local、SFTP、Google Drive；执行器又用 `is LocalEndpoint`/`is RcloneEndpoint` 分支。增加 S3 后分支会快速扩散。

建议边界：

- `IEndpointProvider`：验证设置、创建端点、测试连接、探测能力；
- `IEndpoint`：扫描和基础文件/对象操作；
- `EndpointCapabilities`：表达 hash、原子移动、服务端复制、版本控制、空目录等差异；
- `EndpointProviderRegistry`：编译期注册 provider。

控制复杂度：首期**不做动态插件加载、不设计通用表单 schema 引擎**。Registry 只是应用启动时注册内置 provider；每种复杂端点使用自己的 WPF 编辑控件。等第三方插件需求真实出现后再稳定插件 ABI。

### 2.2 同步工作流协调器

**结论：必须，优先级 P0。**

当前 UI 同步、`ProfileRunner`、`EndpointSynchronizer` 和云端批处理分别拼接扫描、规划、执行、基线提交，安全校验很容易只接入其中一个入口。

新增一个具体类 `SyncOrchestrator`，统一执行：

`解析设置 → 创建端点 → 能力校验 → 扫描 → 规划 → 安全校验 → 执行 → 基线提交 → 记录结果`

所有 UI、批处理和 CLI 必须调用它。首期它可以是具体类并通过构造函数接收少量依赖；不需要为每一步建立接口或工作流 DSL。

### 2.3 统一执行管线

**结论：必须，优先级 P0。**

`LocalExecutor` 和 `EndpointExecutor` 已经是重复实现，且 journal、校验和版本策略行为不同。应收敛为 `SyncExecutor`，内部按固定阶段执行：准备、传输、校验、提交、删除/版本处理。

需要抽象的只有真正因端点变化的操作，例如传输和提交能力；并发调度、journal、结果、取消、重试和进度保持为共享实现。不要把每个阶段拆成可替换接口，普通内部类/方法足够。

### 2.4 配置与持久化边界

**结论：必须，优先级 P0。**

程序设置、Profile、运行历史、journal 和基线有不同生命周期与一致性要求，不能继续由窗口直接调用 `File.*` 和 `JsonSerializer`。

建议保留四个明确存储类：

- `SettingsStore`：程序设置、迁移、备份；
- `ProfileStore`：Profile 集合和导入导出；
- `RunHistoryStore`：运行历史与保留策略；
- `BaselineStore`/`JournalStore`：同步事务状态。

控制复杂度：不创建 `IRepository<T>`、Unit of Work 或通用 CRUD 框架。这些数据的事务规则不同，强行统一会隐藏关键行为。只有测试确实需要替换磁盘时，才给具体 store 增加窄接口。

### 2.5 凭据存储

**结论：必须，优先级 P0。**

Google OAuth、SFTP 密码/密钥、S3 Secret 和内置 SFTP 账号都需要一致的敏感信息边界。

- `CredentialReference` 进入 Profile，只保存标识和来源；
- `SecretStore` 负责 Windows Credential Manager/DPAPI；
- provider 只能按引用临时取得 secret；
- 日志和诊断包统一经过敏感字段清理。

这是安全边界，应有接口以便测试使用内存实现。不要建立自定义加密协议或“万能密钥管理平台”。Windows 首版只支持系统安全存储。

### 2.6 结果、进度与错误模型

**结论：必须，优先级 P0/P1。**

目前进度是字符串，失败主要靠异常，无法支撑重试、历史和自动化退出码。建立共享数据模型：

- `TransferProgress`：阶段、路径、字节、总量；
- `OperationResult`：成功、跳过、失败、取消；
- `SyncRunResult`：汇总和逐项结果；
- `SyncError`：类别、阶段、是否可重试、用户建议。

它们是普通不可变模型，不需要访问者模式、事件总线或复杂响应式框架。进度继续使用 `IProgress<T>` 即可。

## 3. 只做轻量抽象的部分

### 3.1 同步模式规划

`TwoWay` 与单向模式已有明显不同算法，但目前模式数量固定。保持 `ModePlanner` 作为入口，将 TwoWay 和 Custom 的复杂规则拆为具体 planner 类即可。暂不需要公开 `ISyncPlanner` provider 系统；只有未来允许第三方同步算法时再考虑。

### 3.2 删除与版本策略

永久删除、回收站、版本目录确实有不同执行行为，可使用三个策略类并由一个 `VersioningService` 选择。接口可以是包内窄接口，但不需要策略注册中心，也不需要让用户插件注入删除逻辑。

### 3.3 安全校验

路径拓扑、删除阈值、空间和计划新鲜度应由一个 `SafetyValidator` 聚合若干具体校验器。校验结果使用统一模型。初期不构建规则引擎、表达式语言或动态规则注册。

### 3.4 认证方式

SFTP 密码、公钥，S3 静态密钥、临时令牌和实例角色可用小型 discriminated model/枚举加配置对象表达。不要为每种凭据创建完整服务层；具体 provider 负责协议认证，`SecretStore` 只负责敏感数据。

## 4. 暂时不要抽象的部分

### 4.1 不做通用插件平台

近期端点均随 Feng Sync 发布。动态 DLL 发现、版本协商、沙箱、插件市场和第三方 UI 加载会显著扩大兼容与安全面。当前只做编译期 provider registry。

### 4.2 不引入事件总线

主窗口、进度窗口和服务状态用 ViewModel、命令、`IProgress<T>` 与少量 .NET 事件即可。事件总线会让同步生命周期和错误路径难以追踪。

### 4.3 不做通用仓储与通用序列化框架

Settings、Profile、Baseline、Journal、History 语义不同。保留具体 store 和共享的原子文件写入辅助类即可。

### 4.4 不把每个类都接口化

`ModePlanner`、`SafetyValidator`、`ConfigurationMigrator`、错误分类器等若只有一个实现，保持具体类。需要测试替身时优先传入纯数据或拆出实际 I/O 边界。

### 4.5 不立即拆成大量项目或进程

建议近期只保留：

- `FengSync.Core`：业务模型与同步核心；
- `FengSync`：WPF 应用；
- `FengSync.Cli`：自动化入口（实施 CLI 时新增）；
- `FengSync.Tests`。

内置 SFTP 可先作为 Core 之外的命名空间/文件夹；只有依赖冲突、Windows Service 或独立部署成为现实需求时再拆 `FengSync.SftpServer` 项目。每个云端 provider 不单独建程序集。

### 4.6 不使用泛化的“设置 schema UI”

Local 和简单端点可共享控件；SFTP、Google Drive、S3 使用各自编辑 View/UserControl。共享字段通过组合控件复用，不建立运行时表单描述语言。

## 5. 推荐的最小架构

```text
WPF / CLI
    |
SyncOrchestrator
    |-- Settings/Profile resolution
    |-- EndpointProviderRegistry -> IEndpoint + capabilities
    |-- ModePlanner
    |-- SafetyValidator
    |-- SyncExecutor
    |      |-- JournalStore
    |      |-- VersioningService
    |      `-- TransferProgress / SyncRunResult
    |-- BaselineStore
    `-- RunHistoryStore

SecretStore <--- CredentialReference <--- Endpoint Provider
```

依赖方向保持单向：UI/CLI 依赖 Core；Core 不依赖 WPF；provider 不操作窗口；store 不包含业务规划；planner 不访问网络或磁盘。

## 6. 分步落地顺序

1. 先建立共享结果模型和 `SyncOrchestrator`，把 UI、本地批处理、云端批处理切到同一入口。
2. 合并两个 executor，确保 journal、校验、进度和基线事务一致。
3. 分离 Settings/Profile store 与 SecretStore，移除窗口中的直接文件读写。
4. 引入编译期 EndpointProviderRegistry，迁移 Local、SFTP、Google Drive。
5. 在稳定 provider 契约后实现 S3，借此验证抽象是否足够；不要提前为未知端点添加扩展点。
6. 最后拆 ViewModel 和设置页面；只为已经稳定的业务服务补接口和测试替身。

## 7. 判定抽象是否成功的验收标准

- 新增 S3 时不修改主窗口端点类型判断、ModePlanner 或同步工作流顺序。
- UI、批处理、CLI 对同一 Profile 得到相同安全检查、执行结果和基线行为。
- 本地与远端不再使用两套 executor。
- Profile JSON 不含任何 secret；日志模型能统一脱敏。
- 单元测试可用内存端点和内存 SecretStore 覆盖同步主流程，无需启动 WPF/rclone。
- 核心接口数量保持有限；每个接口至少有两个真实实现，或明确隔离一个 I/O/安全边界。

