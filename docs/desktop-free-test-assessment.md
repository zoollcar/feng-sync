# 让测试脚本「完全不争夺用户焦点、不显示在用户屏幕上」——技术方案评估

评估对象：`scripts/Test-All.ps1` + `tests/FengSync.UiTests/Scripts/Invoke-UiScenario.ps1`（桌面场景）+ `tests/FengSync.UiTests`（进程内 WPF 测试）+ `tests/FengSync.Tests`（Core 阶段）。

评估日期：2026-10-13。结论基于**本机实测**（探测脚本已清理，无残留进程），不是纸面推断。

---

## 0. 结论摘要

| 目标 | 现状 | 判定 |
| --- | --- | --- |
| 窗口不画在屏幕上 | 主窗口/传输窗由应用自己停到 `-32000`；其它窗口靠 harness 轮询后搬走 | **基本达成**（有闪窗窗口期） |
| 光标不动、不合成真实鼠标 | `SetCursorPos`/`mouse_event`/`SendKeys` 全部落在 `$script:showOnScreen` 之后 | **达成** |
| 不抢用户前台（焦点） | 窗口仍然激活，**实测抢走了前台** | **未达成** |
| 不留下可见痕迹（任务栏按钮） | 被停放的窗口仍带 `WS_EX_APPWINDOW`，会建任务栏按钮 | **未达成** |
| 覆盖度不因无桌面而损失 | 所有截图在无桌面模式下**直接跳过** | **有代价**（见 §4.6） |

**一句话**：位置那一半做得相当扎实，焦点那一半没做——而修焦点这一半的成本极低（1 处产品代码 + 1 个兜底样式 + 1 条护栏测试），收益是让 `tests/README.md:16-19` 里「never takes the foreground window」这句话从**假的**变成**真的**。

> **重要更新（见 §8）**：还有一条**完全不用改产品代码**的路线——把测试跑在一个 Windows **私有桌面**上。本机实测通过：窗口按正常几何（1650×960 @ 32,32）显示并完整渲染、用户桌面上该应用的窗口 0 个、用户前台全程未被抢、`PrintWindow` 能把画面完整取回，而应用零改动。因为子进程会继承桌面，改动只落在**测试项目的两个文件**上。**这条路线可用时应优先于 §5 的 P0。**

---

## 1. 现状盘点（已经做到的）

| 机制 | 位置 | 作用 |
| --- | --- | --- |
| `-ShowOnScreen` 开关（默认关） | `scripts/Test-All.ps1:8,84`、`UiAcceptanceTests.cs:21,156` | 唯一开关，三层贯通；默认不进前台模式 |
| 应用侧自行停放 | `src/FengSync/Services/TestWindowPlacement.cs:33-41`、`MainWindow.xaml.cs:63`、`Views/CloudTransferWindow.xaml.cs:52` | 在 `Show()` 之前就把窗口放到 `-32000`，避免「先显示再搬走」 |
| harness 侧兜底停放 | `Invoke-UiScenario.ps1:101-136`（`SWP_NOSIZE|NOZORDER|NOACTIVATE`） | 覆盖应用没自己放好的窗口 |
| 输入改走 UIA / posted 消息 | 同上 `:194-300`（`PostMessage`、`Post-KeyToWindow`、`InvokePattern`） | 不需要窗口在前台，也不碰物理指针 |
| 真实鼠标只在 `-ShowOnScreen` 下 | `:197-206`、`:255-262`、`:449-454`、`:1235` | 合成鼠标移动全部被开关挡住 |
| 截图只在 `-ShowOnScreen` 下 | `:1007-1016` | 无桌面时记录「skipped」而不是伪造证据 |
| 源码级护栏 | `tests/FengSync.Tests/DesktopFreeUiHarnessTests.cs` | 防止后人加一句 `SetCursorPos` 就把默认行为破坏掉 |
| 进程清理只杀测试进程 | `tests/Shared/TestProcessCleanup.ps1:11-20` | 按 `--fengsync-test-run-id` / `.fengsync-test\` / UiTests bin 路径三重匹配，**不会**误杀用户自己装的 FengSync |

这套设计本身是好的：**默认关、开关唯一、护栏在源码层**。问题只在「焦点」这一维度上少了一层。

---

## 2. 实测：当前实现仍然抢走用户前台（关键证据）

### 2.1 探测方法

用 `FENGSYNC_OFFSCREEN_WINDOWS=1` 启动真实的 `src/FengSync/bin/Debug/net10.0-windows/FengSync.exe`（数据目录指向临时目录、种上 `CloudGuideSeen`），在启动前后各读一次 `GetForegroundWindow()`，并枚举该进程的可见顶层窗口及其 `GetWindowRect` / `WS_EX_APPWINDOW`。

### 2.2 原始输出

```
=== baseline ===
before: fg hwnd=0x30828 pid=12376 proc=explorer title='T'

=== after launch (app pid 44672) ===
after : fg hwnd=0x690E04 pid=44672 proc=FengSync title='F'
visible top-level windows owned by the app:
  hwnd=0x690E04 rect=(-32768,-32768)-(-31118,-31808) appwindow=True toolwindow=False title='F'
