# 02 Profile 设置窗口 MVP

## 实施目标

提供完整、集中、可验证的 Profile 编辑入口，使用户无需手工编辑 JSON 即可设置名称、端点、比较、同步、过滤、删除与版本、校验和并发。

## 界面设计

- 采用左侧分类导航、右侧表单：常规、比较、过滤器、同步、版本管理、性能可靠性。
- 顶部固定显示 Profile 名称、启用状态、未保存标记。
- 常规页支持名称、说明、左右端点、交换、浏览和连接测试；云端通过 provider 卡片选择 Google Drive、SFTP、Amazon S3 或 S3 兼容存储。
- 比较页提供时间和大小、仅大小、内容/hash 三种策略及时间容差。
- 过滤页提供包含/排除多行规则、常用预设和“测试路径”。
- 同步页提供双向、镜像、更新；未完成的自定义模式显示不可用说明。
- 版本页提供永久删除、版本目录；回收站完成前不可选择。
- 底部提供“保存”“另存为新 Profile”“取消”。错误定位到具体导航页和字段。

## 建议代码文件

- 新增 `src/FengSync/Views/ProfileEditorWindow.xaml` 与 `.xaml.cs`：窗口与少量视图行为。
- 新增 `src/FengSync/ViewModels/ProfileEditorViewModel.cs`：编辑副本、命令、dirty tracking。
- 新增 `src/FengSync/ViewModels/ProfileSectionViewModel.cs`：分类页状态。
- 新增 `src/FengSync/Services/ProfileDialogService.cs`：打开窗口并返回保存结果。
- 新增 `src/FengSync/Views/EndpointEditorHost.xaml`：根据 provider 设置 schema 加载端点编辑器。
- 新增 `src/FengSync.Core/Profiles/ProfileValidator.cs`：业务校验。
- 修改 `src/FengSync/MainWindow.xaml`：增加“编辑 Profile”、复制、重命名入口。
- 修改 `src/FengSync.Core/ProfileStore.cs`：支持按 ID 更新和冲突检查。

## 功能流程

窗口始终编辑 Profile 深拷贝；只有验证通过并点击保存后才替换列表对象并持久化。切换分类不触发保存。连接测试只验证端点，不建立长期同步会话。另存为必须生成新 ID。

## 验收标准

- 所有 MVP 字段均能通过界面编辑、保存、重启后恢复。
- 无效端口、空端点、同一目录、归档目录嵌套等问题不能保存。
- 取消不会污染原 Profile；另存为不会覆盖源 Profile。
- 键盘导航、错误提示和高 DPI 下布局可用。
