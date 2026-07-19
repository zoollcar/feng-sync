# 10 CLI、计划任务与受控批处理

## 实施目标

让 Profile 可以可靠地无人值守运行，并对批处理并发、确认策略、退出码、日志和失败隔离进行治理。

## 界面设计

- Profile 设置增加“自动化”页：计划运行、启动时运行、无人值守风险策略。（实时监控功能已移除，参见 #8）
- 提供 Windows 计划任务向导：频率、时间、网络/电源条件、运行账户、测试运行。
- 批处理窗口展示队列、运行中数量、每项结果和全局停止/继续策略。
- 程序设置提供全局最大同时运行 Profile 数、网络/磁盘并发上限。
- 对 S3 等计费端点增加预计请求量、归档恢复、跨区域流量提示和单次成本保护阈值。
- 导出命令时显示可复制 CLI 命令，但不把密码放入参数。

## 建议代码文件

- 新增 `src/FengSync.Cli/` 项目：正式命令行入口。
- 新增 `src/FengSync.Core/Automation/AutomationRunner.cs`。
- 新增 `src/FengSync.Core/Automation/BatchScheduler.cs`：受控队列和资源限制。
- 新增 `src/FengSync.Core/Automation/ExitCode.cs`：稳定退出码。
- 新增 `src/FengSync.Windows/TaskSchedulerService.cs`：Windows 任务计划集成。
- 新增 `src/FengSync/Views/ScheduleWizard.xaml` 与 `BatchRunWindow.xaml`。
- 修改 `BatchRunner.cs`：取消无上限 `Task.WhenAll`。
- ~~接入现有 `RealtimeMonitor.cs`，增加运行中变更队列和循环抑制~~。该功能已随 Issue #8 移除，相关源文件已删除。

## 功能流程

CLI 与 UI 都调用同一个 AutomationRunner。计划任务只保存 Profile ID/文件路径和非敏感参数。BatchScheduler 按全局资源限制调度，单个 Profile 失败不丢失其他结果。无人值守遇到确认风险时按明确策略中止并返回退出码。

## 验收标准

- CLI compare/run 有稳定 JSON 输出和成功、警告、失败、冲突、配置错误退出码。
- 计划任务可创建、修改、测试和删除，不遗留孤儿任务。
- 批处理不会无界并发启动 rclone 或磁盘任务。
- 无人值守模式绝不绕过删除阈值或能力门控。