taskbar-visible (WS_EX_APPWINDOW): 1
```

### 2.3 结论

1. **窗口确实停到了屏幕外**（`-32768,-32768`），这一半是有效的。
2. **前台从 `explorer` 变成了 `FengSync`**：窗口虽然看不见，但**照样抢走了用户的前台窗口**。
3. 该窗口带 `WS_EX_APPWINDOW`，会创建任务栏按钮——即使位置在屏幕外，用户仍可能看到任务栏上多出一个按钮/闪烁。

根因很直接：`TestWindowPlacement.Apply` 只设了 `WindowStartupLocation` / `Left` / `Top`，**没有关掉窗口激活**；WPF `Window.ShowActivated` 默认为 `true`，而激活与窗口位置无关。`grep ShowActivated src/` 结果为 0——全仓库没有任何地方设置过它。

因此 `tests/README.md:16-19` 与 `Invoke-UiScenario.ps1:82-95` 里「never takes their foreground window」的表述，与实测不符。

---

## 3. 修复机制对比（含实测）

我逐个实测了四种候选机制，结论差别很大：

| 机制 | 实测结果 | 判定 |
| --- | --- | --- |
| **`ShowActivated = false`（+ `ShowInTaskbar = false`）** | `ShowActivated=True -> 前台变成探测进程`；`ShowActivated=False -> 前台仍是用户窗口` | ✅ **正确做法** |
| `WH_CBT` 钩子 + `HCBT_ACTIVATE` 返回 1 | 激活被拦住了（`activations blocked: 1`），但**前台变成 `hwnd=0x0`**——用户窗口被置为非活动而无人接管 | ❌ **不要用**：比抢焦点更糟，用户按键会掉 |
| 类处理器拦 `FrameworkElement.InitializedEvent` | 反射枚举 `FrameworkElement` 的静态 `RoutedEvent` 字段：**不存在 `InitializedEvent`**（`Initialized` 是普通 CLR 事件） | ❌ 不可用 |
| 类处理器拦 `FrameworkElement.LoadedEvent` | `Loaded` 是最早的路由事件，但它在 `Show()` 之后才触发——激活已经发生 | ❌ 太晚 |
| **`Application.Resources` 里的隐式 `Style TargetType="{x:Type Window}"`** | 代码创建的 `Window` 上 `ShowActivated`/`ShowInTaskbar` 均被置为 `False`，**前台保持不变** | ✅ **推荐作为统一兜底** |

关于最后一条的实测输出：

```
implicit Window style installed into Application.Resources
--- window A: code-created, no local ShowActivated ---
  effective ShowActivated = False ; ShowInTaskbar = False
  after A.Show() -> foreground hwnd=0x4E0BDA pid=11628   <-- 用户窗口，未被抢
--- window B: local ShowActivated = true ---
  effective ShowActivated = True                          <-- 本地值盖过样式
