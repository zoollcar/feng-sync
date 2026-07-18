# 04 同步安全校验体系

## 实施目标

在比较前、计划生成后和执行前建立三层安全校验，阻止空源镜像、批量误删、目录嵌套、空间不足和计划过期。

## 界面设计

- 比较结果上方增加“安全检查”摘要条：通过、警告、阻断。
- 同步确认窗口展示复制、覆盖、删除数量与比例、传输量、空间需求。
- Profile 可设置最大删除数量和最大删除比例；超限默认阻断，可要求再次输入 Profile 名称确认一次性放行。
- 阻断项提供“查看受影响文件”和“返回设置”，不提供模糊的“仍然继续”。

## 建议代码文件

- 新增 `src/FengSync.Core/Safety/SafetyValidator.cs`：统一校验入口。
- 新增 `src/FengSync.Core/Safety/PathTopologyValidator.cs`：相同、嵌套、归档递归路径。
- 新增 `src/FengSync.Core/Safety/DeletionGuard.cs`：删除阈值与空源异常。
- 新增 `src/FengSync.Core/Safety/StorageCapacityChecker.cs`：可用空间估算。
- 新增 `src/FengSync.Core/Safety/PlanFreshnessValidator.cs`：执行前重验指纹。
- 新增 `src/FengSync/Views/SyncConfirmationWindow.xaml`：风险确认。
- 修改 `src/FengSync.Core/ProfileRunner.cs` 和主窗口同步流程：强制调用校验器。

## 功能流程

配置校验先检查路径拓扑；扫描后比较上次统计判断端点异常；计划生成后评估删除风险和空间；用户确认后、真正写入前复核来源指纹。计划过期则返回重新比较，不沿用旧动作。

## 验收标准

- 左右相同或互相包含时不能运行。
- 空源镜像、删除超阈值和空间不足能被准确拦截。
- 比较后修改源文件会使执行中止并要求重新比较。
- UI、批处理和 CLI 使用同一套规则。

