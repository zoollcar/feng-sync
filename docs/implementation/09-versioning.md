# 09 删除、回收站与版本保留

## 实施目标

完整实现永久删除、Windows 回收站和版本目录三种策略，并让保留天数、版本数和容量限制真正执行。

## 界面设计

- Profile 版本管理页使用三张策略卡：永久删除、回收站、版本目录。
- 回收站仅在端点能力支持时可选；远端显示不支持原因。
- 版本目录可浏览选择，并即时检查是否位于同步树内。
- 保留策略支持天数、每文件版本数、总容量，显示下一次预计清理内容。
- 同步确认页显示本轮永久删除、移入回收站、归档的数量。
- 维护页提供“预览清理”和“立即清理”，默认先预览。

## 建议代码文件

- 新增 `src/FengSync.Core/Versioning/IDeletionStrategy.cs`。
- 新增 `PermanentDeleteStrategy.cs`、`RecycleBinStrategy.cs`、`ArchiveStrategy.cs`。
- 新增 `src/FengSync.Core/Versioning/RetentionPolicy.cs`。
- 新增 `src/FengSync.Core/Versioning/RetentionCleanupService.cs`。
- 新增 `src/FengSync.Core/Versioning/ArchivePathValidator.cs`。
- 新增 `src/FengSync/Views/VersioningSettingsControl.xaml`。
- 修改统一执行器和端点能力模型。

## 功能流程

规划阶段按端点能力验证策略；执行阶段通过策略接口处理文件与目录。归档先创建目标父目录再原子移动；跨卷时采用复制、校验、删除。清理服务在成功运行后或维护操作中执行，先生成候选清单并记录日志。

## 验收标准

- 选择回收站不会走永久删除。
- `KeepDays`、数量和容量限制均可验证生效。
- 归档目录嵌套同步树时不能保存。
- 文件、非空目录、同名版本、跨卷和清理失败均有确定行为。

