# Feng Sync UI 一致性计划

状态：提案  
范围：`src/FengSync/` WPF 桌面应用  
基准：Windows Fluent Design、.NET 10 WPF Fluent theme

## 1. 决策

Feng Sync 采用“平台标准库 + 应用设计系统”的两层方案，不另造一套完整控件库，也不立即引入第三方 WPF 主题框架。

- 平台层：继续使用 .NET WPF 内置 Fluent theme、`ThemeMode="System"`、系统强调色、标准 WPF 控件、UI Automation 和键盘行为。
- 图标层：继续使用 `FluentIcons.Wpf`，禁止重新引入零散 PNG、Emoji 或字符图标来表示产品命令。
- 应用层：由 Feng Sync 定义语义化设计令牌、按钮层级、排版、表单、状态、页面布局和业务复合控件。
- 例外层：只有在内置控件确实不能满足需求时，才创建自定义控件；引入新的 UI 包必须先写清缺口、可访问性、维护成本和退出方案。

这样既获得 Fluent 的原生视觉、深浅色、强调色和可访问性行为，也保留文件同步产品特有的危险操作、端点选择、变更预览和安全确认表达。

## 2. 当前基线

截至本计划建立时：

- `App.xaml` 已设置 `ThemeMode="System"`。
- 已存在 `Themes/DesignTokens.xaml`、`Typography.xaml`、`Icons.xaml` 和 `Controls.xaml`。
- 15 个非主题 XAML 文件中约有 83 个按钮；其中显式使用 Primary、Secondary、Ghost 样式的分别约为 11、11、24 个，其余主要依赖隐式默认或局部属性。
- 页面中没有发现直接写入的十六进制前景色或背景色，这是好的基础。
- 仍约有 29 处局部字号、241 处局部 Margin 和 28 处局部 Padding；部分是合理布局，部分应提升为语义令牌或公共布局样式。
- 旧按钮样式（如 Toolbar、PrimaryAction）和新语义样式并存；主按钮高度又被部分页面覆盖为 32/36，说明组件契约尚未稳定。

本计划不要求消灭所有数字。仅重复出现、表达同一语义、影响主题或组件状态的值必须令牌化；窗口尺寸、网格列宽和一次性布局可保留在页面内。

## 3. 设计系统结构

主题资源按以下职责拆分，加载顺序固定：

1. `DesignTokens.xaml`：原始尺度和语义颜色，不包含控件模板。
2. `Typography.xaml`：Fluent 字体梯度和文本语义。
3. `Icons.xaml`：图标尺寸、颜色和统一呈现方式。
4. `Controls.xaml`：Button、TextBox、ComboBox、CheckBox、DataGrid 等基础组件。
5. `Patterns.xaml`：对话框页脚、表单行、空状态、Callout、状态 Chip 等组合模式。

令牌命名采用“类别 + 语义 + 状态”，例如：

- `Spacing.ControlGap`、`Spacing.SectionGap`、`Spacing.PageHorizontal`
- `Color.Foreground.Primary`、`Color.Fill.Accent.Hover`
- `Size.Control.Default`、`Size.Control.Large`
- `Radius.Control`、`Radius.Surface`
- `Typography.Body`、`Typography.Caption`、`Typography.Title`

迁移期间保留旧 Key 的兼容别名；完成全部页面迁移后再删除，避免一次大规模替换。

## 4. 按钮标准

每个可见区域原则上只有一个主按钮。按钮的视觉层级由操作重要性决定，不由开发顺序或按钮位置决定。

| 类型 | 用途 | 外观与尺寸 | 典型示例 |
| --- | --- | --- | --- |
| Primary | 当前页面最主要、用户可安全继续的操作 | Accent 填充；默认高 32，大型工作区 CTA 高 40；Semibold | “开始同步”“保存”“下载并更新” |
| Secondary | 支持主流程、可与主按钮并列 | 中性表面、细边框；高 32 | “比较”“测试连接”“应用” |
| Tertiary | 低优先级、工具栏或行内命令 | 透明背景，无常驻边框；高 32 | “编辑”“导出”“稍后提醒” |
| Icon | 空间受限且图标含义稳定的命令 | 32×32；必须有 Tooltip 和 Automation Name | 交换端点、更多操作 |
| Destructive | 删除、覆盖、跳过版本等破坏性操作 | 默认中性或文字危险色；只有最终确认才使用危险填充 | “删除 Profile”“确认删除” |

共同规则：

- 所有按钮必须覆盖 Normal、Hover、Pressed、Keyboard Focus、Disabled 五种状态。
- Loading 是业务状态：按钮禁用，保留原宽度，并在文字旁显示进度反馈；不得只把文字清空。
- 对话框主操作位于右侧；同组按钮间距 8 DIP。危险确认不能只通过颜色表达，必须有明确动词和说明。
- `IsDefault` 只给主操作，`IsCancel` 给取消；不可执行或未解决冲突时主按钮保持禁用。
- 仅图标按钮必须设置 `ToolTip` 和 `AutomationProperties.Name`；图标与文字组合时使用统一 16/20 DIP 图标尺度。
- 不允许页面覆写公共按钮的 Background、Foreground、BorderBrush、Padding、FontSize 或状态 Trigger。
- 页面可通过明确的 `Compact` / `Large` 变体选择尺寸，不直接覆写 Height。

建议最终公共 Key：

- `Button.Primary`
- `Button.Primary.Large`
- `Button.Secondary`
- `Button.Tertiary`
- `Button.Icon`
- `Button.Icon.Danger`
- `Button.Danger`

## 5. 其他公共 UI 标准

