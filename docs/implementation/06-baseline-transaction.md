# 06 基线事务与崩溃一致性

## 实施目标

确保双向基线只反映真正成功提交的文件状态，避免部分失败或崩溃后把未同步项目误认为已同步。

## 界面设计

- 运行结果显示“基线已提交”“基线未变更”或“需要恢复”。
- 启动发现未完成事务时打开恢复向导：查看详情、继续安全操作、放弃临时文件。
- 不允许用户直接删除基线；维护页提供有风险说明的“重建基线”。

## 建议代码文件

- 新增 `src/FengSync.Core/Baseline/BaselineTransaction.cs`：事务生命周期。
- 新增 `src/FengSync.Core/Baseline/BaselineRepository.cs`：读取、暂存、提交与回滚。
- 新增 `src/FengSync.Core/Baseline/EndpointIdentity.cs`：稳定标识端点和根目录。
- 新增 `src/FengSync.Core/Recovery/RecoveryCoordinator.cs`：journal 与基线恢复决策。
- 修改 `src/FengSync.Core/BaselineStore.cs`：迁移为 repository 或兼容适配器。
- 修改统一执行器：逐项提交状态，整轮成功后原子替换基线。
- 新增 `src/FengSync/Views/RecoveryWindow.xaml`：恢复向导。

## 功能流程

运行开始记录基线版本和计划 ID；每项文件提交后记录新指纹，但不覆盖正式基线。所有必要操作成功后生成候选基线，通过原子替换提交。失败或取消时保留旧基线与 journal，下一次启动由恢复协调器判断可重试项目。

## 验收标准

- 任意操作失败、取消、断电模拟后，正式基线不包含未成功状态。
- 已成功文件不会因恢复而重复产生危险操作。
- 端点身份变化、单侧基线缺失或版本不匹配会阻断双向同步。
- journal 和基线提交顺序有自动化故障注入测试。