```

**重要约束**：样式是「低优先级默认值」，**窗口上任何一处本地 `ShowActivated="True"` 都会盖掉它**。当前全仓库没有任何地方写 `ShowActivated`（`grep` 为 0），所以样式生效；这条不变式必须用护栏测试钉住，否则将来某个窗口写一行就静默失效。

---

## 4. 残余向量清单（按风险排序）

### 4.1 窗口激活抢前台（P0）

- 现象：见 §2。**每一次 `Show()`/`ShowDialog()` 都可能抢前台**。
- 覆盖面：`src/FengSync` 下 21 个 `Window` 派生类 + 约 6 处 `new Window { ... }.ShowDialog()` 匿名窗口；而 `TestWindowPlacement.Apply` 目前只被 2 个窗口调用（`DesktopFreeUiHarnessTests.cs:107-122` 正是钉这两个）。
- 建议：产品侧加「进程级抑制」（§5 P0），而不是去 27 处逐个补 `Apply`。

### 4.2 非主窗口的 CenterOwner 闪窗（P0）

- 现象：除主窗口/传输窗外，所有窗口都在 XAML/代码里写死 `WindowStartupLocation="CenterOwner"`（`SettingsWindow.xaml:6`、`ScheduleWizard.xaml:8`、`RunHistoryWindow.xaml:9`、`AboutWindow.xaml:8`、`ProfileEditorWindow.xaml:6`、`CloudEndpointEditorWindow.xaml:6` … 共 16 个 XAML + 若干代码构造）。它们**先出现在用户屏幕上**，再被 harness 的 `Park-AllAppWindows` 轮询搬走——中间存在真实可见的闪窗窗口期。
- 注意：`WindowStartupLocation` 是本地值，**样式改不动它**，所以「进程级抑制」解决的是焦点，不解决这个闪窗。
- 建议：把 `TestWindowPlacement.Apply` 扩展到所有窗口（或引入 `FengSyncWindow` 基类在构造函数里统一调用），让窗口在第一帧就落在屏幕外。

### 4.3 无 owner 的 `MessageBox`（P1）

- 现象：有 owner 的 `MessageBox` 会相对（已被停到屏幕外的）owner 定位，通常跟着到屏幕外；**没有 owner 的 `MessageBox.Show(msg, caption, ...)` 会居中在主显示器上**——真实可见。
- 命中点：`Views/AboutWindow.xaml.cs:12-13`、`Views/ProfileEditorWindow.xaml.cs:64,65,66,67,72,83`、`Views/SftpServerSettingsWindow.xaml.cs:94`、`Services/UpdateCoordinator.cs:37,38,49,51,53,63`。
- 建议：这些都在错误/校验路径上，触发概率低。要么给它们补 owner，要么接受并记录为已知例外。

### 4.4 文件/文件夹选择器（P1）

- 现象：`MainWindow.xaml.cs:437`、`:560`、`:573`、`ProfileEditorWindow.xaml.cs:121` 等打开的是 Win32 公共对话框；harness 在 `:742` 明确对它们做 `Park-WindowOffScreen`，说明**它们确实会先出现在屏幕上**。
- 建议：公共对话框是独立 HWND、属于被测进程，`EnumWindows` + 停放对它们有效；缩短窗口期的办法是在打开前先 `Park-AllAppWindows`，并把停放轮询间隔调小（当前靠 `Wait-Until` 的循环，频率未固定）。

### 4.5 `schedule` 场景会拉起一个真实控制台窗口（P1）

- 现象：场景点「测试运行」后 `Wait-Until ... 'Scheduled test run did not leave the running state.'`（`:1848-1851`）说明**计划任务真的跑起来了**；而任务命令行是
  `"<CliPath>" run --profile <id> --non-interactive --json-log`（`src/FengSync.Core/Automation/WindowsTaskSchedulerService.cs:37`），`FengSync.Cli` 是 `OutputType=Exe`（控制台子系统）。计划任务在当前用户会话里执行 → **控制台窗口会闪现在用户桌面上**。
- 可选缓解（都有代价）：
  1. 把 `FengSync.Cli` 改成 `WinExe`——彻底没有控制台，但交互式 CLI 用途受影响；
  2. 任务命令行外面套一层隐藏窗口的宿主（`pwsh -WindowStyle Hidden ...`）——产品行为变更；
  3. 测试侧接受这 1~2 秒的闪窗并写成已知例外（`<Hidden>true</Hidden>` 只隐藏任务本身，**不隐藏控制台窗口**，这条路走不通）。
- 我的建议：先按 3 记录，等 P0/P1 落地后再评估 2。
- **已处置（见 §9.5）**：私有桌面方案落地后，这条顾虑只剩「任务本身真的跑起来时闪窗」；而该场景的最后一层验证依赖
  外部程序 `schtasks.exe`，在本机被程序黑名单拦截，因此整条场景已删除，这条风险随之消失。

### 4.6 截图的覆盖度损失（P1，技术风险最高的一条）

- 现象：无桌面模式下 `Capture-Element` 直接记录「skipped」（`:1007-1016`），于是 `ui-shell-native` / `ui-shell-software` / `ui-visual-matrix` 以及所有场景的截图证据全部为空；`ui-shell` 里对 `02-shell.png` 的存在性断言也被 `if ($script:showOnScreen ...)` 短路（`:1246`）。剩下的只是 `BoundingRectangle` 几何断言。
- 缓解思路：用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` 替代 `Graphics.CopyFromScreen`——**对屏幕外窗口有效，且不需要窗口可见或在前台**。风险：WPF 走 D3D 硬件渲染时 `PrintWindow` 可能拿到空/陈旧画面，所以 `ui-shell-native` 这条要么接受降级、要么临时切 `FENGSYNC_FORCE_SOFTWARE_RENDERING=1`，要么改成应用侧用 `RenderTargetBitmap` 自渲染（最可靠，但要加产品代码）。
- 这一条是「完全不显示在屏幕上」与「像素级渲染验证」之间的**本质冲突**，需要明确取舍。

### 4.7 `ui-visual-matrix` 的最大化（P2）

- 现象：`:1263-1274` 自己写明了——「最大化是唯一会把停放的窗口放回显示器上的手势」，所以它把窗口放到用户屏幕上、截图、再恢复停放。这是一段**刻意的、已知的**屏幕占用。
- 缓解：无桌面模式下用 `TransformPattern.Resize` 到一个足够大的尺寸代替 `Maximized`（几何断言用的是矩形，两者等价），或该场景整体保留 `-ShowOnScreen` 要求。

### 4.8 harness 自己仍有两条抢焦点的路径（P2）

- `Focus-OffScreenWindow`（`:157-167`）在**无桌面模式**下也会 `SetForegroundWindow`——用于打开 `ContextMenu`（调用点 `:688-689`）。注释里承认「只改变前台窗口」。
- 无桌面分支里的 `$element.SetFocus()`（`:212`、`:289`）可能触发激活（UIA `SetFocus` 对非活动窗口的行为需要实测确认）。
- 建议：把 `Post-ContextMenuKeyChord`（Shift+F10，`:232-242`）提到 attempt 1，把 `Focus-OffScreenWindow` 降为最后回退；并实测 `SetFocus()` 是否真的抢前台（用 §2.1 同一套探测器，60 秒可得结论）。

