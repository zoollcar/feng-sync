# Feng Sync 首批实施设计索引

本目录将总方案中的 12 个首批开发任务展开到界面、代码文件、代码职责和验收层面，不包含具体实现代码。

整体重构边界另见 [架构抽象层审查](../ARCHITECTURE_ABSTRACTION_REVIEW.md)，其中明确哪些部分必须抽象、哪些只做轻量策略化，以及当前不应建设的通用框架。

1. [全局设置与 Profile 设置分层](01-settings-split.md)
2. [Profile 设置窗口 MVP](02-profile-editor.md)
3. [未完成功能的能力门控](03-feature-gating.md)
4. [同步安全校验体系](04-safety-validation.md)
5. [统一同步执行器](05-unified-execution.md)
6. [基线事务与崩溃一致性](06-baseline-transaction.md)
7. [真实进度、错误详情与运行结果](07-result-ui.md)
8. [过滤器编辑器](08-filter-editor.md)
9. [删除、回收站与版本保留](09-versioning.md)
10. [CLI、计划任务与受控批处理](10-automation.md)
11. [内置 SFTP 服务器](11-built-in-sftp-server.md)
12. [可扩展端点与 S3 对象存储](12-extensible-endpoints-s3.md)
