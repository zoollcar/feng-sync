# 07 真实进度、错误详情与运行结果

## 实施目标

让用户在同步期间和完成后准确了解发生了什么，并能针对失败项行动，而不是只看到状态栏中的单条异常。

## 界面设计

- 运行窗口分为概要、当前传输、操作列表、错误四区。
- 概要展示进度百分比、字节、文件数、速度、剩余时间和耗时。
- 操作列表可按状态筛选，支持搜索路径、复制错误、打开源/目标目录。
- 完成页使用“成功/部分成功/失败/取消”四种结果，不以绿色对勾掩盖跳过和警告。
- 提供“重试失败项”“保存日志”“打开运行历史”“关闭”。
- 主界面 Profile 列表显示最后运行时间和结果徽标。

## 建议代码文件

- 新增 `src/FengSync/ViewModels/RunProgressViewModel.cs`。
- 新增 `src/FengSync/ViewModels/OperationResultViewModel.cs`。
- 重构 `src/FengSync/ProgressWindow.xaml` 为绑定式视图。
- 新增 `src/FengSync/Views/RunHistoryWindow.xaml`。
- 新增 `src/FengSync.Core/History/RunHistoryRepository.cs`。
- 新增 `src/FengSync.Core/History/RunLogEntry.cs`。
- 修改主窗口 Profile 行模板，显示最近状态。

## 功能流程

执行器发布节流后的进度事件，ViewModel 聚合速度和剩余时间。运行结束把摘要与逐项结果写入历史库。重试会生成新运行并引用原运行 ID，只选择可重试失败项。

## 验收标准

- 大量小文件时 UI 不因高频事件卡顿。
- 字节数、文件数与最终日志一致。
- 错误包含类别、路径、阶段、建议动作和底层信息。
- 历史可以按 Profile、日期和结果查询，保留策略生效。