### 4.9 已确认**不需要**动的部分

- 进程清理（`TestProcessCleanup.ps1`）已经严格限定在测试进程上。
- `FengSync.Updater` 是 `WinExe`，且 `PortableUpdaterIntegrationTests` 刻意拿它当「惰性重启目标」以避免 `cmd.exe` 留下交互式控制台——这条已经处理过了。
- `RcloneOAuthFlowTests` 用的是注入的假浏览器打开器，且生产侧带 `config_auth_no_browser=true`，不会真的拉起浏览器。
- Core 阶段的 rclone 子进程都带 `UseShellExecute=false` + 继承父控制台（或 `CreateNoWindow=true`），不会新开窗口。

---

## 5. 建议方案（分阶段）

> **先读 §8。** 如果「私有桌面」这条路走得通（本机实测可行），它就是**首选**：产品零改动、焦点与可见性一起解决、截图还能保住。下面的 P0/P1 退化为**降级路径**——当 `CreateDesktop` 被环境策略拦下、或不想引入 P/Invoke 启动器时使用。

### P0 — 把「不抢前台」变成进程级属性（改动最小、收益最大）

1. **产品侧**：`TestWindowPlacement` 增加进程级抑制，在 `App.OnStartup` 的 `base.OnStartup(e)` **之前**（即 `StartupUri` 窗口创建之前）安装：

   ```csharp
   // 仅在 FENGSYNC_OFFSCREEN_WINDOWS=1 时合并
   application.Resources.MergedDictionaries.Add(new ResourceDictionary {
       Source = new Uri("pack://application:,,,/FengSync;component/Themes/DesktopFreeTest.xaml")
   });
   ```
   其中 `Themes/DesktopFreeTest.xaml` 只含一个隐式样式：

   ```xml
   <Style TargetType="{x:Type Window}">
     <Setter Property="ShowActivated" Value="False" />
     <Setter Property="ShowInTaskbar" Value="False" />
   </Style>
   ```

   （实测依据见 §3：隐式样式对代码创建与 XAML 创建的 `Window` 都生效，且能让前台保持不变。）
2. **护栏测试**（`DesktopFreeUiHarnessTests` 加两条，都是源码级、毫秒级）：
   - 全仓库 `grep ShowActivated` 只允许出现在 `TestWindowPlacement.cs` 与那个样式 XAML 里——**防止本地值盖掉样式**（§3 的约束）；
   - `TestWindowPlacement` 必须在该样式 XAML 里同时出现 `ShowActivated` 与 `ShowInTaskbar` 的 `False` setter。
3. **验收**：重跑 §2.1 的探测器，`before`/`after` 的前台 pid 必须相同（这是唯一能证明「不抢焦点」的判据，**不要**用「门禁绿了」代替）；然后跑一次 `pwsh -File .\scripts\Test-All.ps1 -Level UiOffline`（超时给 `600000`，见陷阱 13），确认 24 个场景没有因为窗口不再激活而失效。

   **已知交互风险**：`ContextMenu` 这类「WPF 只路由给活动窗口」的手势会更依赖 `Focus-OffScreenWindow` 回退（§4.8），可能让个别场景变慢或偶发失败。若出现，按 §4.8 调整尝试顺序，不要回退 P0。

### P1 — 消掉屏幕上的闪窗窗口期

4. 让**所有**窗口在第一帧就落在屏幕外：把 `TestWindowPlacement.Apply(this)` 补进其余窗口的构造函数，或引入 `FengSyncWindow` 基类统一调用（21 个类 + 6 处匿名窗口）。
   - 护栏：沿用 `DesktopFreeUiHarnessTests.The_shell_and_the_transfer_window_are_moved_off_screen_before_they_are_shown` 的写法，扩成「枚举 `src/FengSync` 下所有 `: Window` 的类，每个都必须出现 `TestWindowPlacement.Apply`」。
5. 无 owner 的 `MessageBox` 补 owner（§4.3），或在文档里明确列为已知例外。
6. 用 `PrintWindow` 恢复无桌面模式下的截图（§4.6），并明确 `ui-shell-native` 的取舍。

### P2 — 收尾

7. `ui-visual-matrix` 改用 `Resize` 代替 `Maximized`（§4.7）。
8. `Focus-OffScreenWindow` 降为最后回退，并实测 `SetFocus()` 的抢焦点行为（§4.8）。
9. ~~`schedule` 场景的控制台窗口：记录为已知例外，或评估 CLI 改 `WinExe` 的代价（§4.5）。~~
   **已随该场景整体删除而消解**（§9.5）。
10. 更新 `README.md` / `tests/README.md` / `AGENTS.md` 里的措辞，并**在文档里写清判据是探测器而不是门禁绿**。

---

## 6. 不建议做的事

