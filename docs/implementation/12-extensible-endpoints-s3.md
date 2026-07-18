# 12 可扩展端点与 S3 对象存储

## 实施目标

新增 AWS S3 和 S3 兼容对象存储，并把 Local、SFTP、Google Drive 的硬编码创建流程改为 provider 注册体系。以后新增 Azure Blob、OneDrive、WebDAV 等存储时，主要增加 provider、设置页和契约测试，不再修改主窗口大量条件分支。

## 首期范围

- 正式支持 Amazon S3。
- 支持自定义 S3-compatible endpoint，并提供 MinIO、Cloudflare R2、Backblaze B2 等预设。
- 支持 bucket 与 bucket 内 prefix 作为同步根。
- 支持列表、上传、下载、覆盖、删除、服务端复制和 multipart upload。
- 支持静态密钥、Session Token、环境/实例角色和 Windows 安全凭据引用。
- 不负责创建/删除 bucket、修改 bucket policy或开启公网访问。

## 界面设计

“添加云端端点”首屏采用 provider 卡片：Google Drive、SFTP、Amazon S3、S3 兼容存储。首期卡片和编辑器由编译期注册的 WPF UserControl 提供，不建设动态插件 UI 或通用表单 schema 引擎。

S3 基础页包含连接名称、预设、Endpoint URL、Region、Bucket、根前缀和凭据来源。凭据来源可选 Windows 凭据库、仅本次会话、环境/实例角色。提供“测试连接”“浏览 Bucket”“查看权限”。

高级页包含 path-style、TLS 证书信任、SSE-S3/SSE-KMS、KMS Key ID、storage class、分段阈值、part 大小、最大请求并发、超时和限速。

测试后展示 list/get/put/delete 权限、版本控制、服务端复制、可用 hash、时间精度和空目录语义。同步确认页提示大量请求、跨区域流量、归档恢复和删除成本。

## 建议代码文件

- 新增 `src/FengSync.Core/Endpoints/IEndpointProvider.cs`：provider 描述、验证、创建和能力探测接口。
- 新增 `src/FengSync.Core/Endpoints/EndpointProviderRegistry.cs`：provider 注册与查找。
- 新增 `src/FengSync.Core/Endpoints/EndpointDescriptor.cs`：稳定 type ID、显示名、图标和对应编辑器键；不承载通用表单语言。
- 扩展 `src/FengSync.Core/Endpoints/EndpointCapabilities.cs`：原子移动、服务端复制、hash、版本控制、对象恢复。
- 新增 `src/FengSync.Core/Endpoints/S3/S3EndpointSettings.cs`：非敏感设置。
- 新增 `S3EndpointProvider.cs`：设置验证、凭据解析与端点创建。
- 新增 `S3Endpoint.cs`：对象列表、传输、复制、删除和 metadata 映射。
- 新增 `S3CapabilityProbe.cs`：bucket 状态和实际权限探测。
- 新增 `S3MultipartTransfer.cs`：分段上传、恢复和清理。
- 新增 `S3ErrorClassifier.cs`：认证、Region、限流、归档等待等错误分类。
- 新增 `src/FengSync.Core/Security/CredentialReference.cs` 与 `SecretStore.cs`。
- 新增 `src/FengSync/Views/Endpoints/S3EndpointEditor.xaml` 及对应 ViewModel。
- 修改 `MainWindow.xaml.cs`：端点创建迁移到 registry，移除 URI 前缀硬编码。
- 修改 `Model.cs`：EndpointType 改为稳定字符串 type ID，新增 provider 不再修改公共枚举。

## 代码功能分层

Profile 只保存 provider type ID、非敏感设置和 CredentialReference。Provider 负责解析设置、取得 secret、创建端点并探测能力。规划器只读取统一快照和能力。执行器根据能力选择普通复制、服务端复制、分段传输和非原子重命名流程。

首期 registry 只注册随程序发布的内置 provider，不加载第三方 DLL。S3 可通过 rclone 后端实现，但 provider 模型和 Profile schema 不直接暴露 rclone remote。未来可增加原生 S3 传输实现，而不改变上层界面。

## 对象存储特殊规则

- 目录映射为 key prefix；空目录可忽略或使用 marker。
- 重命名是 copy + delete，journal 必须记录两个阶段和部分成功。
- ETag 仅在确认算法时作为 hash；multipart 和加密对象不可当作 MD5。
- 上传使用 staging key 或 multipart upload，成功后提交目标 key。
- 覆盖和删除尽量使用条件请求，防止计划生成后第三方更新对象。
- Glacier 类对象标记为等待恢复，允许发起恢复后稍后重试。
- S3 Versioning 与 Feng Sync 版本目录是两种策略，界面分别说明。

## 凭据与安全

- Secret、Session Token、KMS 信息不进入 Profile、日志、CLI 参数或诊断包。
- 删除 Profile 时询问是否删除无人引用的安全凭据。
- 默认只接受 HTTPS；自签名证书只能按指纹信任。
- 无 delete 权限时镜像模式被能力门控阻止。
- 日志中的 bucket、key 和账号可按程序设置脱敏。

## 运行与恢复流程

1. Provider 解析 Profile、获取 secret 并创建端点。
2. 探测能力和权限，执行前复核关键写权限。
3. 分页扫描对象并映射为统一快照。
4. 小文件上传 staging key；大文件创建 multipart upload 并逐 part 写 journal。
5. 校验后提交目标对象；重命名记录 copy/delete 两阶段。
6. 崩溃后继续 multipart 或清理孤儿 upload。
7. 按策略处理限流、网络中断、归档等待和永久权限错误。

## 测试计划

- 建立 provider 契约测试，同一组扫描、读写、删除、取消用例覆盖各端点。
- 本地 MinIO 覆盖普通和分段上传；隔离账号验证 AWS S3 及至少两个兼容服务。
- 覆盖分页、Unicode key、大小写、零字节对象、目录 marker、大对象、SSE 和版本控制。
- 故障注入覆盖 part 失败、限流、断网、重启以及 copy 成功但 delete 失败。

## 验收标准

- 用户可完全通过界面添加 AWS S3 和自定义 S3 端点，无需手改 rclone 配置。
- AWS S3、MinIO 及至少两个兼容服务完成基础同步契约。
- 凭据不出现在任何非安全存储或日志中。
- 无写/删除权限、Region/TLS 错误和归档对象均有准确提示与能力门控。
- 大文件上传中断后可继续或安全清理，不遗留不可见费用资源。
- 新增下一个 provider 时无需修改主窗口端点判断和同步规划器。
