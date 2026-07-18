# 08 过滤器编辑器

## 实施目标

将现有简单包含/排除通配符升级为可理解、可测试、可复用的过滤配置，同时保证过滤不会意外传播删除。

## 界面设计

- 分为包含规则、排除规则、属性条件三部分。
- 每条规则独占一行，支持注释、启停、拖动排序和错误提示。
- 提供常用预设：临时文件、版本控制目录、系统文件、图片/文档等。
- 属性条件支持最小/最大文件大小、修改日期范围、隐藏/系统文件、符号链接。
- “规则测试器”输入相对路径和属性，即时显示最终包含结果及命中的规则。
- 比较结果可临时排除选中项，但明确区分“本次过滤”和“保存到 Profile”。

## 建议代码文件

- 新增 `src/FengSync.Core/Filtering/FilterRule.cs`：规则模型。
- 新增 `src/FengSync.Core/Filtering/FilterEngine.cs`：统一匹配与解释结果。
- 新增 `src/FengSync.Core/Filtering/FilterDecision.cs`：命中规则和原因。
- 新增 `src/FengSync.Core/Filtering/FilterPresetCatalog.cs`。
- 新增 `src/FengSync/Views/FilterEditorControl.xaml`。
- 新增 `src/FengSync/ViewModels/FilterEditorViewModel.cs`。
- 修改 `ModePlanner.cs`：使用 FilterEngine，并为被过滤项建立明确语义。

## 功能流程

规则编辑时实时语法校验；比较扫描可尽早排除无需读取 hash 的文件。双向基线必须保留过滤边界信息：从“包含”变为“排除”不能被解释成文件删除。规则变化时提示重新比较。

## 验收标准

- 测试器结果与实际扫描一致。
- Windows 路径分隔符、大小写、目录递归规则有明确且稳定的语义。
- 过滤规则变化不会造成误删。
- 大目录下过滤不会造成明显性能回退。