- **不要用 `WH_CBT` + `HCBT_ACTIVATE` 拦截**：实测会把用户窗口置为非活动（前台变 `hwnd=0x0`），比抢焦点更糟（§3）。
- **不要在 `Loaded` 事件里补救**：太晚，激活已经发生（§3）。
- **不要只靠「源码里有 `$script:showOnScreen` 字样」这种护栏**：现有 `DesktopFreeUiHarnessTests` 的字符串检查是必要的，但挡不住 `ShowActivated` 语义这类漏洞——新护栏要检查**不变式**（谁允许写 `ShowActivated`），而不只是「出现过开关变量」。
- **不要为了让它变绿而改既有测试**（陷阱 9）：例如把 `ui-shell` 的截图断言删掉，就把「渲染验证」这个真实覆盖度一起删了。

---

## 7. 工作量与风险

| 阶段 | 改动面 | 风险 | 验证成本 |
| --- | --- | --- | --- |
| P0 | 1 个产品类 + 1 个样式 XAML + 1 个 `App.OnStartup` 调用 + 2 条护栏 | 低；主要风险是少数依赖「活动窗口」的手势（ContextMenu）行为变化 | 探测器 60s + 门禁 ~4m20s |
| P1 | 21 个窗口类 + 6 处匿名窗口 + 截图实现 | 中；改动面广，护栏必须跟上 | 门禁 ~4m20s |
| P2 | 2 个场景 + 文档 | 低 | 门禁 ~4m20s |

P0 是「一个下午就能做完并证明有效」的改动；P1 的截图部分（§4.6）是唯一需要产品侧权衡取舍的地方。

---

## 8. 备选路线：私有桌面隔离（实测可行，产品零改动）

> **落地状态：本方案已被采用并实现，验证通过。** 改了什么、新增了哪些实测、以及哪几处与本节预测不同，见 **§9**。本节保留为当初的评估记录。

这一节回答「有没有办法不改程序就做到」，并且**全部结论都是本机实测**，不是纸面推断。

### 8.1 机制

Windows 的「桌面」（desktop object）是窗口站（window station）里的一个独立显示空间。用 `CreateDesktop` 建一个自己的桌面，再用 `CreateProcess` + `STARTUPINFO.lpDesktop` 把进程放进去，那个进程的所有窗口就只存在于这个桌面上——**不是「移出屏幕」，而是「根本不在用户的桌面上」**。

### 8.2 实测结果（本机 1920×1080）

| 测项 | 实测结果 |
| --- | --- |
| `CreateDesktop("FengSyncTest-…")` | 成功（handle `0xB20`，lastError 0）；**普通用户权限即可**，不需要管理员 |
| 直接在该桌面启动 `FengSync.exe` | 用户桌面上该应用的窗口 **0 个**；私有桌面上 3 个 |
| 窗口几何 | `rect=(32,32)-(1682,992)`，即 **1650×960 的正常窗口**——不是 `-32000`。应用完全没被改动，它以为自己正常显示了 |
| 用户前台 | 启动前 / 运行中 / 清理后**都是同一个 pid**（用户自己的窗口），全程未被抢 |
| **子进程继承** | 在私有桌面上启动的 `pwsh` 再 `Start-Process FengSync.exe` → 该窗口同样落在**私有桌面**（私有桌面 3 个 / 用户桌面 0 个） |
| UIA 跨桌面 | `AutomationElement.FromHandle(hwnd)` 从默认桌面**可用**；但 `AutomationElement.RootElement`（默认桌面）看不到该应用的窗口（0 of 10） |
| 截图 | `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` 成功；**硬件渲染下客户区全白（只抓到标题栏）**，`FENGSYNC_FORCE_SOFTWARE_RENDERING=1` 下**内容完整**（侧栏/工具栏/比较表/变更汇总/状态栏全部到位，40 种颜色） |

### 8.3 为什么它的改造量比改产品小得多

关键就是「子进程继承桌面」这一条：**只要把 `UiAcceptanceTests.RunAsync` 里启动的那个 `pwsh` 放进私有桌面，`Invoke-UiScenario.ps1` 内部所有的 `Process.Start`（FengSync.exe / rclone / schtasks / FengSync.Cli）就全部自动落到私有桌面上**，于是：

- 场景脚本的交互逻辑**一行都不用改**；
- `EnumWindows`（枚举的是调用线程自己的桌面）**天然自洽**，`Get-AppWindowHandles` 照常工作；
- `Focus-OffScreenWindow` / `SetForegroundWindow` 变成「抢私有桌面的前台」，**对用户无害**——§4.8 那条残余向量自动消失；
- §4.1（抢前台）和 §4.2（CenterOwner 闪窗）**一起解决**，因为窗口压根不在用户桌面上。

改动清单（全部在测试侧）：

1. `tests/FengSync.UiTests/PrivateDesktop.cs`：`CreateDesktop` / `CreateProcessW`(lpDesktop) / `CloseDesktop`，约 80 行 P/Invoke；
2. `UiAcceptanceTests.RunAsync`：`Process.Start("pwsh")` → 在私有桌面上 `CreateProcess`（约 10 行）；
3. `Invoke-UiScenario.ps1` 的 `Capture-Element`：`CopyFromScreen` → `PrintWindow`（约 15 行），并给被测进程加 `FENGSYNC_FORCE_SOFTWARE_RENDERING=1`。