### 排版

- UI 主字体跟随 Windows；中文使用系统回退字体。
- 正文以 14/20、Caption 以 12/16、Subtitle 以 20/28 为目标，标题使用 Semibold。
- 页面禁止随意添加字号；数字指标、代码/路径等特殊内容使用命名样式。
- 文案使用句式大小写和明确动词；错误信息说明“发生了什么、为什么、用户能做什么”。

### 间距与布局

- 以 4 DIP 为基础网格，主要序列为 4、8、12、16、24、32。
- 控件同组间距 8，标签到输入控件 4–8，内容组间距 12–16，章节间距 24。
- 建立统一的 `DialogBody`、`DialogFooter`、`FormField` 和 `PageHeader` 模式，取消每个窗口重复拼装页脚。
- 支持窗口最小尺寸、125%–200% DPI 和长中文/英文文案；不能依赖固定高度截断关键信息。

### 颜色、主题与状态

- 页面只引用语义 Brush，不引用具体颜色。
- Accent 优先映射系统强调色；产品状态色只用于信息、成功、警告、危险。
- Light、Dark、High Contrast 都必须可用。状态不得只靠颜色，需同时使用图标、文字或形状。
- 修正当前仅有浅色默认值的自定义 Surface/Text/Status Brush，使其随主题切换，而不是覆盖 WPF Fluent 的深色行为。

### 表单、列表与业务模式

- 统一 Label、必填提示、说明文字、验证错误和禁用状态。
- DataGrid 统一表头、行高、选择、排序、空状态和行内操作。
- 将端点选择器、同步摘要、安全 Callout、状态 Chip、空状态提升为可复用 Pattern；只在需要绑定行为和可访问性逻辑时升级为 CustomControl/UserControl。
- 删除、覆盖和双向冲突确认沿用 Core 的安全语义；UI 样式不得弱化确认步骤或删除阈值。

## 6. 实施阶段

### Phase 0：冻结契约与样例页

- 确定令牌命名、按钮矩阵、排版梯度和组件状态。
- 新增仅用于开发/测试的 UI Gallery 窗口，展示所有组件在 Light、Dark、High Contrast、Disabled、Focus 和长文案下的状态。
- 为新增 XAML 约定：新页面不得使用旧样式 Key，不得写内联颜色或复制控件模板。

完成标准：设计评审通过；Gallery 覆盖全部基础组件和按钮状态。

### Phase 1：修正基础层

- 让自定义语义色正确继承 WPF Fluent/System 资源。
- 收敛按钮为 Primary、Secondary、Tertiary、Icon、Danger 及尺寸变体。
- 清理 Toolbar/PrimaryAction 等重复契约，先保留兼容别名。
- 建立 Patterns 资源字典和统一对话框页脚。

完成标准：公共样式在三种主题下可读；无页面级颜色覆盖；键盘 Focus 清晰。

### Phase 2：迁移高频主流程

按用户频率和风险依次迁移：

1. `MainWindow`
2. `SyncConfirmationWindow`
3. `ProgressWindow`
4. `ProfileEditorWindow`
5. `SettingsWindow`

每迁移一个窗口，同时处理按钮层级、间距、排版、图标、空/错/忙状态、Automation Name 和 Tab 顺序，不做只换颜色的表面迁移。

完成标准：比较、确认、同步、取消、失败重试、冲突阻止等路径通过 UI acceptance tests。

### Phase 3：迁移其余窗口

- 端点管理与选择
- 云端/SFTP 编辑
- 计划任务
- 运行历史
- 更新、关于和挂载对话框

完成标准：所有生产 XAML 不再引用废弃样式；同类对话框具有同一结构与操作顺序。

### Phase 4：建立防回退门槛

- 增加 XAML 静态检查：禁止内联 Hex 色、废弃 Style Key、Emoji/字符命令图标、页面内复制 Button Template。
- 增加 UI Gallery 截图基线，至少覆盖 Light/Dark、100%/200% DPI；截图测试只作为视觉回归辅助，不替代行为测试。
- 使用 Accessibility Insights 或等价自动化检查可访问名称、键盘路径和对比度。
- PR 模板加入 UI 检查项；新 Pattern 或 Token 必须同时补 Gallery 示例。

完成标准：CI 能阻止最常见的一致性回退；UI acceptance suite 全部通过。

## 7. 验收指标

- 100% 的生产按钮能归入五种语义类型之一。
- 每个窗口/主要区域最多一个 Primary；例外需在代码评审中说明。
- 0 处生产 XAML 内联颜色，0 处废弃样式引用，0 个 Emoji/字符命令图标。
- 100% 仅图标交互控件具有 Tooltip 和 Automation Name。
- 所有主要流程可完全使用键盘完成，焦点始终可见。
- Light、Dark、High Contrast 和 200% DPI 下无关键内容截断、不可读或不可操作。
- 所有危险操作仍有明确文案、确认和现有安全校验。

## 8. 非目标

- 不把 WPF 迁移到 WinUI 3；这是平台重写，不是 UI 一致性工作的前置条件。
- 不为了“像 Fluent”而重写全部 WPF 控件模板。
- 不一次性机械替换所有 Margin/Padding。
- 不在本计划中改变同步、安全、执行或配置业务规则。

## 9. 首个实施迭代建议

第一个迭代只做 Phase 0–1，并选 `SyncConfirmationWindow` 作为试点。它同时包含主/次/危险操作和安全信息，最适合验证设计系统是否能承载 Feng Sync 的业务语义。试点验收后，再迁移 `MainWindow`，避免在最大页面上边设计边返工。
