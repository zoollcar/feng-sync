# 01 全局设置与 Profile 设置分层

## 实施目标

解决当前 Profile 设置与程序设置互相覆盖的问题。程序设置只定义应用行为和新 Profile 默认值；已存在 Profile 保存自身同步参数。运行时生成一次不可变的有效设置快照。

## 界面设计

- “工具 → 程序设置”包含常规、默认值、日志通知、连接性能、集成、维护六页。
- Profile 的“同步设置”独立打开，不再复用程序设置窗口。
- Profile 字段旁显示“使用程序默认值”复选框；取消后才能填写覆盖值。
- 设置底部提供“应用”“确定”“取消”“恢复本页默认值”。
- 首次读取旧配置时显示一次迁移结果；配置损坏时显示备份位置和修复选项。

## 建议代码文件

- 新增 `src/FengSync.Core/Configuration/ApplicationSettings.cs`：程序级设置模型。
- 新增 `src/FengSync.Core/Configuration/ProfileSettings.cs`：Profile 专属设置模型。
- 新增 `src/FengSync.Core/Configuration/EffectiveProfileSettings.cs`：合并默认值与覆盖值。
- 新增 `src/FengSync.Core/Configuration/ConfigurationValidator.cs`：范围、路径和组合校验。
- 新增 `src/FengSync.Core/Configuration/ConfigurationMigrator.cs`：schema 版本迁移。
- 新增 `src/FengSync.Core/Configuration/SettingsStore.cs`：原子读写、备份、损坏恢复。
- 修改 `src/FengSync.Core/Model.cs`：让 `SyncProfile` 持有 ProfileSettings。
- 修改 `src/FengSync/MainWindow.xaml.cs`：禁止再用 `_settings` 承载当前 Profile 状态。

## 功能流程

1. 启动时加载程序设置，验证并迁移。
2. 加载 Profile 时只填充 Profile 编辑状态，不修改程序设置。
3. 运行前由配置解析器生成 `EffectiveProfileSettings`。
4. 保存程序设置不会修改现有 Profile；修改默认值只影响新建 Profile 或仍选择继承的字段。
5. 写入失败时保留旧文件并向用户报告，不静默重置。

## 验收标准

- 切换 Profile 不会改变程序设置。
- 两个 Profile 可拥有不同并发数、过滤器和版本策略。
- 旧版配置可迁移且有备份；损坏文件不会导致全部 Profile 静默丢失。
- 取消设置窗口后，内存和磁盘配置均不变化。