对照 §5 的 P0/P1：那条路要动 1 个产品类 + 1 个样式 + `App.OnStartup`，P1 还要动 21 个窗口类和 6 处匿名窗口。**私有桌面的改造面明显更小，而且更彻底。**

### 8.4 代价与风险

- **`ui-shell-native` 这条场景的本质会被削弱**：它验证的就是「原生（硬件）渲染下窗口也可用」，而硬件渲染下 `PrintWindow` 拿不到客户区。要么让它保留 `-ShowOnScreen`（或临时 `SwitchDesktop` 切过去看），要么改成断言别的东西。注意：这条场景**现在在无桌面模式下本来就跳过截图**，所以不算新增损失。
- `CreateDesktop` 在个别环境会被组策略 / EDR 拦（本机通过）。必须有「拿不到桌面就退回现状」的降级路径。
- 私有桌面没有可抓的屏幕 DC，`CopyFromScreen` 必须**全部**换掉。
- `RootElement` 跨桌面不可见——只要 harness 一起进私有桌面就不成问题（这正是 §8.3 的做法）。
- 非活动桌面上 DWM 的合成行为可能随 Windows 版本变化，`PrintWindow` 的可靠性需要用一次**真实场景**（而不是我这个最小探测）验证。

### 8.5 其他「不改程序」的路线（对比）

| 路线 | 解决可见 | 解决焦点 | 截图 | 代价 |
| --- | --- | --- | --- | --- |
| **私有桌面**（本节） | ✅ | ✅ | ✅（需软件渲染） | 测试侧 ~105 行 |
| 独立会话：断开的 RDP / Windows Sandbox / VM / CI | ✅ | ✅ | ✅ | 零代码，但要额外环境；本地开发机体验差 |
| 私有 window station + 私有桌面 | ✅ | ✅ | ✅ | 更彻底（剪贴板/热键也隔离），更容易被策略拦 |
| `DwmSetWindowAttribute(DWMWA_CLOAK)` | ✅ | ❌ | ⚠️ | harness 侧即可，但**激活照旧发生** |
| `STARTF_USESHOWWINDOW` + `SW_SHOWNOACTIVATE` | — | ⚠️ | — | 只影响第一个窗口，且依赖应用用 `SW_SHOWDEFAULT` 显示；**不可靠，不建议** |

**关于「测试框架」**：FlaUI、WinAppDriver(Appium-Windows)、Winium、Coded UI、AutoIt 都**不提供**桌面隔离——它们驱动的是真实交互桌面（WinAppDriver 还要常驻服务 + 真实鼠标），比现状更侵入。**能解决这个问题的不是测试框架，而是操作系统的隔离机制**；框架只负责「怎么点」，不负责「点在哪个桌面」。

---

## 9. 私有桌面方案落地记录（已实现并验证）

### 9.1 改了什么

**删掉的产品侧「隐藏逻辑」**

| 删除 | 说明 |
| --- | --- |
| `src/FengSync/Services/TestWindowPlacement.cs` | 产品类，唯一职责是替测试把窗口挪到屏幕外 |
| `MainWindow.xaml.cs` / `Views/CloudTransferWindow.xaml.cs` 里的 `TestWindowPlacement.Apply(this)` | 两个窗口构造函数里的测试专用接缝 |
| `FENGSYNC_OFFSCREEN_WINDOWS` | 打开上面那个接缝的环境变量 |
| `tests/FengSync.UiTests/TestWindowPlacementTests.cs` | 随产品类一起删掉 |

**删掉的 harness 侧「隐藏逻辑」**：`Initialize-TestWindowPlacement`、旧的 `Get-AppWindowHandles`（只用于停靠）、`Park-WindowOffScreen`、`Park-AllAppWindows`、`Focus-OffScreenWindow`、`$script:offScreenOrigin = -32000`，以及 9 处调用点；P/Invoke 里的 `ShowWindowNative` / `SetWindowPosNative` 与 `TOPMOST` / `NOTOPMOST` / `NOMOVE` / `NOSIZE` / `SHOWWINDOW` / `NOZORDER` / `NOACTIVATE` / `RESTORE` 常量。

**新增 / 改写的测试侧代码**

| 文件 | 内容 |
| --- | --- |
| `tests/FengSync.UiTests/PrivateDesktop.cs`（新） | `CreateDesktopW` / `CreateProcessW`(lpDesktop) / `CloseDesktop`；子进程 stdout+stderr 合并落文件（harness 输出远超管道缓冲）；`ResolveApplication` 补上「`CreateProcess` 不像 `Process.Start` 那样搜 `PATH`」这一差异；`Quote` 改为按 `CommandLineToArgvW` 的规则转义 |
| `UiAcceptanceTests.RunAsync` | 默认 `PrivateDesktop.Create()`；只有 `ShowOnScreen` 才走可见桌面；超时清理改用 `taskkill /T` 走完整个进程树 |
| `Invoke-UiScenario.ps1` `Capture-Element` | 私有桌面分支改用 `PrintWindow` + `PW_RENDERFULLCONTENT`，再按元素矩形裁剪 |
| `Invoke-UiScenario.ps1` `Start-App` | 按场景给被测进程设 `FENGSYNC_FORCE_SOFTWARE_RENDERING`（`ui-shell-native` 除外） |
| `Invoke-UiScenario.ps1` `Get-MainWindowHandle`（新） | 结构性识别主窗口，取代 `Process.MainWindowHandle` |
| `tests/FengSync.Tests/PrivateDesktopHarnessTests.cs`（新，9 用例） | 源码级护栏，取代 `DesktopFreeUiHarnessTests` |

