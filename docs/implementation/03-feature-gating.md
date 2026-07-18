# 03 未完成功能的能力门控

## 实施目标

防止用户选择当前代码不能安全兑现的远端双向、Custom 和 RecycleBin，避免界面承诺与执行行为不一致。

## 界面设计

- 不可用选项保留可见但禁用，旁边显示原因与预计支持方向。
- 选择远端端点后，若双向基线尚不支持，模式自动保持原值并弹出非阻塞说明。
- 打开旧 Profile 遇到未支持组合时显示“需要修复”，比较和运行按钮禁用。
- Profile 列表为不可运行项显示警告徽标；鼠标悬停显示具体原因。

## 建议代码文件

- 新增 `src/FengSync.Core/Capabilities/FeatureCapabilityService.cs`：根据端点和版本计算能力。
- 新增 `src/FengSync.Core/Capabilities/ProfileCompatibilityResult.cs`：阻断项与警告项。
- 新增 `src/FengSync/ViewModels/FeatureOptionViewModel.cs`：选项启用状态和说明。
- 修改 `src/FengSync/Views/ProfileEditorWindow.xaml`：绑定能力状态。
- 修改 `src/FengSync/MainWindow.xaml`：主界面模式入口同步门控。
- 修改 `src/FengSync.Core/ProfileRunner.cs`：执行层再次校验，不能只依赖 UI。

## 功能流程

端点或设置变化后重新计算能力。S3 还需按实际账号动态探测 list/get/put/delete、版本控制和服务端复制能力。界面负责解释，核心运行器负责最终拒绝。导入旧配置不擅自改写。后续新增存储只注册 provider，不在多个窗口散落协议判断。

## 验收标准

- 任何入口均不能运行远端双向、伪 Custom 或伪回收站。
- batch、CLI 和 UI 获得相同的阻断结果与错误代码。
- 未支持配置不会被静默降级为其他模式或永久删除。