### 9.2 新增实测（都在本机）

**A. 合成鼠标输入在私有桌面上被系统直接拒绝。** 同一段探针分别在私有桌面与可见桌面（对照组）上执行：

| API | 私有桌面 | 可见桌面（对照） |
| --- | --- | --- |
| `GetThreadDesktop` | `FengSyncInputApi-…` | `Default` |
| `OpenInputDesktop` | `Default` | `Default` |
| `GetCursorPos` | **False，err=5（ACCESS_DENIED）** | True `(1635,501)` |
| `SetCursorPos(400,300)` | **False** | True，回读 `(400,300)` |
| `mouse_event(down)` | **err=5（ACCESS_DENIED）** | err=0 |

**这条直接决定架构**：私有桌面解决的是「焦点 / 可见性 / 任务栏」，**不解决「输入可达性」**。所以 harness 里 UIA pattern、投递按键、`PostMessage` 这些路径**不是隐藏逻辑，必须保留**——它们是唯一能把操作送进「非输入桌面」上窗口的手段。§8.3 里「交互逻辑一行都不用改」在这一点上过于乐观：真正需要改的是「谁来找主窗口」和「截图从哪来」。

**B. `Process.MainWindowHandle` 在私有桌面上会指错窗口。** 实测应用进程在该桌面上有 3 个**可见**顶层窗口：

| hwnd | class | UIA ControlType | 含 `ProfileList` | `MainWindowHandle` |
| --- | --- | --- | --- | --- |
| `0xD60B8E` | `UAC_InputIndicatorOverlayWnd` | **Pane** | 否 | ← **返回的是它** |
| `0x1210B52` | `UAC Input Indicator` | Pane | 否 | |
| `0x2F0C9E` | `HwndWrapper[FengSync;;…]`「Feng Sync」 | **Window** | **是** | 真正的主窗口 |

这些输入指示器覆盖窗是**进程内**创建、启动几秒后出现、且压在 z 序最上层，于是 `MainWindowHandle`（= 进程拥有的第一个可见顶层窗口）开始返回它。后果已实测到：`ui-shell` 报 `Missing UI element: CompareButton`——`Get-LiveMain` 每次重新取 `MainWindowHandle`；而 `about` 因为在启动早期就捕获了 `$main`、之后不再重取，所以侥幸通过。修复方式是按结构识别：`ControlType == Window` 且子树里有 `ProfileList`，并把结果缓存（枚举很便宜，确认用的 UIA 子树搜索不便宜，而调用方在 100ms 循环里轮询）。

**C. 用户桌面上确实一个窗口都没有。** 场景运行期间在可见桌面每 500ms 采样一次：

```text
samples=390 over 200s
max FengSync windows visible on THIS desktop at once = 0
distinct foreground titles seen = 1
  fg: WorkBuddy AI
RESULT: PASS - Feng Sync never appeared on this desktop (no window, no foreground).
```

**D. 截图内容完整。** `PrintWindow` + 软件渲染在私有桌面上抓到的 `01-main.png`（63KB）包含侧栏、工具栏、比较表、变更汇总与状态栏，与可见桌面下 `CopyFromScreen` 的结果一致。`ui-shell-native`（原生渲染）只抓到窗口框架（13.7KB），与 §8.2 的实测一致，截图清单的 `Note` 会注明。

### 9.3 与 §8.4 预测不同的地方

| §8.4 的预测 | 实际决定 |
| --- | --- |
| 要有「拿不到桌面就退回现状」的降级路径 | **不降级，直接抛。** 静默退回可见桌面等于让窗口突然出现在用户屏幕上，比测试失败更糟；`PrivateDesktop.Create` 与 `PrivateDesktop.Start` 都抛异常 |
| `ui-shell-native` 场景本质被削弱 | 保留原生渲染不变，接受「只抓到窗口框架」，并在截图清单里写明；该场景的断言本来只检查「文件存在且非空」，没有检查像素 |
| 需要给被测进程加 `FENGSYNC_FORCE_SOFTWARE_RENDERING=1` | 采纳，但按场景区分：只有 `ui-shell-native` 保留原生渲染 |
| `Activate-TestWindow` 变成「抢私有桌面的前台，对用户无害」，可以无条件调用 | **进一步收紧为只在 `-ShowOnScreen` 下生效。** 实测该桌面的前台窗口在 `SetForegroundWindow` 之后仍是 `0x0`（调用被拒），默认路径本来就不需要它；收紧后护栏才能继续用「触碰指针必须查开关」这条强不变式 |
| 改造量约 105 行 | 实际更大（约 300 行改动 + 删除），主要花在两个没预料到的地方：`MainWindowHandle` 指错窗口（9.2B），以及 `CreateProcess` 不搜 `PATH` / 命令行转义规则 |

### 9.4 验证

| 验证 | 结果 |
| --- | --- |
| 单个场景端到端（`about`） | 私有桌面上 6s 通过 |
| 三个 shell 场景（`ui-shell` / `-native` / `-software`） | 全部通过（修掉 9.2B 之后） |
| 用户桌面泄漏采样 | 390 次采样，0 个窗口，前台全程未变（9.2C） |
| 截图内容 | 私有桌面上抓到完整界面（9.2D） |
| 护栏测试 | 9/9 通过；**并已用「注入回归」证明它真的会失败**——注入无护栏的 `SetCursorPos`、`-32000`、`FENGSYNC_OFFSCREEN_WINDOWS`、`.MainWindowHandle` 之后，3 条用例精确报错 |
| 全量门禁 | 见 §9.5 |

### 9.5 全量门禁结果

```text
pwsh -File .\scripts\Test-All.ps1 -Level UiOffline
```

| 阶段 | 结果 |
| --- | --- |
| Build | Passed（0 warning / 0 error） |
| Core, CLI, real SFTP and available WinFsp | **Passed 576 / 576**（6m31s） |
| WPF UI acceptance tests（online services excluded） | **668 passed / 1 failed / 669**（15m19s） |

唯一失败的是 `schedule` 场景，且**与本次改造无关**，原因是环境策略而不是代码：

- 失败点是 harness 自己的 `& schtasks.exe /Query /TN $scheduledTask *> $null`（脚本第 1826 行），
  报 `Program 'schtasks.exe' failed to run`；
- `schtasks.exe` 在本机被**安全中心的程序黑名单**拦截，拦截提示明确写着「cannot be approved or bypassed
  from the current command」，需要用户自行从 Security Center → Command Security → Program Blacklist 里移除；
- 该场景里所有 `schtasks.exe` 调用点与改造前 **逐字节相同**（`git show HEAD:` 对比确认，只有行号位移）；
- 场景在私有桌面上**已经成功驱动完整个计划任务向导**——保留下来的 `schedule-configured.png` 显示任务名已填入
  唯一值 `FengSync-Test-63e14a1876ee4cc685f39e8f6a84ee7…`、频率 `DAILY`、按钮齐全。也就是说私有桌面改造在这条
  场景上工作正常，只是最后一步要调用的外部程序被环境禁止。

把 `schtasks.exe` 从黑名单移除后重跑该场景即可确认；其余 668 条（含全部 20 个非 `schedule` 场景）已全绿。

### 9.6 删除 `schedule` 场景（用户决定）

上面那条路走不通，因为黑名单**无法从命令行绕过**——拦截提示自己就写明了这一点，只能由用户在安全中心里手工改。
一条在本机永远不可能变绿的场景留在门禁里，价值是负的：它把「环境禁止」长期伪装成「测试失败」。因此整条场景删除。

删除范围（`git` 里可查）：

| 位置 | 删除内容 |
| --- | --- |
| `tests/FengSync.UiTests/UiAcceptanceTests.cs` | `Schedule_ui_creates_tests_and_deletes_a_unique_windows_task` 用例 |
| `Invoke-UiScenario.ps1` | `'schedule'` 分支（17 行）、`-Scenario` 的 `ValidateSet` 条目 |
| `Invoke-UiScenario.ps1` | `$scheduledTask` 状态变量与其 `schtasks /Delete` 清理行 |

删除后 harness 里不再出现 `schtasks` / `ScheduleWizard` / `SchedulesButton` 任何字样（`grep` 为 0），
`-Scenario` 的合法值从 25 个变为 24 个。**`UiAcceptanceTests.cs` 里那句「子进程继承私有桌面，所以应用、rclone、
schtasks 和 CLI 都留在那个桌面上」的注释也一并去掉了 `schtasks`**——它已不是这个套件会拉起的东西。

重跑门禁（同一命令、同一台机器）：

| 阶段 | 结果 |
| --- | --- |
| Build | **Passed**（0 warning / 0 error） |
| Core, CLI, real SFTP and available WinFsp | **Passed 576 / 576**（6m27s） |
| WPF UI acceptance tests（online services excluded） | **Passed 668 / 668**（15m19s） |

`GATE_EXIT=0`，三个阶段全绿，`schedule` 那条假失败随场景一起消失。

**代价（必须记住）**：这是唯一一条覆盖 `ScheduleWizard` UI 的场景。删除后计划任务向导只剩 Core 层单测
（`AutomationAndSftpTests.Task_scheduler_never_puts_secrets_in_command`），它断言的是「命令行里不出现凭据」，
不是「向导能创建/测试/删除任务」。如果以后要把这层覆盖找回来，正解是**只断言向导自己的 `ResultText`**
（创建/测试/删除的结果文案），而不是用外部 `schtasks.exe` 回读真实任务——代价是它证明的是「向导报告成功」，
而不是「Windows 真的接受了这个任务」。
