# 云盘管理改进 —— 交接文档

> **给接手这条线的人（或下一轮会话）。**
> 分支：`dev`（领先 `origin/dev` **73** 个已提交 —— 数字含本文最后一次更新，未 push）。工作区**干净**（仅剩未跟踪的 `.arts/`）。
> 最后更新：2026-10-04。
>
> 本文是这条线**唯一的权威文档**：路线图（§5）、需求盘点的判断依据、已实证的事实（§4）与项目约定（§7）都在这里。
> 原先那份独立的《云盘功能缺口盘点与演进计划》已把仍有价值的内容（剩余项的现状/缺口/参考做法、实现约束）
> 合并进本文后删除，需要它的原文可以去 git 历史里找（`git log --diff-filter=D -- docs/`）。

---

## 0. 30 秒速览

| | |
| --- | --- |
| **在做什么** | 把云盘功能缺口盘点里的 P1 剩余 17 项 + P2 全部 21 项 + 技术债 7 项（共 45 项）补齐，拆成 6 个子批次：**0 → A → B → C → D → E**。每个子批次独立跑门禁、独立提交（**只提交不 push**）。 |
| **已提交** | 批次 **0**（技术债 7 项）、**A0**（Provider descriptor 重构）、**A1**（OneDrive/Dropbox/Box/WebDAV + S3 provider 目录）、**A2**（端点重命名）、**A3+A4**（端点搜索/排序 + 测试全部）、**A5**（代理）、**A6**（分组/标签）、**A7**（端点导出/导入）、**B0**（队列泛化：远端→远端复制）、**B1**（浏览器复制/剪切/粘贴 + 跨端点复制）、**B2**（上传/粘贴冲突：`KeepBoth` 与逐文件询问）、**B3**（属性 / 详情：`operations/stat` + 按需校验值）、**B4**（分享链接：`operations/publiclink` + 先确认再生成）、**B5**（回收站：端点根下的 `.fengsync-trash` + 还原，清空复用**同一条**删除门槛）、**B6**（键盘快捷键：一张纯表把按键映射成命令，命令一律调按钮已有的方法）、**B7**（网格视图 / 缩略图 / 内置预览：**同一份行集合的第二种画法** + 13 个具名预览结果 + 只在 converter 里查缓存、拉字节走可取消的一轮）、**B8**（收藏 / 最近访问：**两条不同的承诺** —— 收藏的顺序归用户且永不修剪，最近访问是应用写的有界队列；规则一个纯类 + 一个只存地名不存凭据的文件 + 同一批行的两个下拉；端点重命名从此一起搬**四**份文件）。**A 组全部收尾，B 组走到 B8。**见 §2。 |
| **下一步** | **B9–B11 文件浏览器**（§5）。B1 打通了「一条剪贴板行 → `EnqueueCopy` 的五个参数」，B2 把「同名冲突怎么答」抽成上传与粘贴共用的纯规则，B3 又加了一条**读单个对象**的路（`StatAsync` → `CloudItemDetails` 定行 → 代码搭的面板），B4 加了**第一个会对外发出权限的动作**（确认走 `ConfirmShare`、剪贴板走 `CopyLink`，都是接缝），B5 加了**第一个「删掉但还能回来」的动作**（规则全在 `CloudRecycleBin`，移动照 §4 实测的两条腿走，「清空回收站」问的还是 `CloudDeleteSafety` 那**一道**门槛），B6 把键盘接上了**同一批方法**（`CloudBrowserShortcuts.Resolve` 只做路由，新加的 `PromptRename` 是重命名问题的接缝），B7 加了**同一份行集合的第二种画法**（`ApplyBrowserView` 只换 `View`/`ItemTemplate`/`ItemsPanel`，`ItemsSource` 仍是那一份 `_entries`）和**读字节的那条有界路**（`54eb6f5` 的 `ReadAsync` → 13 个具名结果的 `CloudBrowserPreview`，缩略图 converter 只查缓存、拉字节走 `RestartTileRound` 那一轮可取消的）。B8 加了**两条不同的承诺**（`CloudBrowserBookmarks` 一个纯类分别管「收藏的顺序归用户、永不为容量修剪」和「最近访问是应用写的有界队列」，`CloudBrowserBookmarkStore` 落一份**只存地名、一个凭据字段都没有**的 `cloud-bookmarks.json`，窗口那两个下拉与「收藏此目录」按钮读的还是同一批行），并把端点重命名从三份文件扩到**四**份。样板就此是：**纯函数定规则 + 假 RC 驱动真窗口 + 需要弹窗／对外副作用的决定走接缝 + 需要落点或答案形状才上真 rclone**。⚠️ **B11 动手前先按 §6 实证**（rclone 是否暴露版本列表；B5 那条 `use_trash` 已由 §6 第 1 条答复：RC **根本没有** trash 动词，所以回收站只能是应用自己的文件夹）。全量门禁 2026-10-04 再跑，`Failed: 0`（**Core 425 / UI 476**，UI 3m7s；Core 自 B7 起不再变（B8 只动 UI 侧），UI 的 +34 是 B8 的 32 条书签 + 2 条重命名）。 |
| **最容易踩的坑** | 挂载选项键名拼错会被 rclone **静默忽略**；`config/create` 对 OAuth 后端会**重新要求浏览器授权**；rclone **不读 `ALL_PROXY`**；**`operations/stat` 里「校验值」只有把名字列进 `opt.hashTypes` 才会给**，而 `opt.hashes`（位域）与 `opt.HashTypes`（大小写）都被**静默忽略或报错** —— 拼错读起来就跟「这个后端没有校验值」一模一样；**`operations/publiclink` 里多写的字段同样被静默忽略**，而它那句「不支持公开链接」的 500 会被共用分类器读成**临时连接中断**（先读文字再看 category）；重建 WPF 列表会抛一次「无选中」事件（见 §3 的设计要点）；**进程里只能有一个 WPF `Application`，且只有创建它的线程看得见它**（见 §4 的 WPF 表 —— B1 的异步窗口测试就是在这里反复失败的）；**接缝返回 `null` 是「用户取消」，不是「没装接缝」**——用 `??` 兜到真弹窗会在测试里打开一个无人可答的对话框，并且 `Window.Owner` 指向一个从未 `Show()` 过的窗口时直接抛（B2 撞到过一次测试宿主崩溃）；**`operations/movefile` 对已存在的目标名是覆盖**、`sync/move` 落到已存在的目录是**合并并把源留成一个空壳**，而 `operations/rmdir` 打**已不存在**的路径答 HTTP 500、被共用分类器读成 `Temporary`（于是 `RemoveIfEmptyAsync` 先 `stat`，不拿 rmdir 的报错当「目录没有了」）（B5）；**键盘的坑是「两处写」而不是「按不动」**：快捷键一旦自己写一遍拒绝语或确认，就和按钮分岔（B6 的做法是命令只调按钮已有的方法）；`TextBox` 会**自己吃掉** Del 与 Ctrl+A、却**不吃** F2，所以 typing 闸门只对 F2 这类键真正起作用；`Keyboard.Modifiers` 读的是**物理**键盘，合成事件改不了它（见 §4 的 WPF 表）；**网格视图必须 `ClearValue` 复原，不能写 `ItemsPanel = null`** —— 本地 null 让**一行都不呈现**而集合完整，进程内的 `Assert.Null` 会把这个 bug 读成正确（B7）；**桌面场景跑的是 `tests/FengSync.UiTests/bin/` 里那份 `FengSync.exe`**，只建 `src/FengSync` 会让场景**绿在它本该检查的那个改动上**（B7 排查时踩过一次）；**跳转必须「接管」端点，不能只「跟随选中」** —— 跟随会先为目标端点列一次它自己的根，一次跨端点跳转因此打**两**次 `operations/list`（B8 的 `SwitchEndpoint` 直接把那一行采纳成当前端点，并有一条断言数死列表调用的次数）；**收藏文件的路径在 store 构造时就定下来**（`AppDataPaths.Root` 本身每次都认 `FENGSYNC_DATA_DIR`，但一个提前建好的 store 之后不受它影响），所以驱动窗口的测试必须**在建窗口之前**把 `FENGSYNC_DATA_DIR` 换进临时目录并在 `finally` 还原 —— 否则窗口会继续往用户真实的 app data 里写、并在下一次运行读回来；**进程级文件锁里的 await 若回到调用方的调度器，锁就再也解不开** —— 一次没被接应的书签写入能让之后每一条写书签的窗口测试各卡 90 秒（B8 实测：`save held` 之后没有 `save release`；单跑一条又全绿，最容易误判成「别处的 flake」），所以 gate 区间一律 `ConfigureAwait(false)`，并按第 8 条测试的写法「发出写入就退出的线程」去证它真的会 fire；**一份用户选择可以有第三份表示** —— 磁盘上的文件、纯规则类、以及窗口内存里那份列表；B8 唯一的产品缺口（重命名搬对了磁盘那份，窗口却拿旧名字的下一次 listing 把文件写回旧名）就是**只有真实外壳**抓到的，32 + 18 条进程内测试全绿。改动任何一份持久表示时，先问「谁还内存着同一份」。 |

---

## 1. 接手先做的三件事

### ① 门禁照跑，基线已经自证过（2026-10-04）

```powershell
pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline
```

A3–A6 是分批提交的，所以接手时先自证过一次基线；B0 提交前后各跑了一次全量门禁，两次都是 `Failed: 0`
（**Core 390 / UI 264**，UI 阶段约 **3m08s**）。§0 说的 `UiFeatureBehaviorTests` 超时在本环境**没有复现**。
**B1 收尾时又跑了一次：`Failed: 0`，Core 392 / UI 281，UI 阶段 3m01s**（读数随批次上涨，别追死数字）。
**B2 收尾再跑一次：`Failed: 0`，Core 392 / UI 301，UI 阶段 3m** —— B2 只动了 UI 侧的规则与窗口，Core 计数没变。
**B3 收尾再跑：`Failed: 0`，Core 399 / UI 331，UI 阶段 3m07s** —— B3 第一次往 **Core** 侧加真 rclone 测试
（`RcloneStatFactTests` 7 条，所以 Core 从 392 起跳），UI 侧加的是纯规则 12 条 + 真窗口 12 条。
**B4 收尾再跑：`Failed: 0`，Core 403 / UI 354，UI 阶段 3m10s** —— B4 往 Core 加 4 条真 rclone
（`RclonePublicLinkFactTests`：整个类在自己的 `IAsyncLifetime` 里起一个 loopback `rclone serve s3` + 一个 `rcd --rc-serve`，
四条都跑在这套之上），UI 加纯规则 13 条 + 真窗口 10 条。
**B5 收尾再跑：`Failed: 0`，Core 415 / UI 383，UI 阶段 3m8s** —— B5 往 Core 加 10 条真 rclone
（`CloudRecycleBinFactTests`：一条 `alias` remote 上钉 movefile 的覆盖/建父目录/拒落到目录、`sync/move` 的合并与空壳、
rmdir 的 500 措辞、mkdir 的幂等与「不许顶掉同名文件」）+ 2 条守卫（`UiTestAwaitGuardTests`），
UI 加纯规则 13 条 + 真窗口 17 条。中途一次全绿是 Core 413 / UI 383（3m4s），那时守卫两条还没加。
⚠️ B5 的门禁连跑三次，其中两次在同一处 fail：`RealWinFspMountIntegrationTests` 收尾删临时目录时
`Access to the path 'mount' is denied`（第 53 行），单跑该类 **382 ms 通过** —— 是 WinFsp **异步 detach** 与
`Directory.Delete(root, true)` 的竞态，**与本轮改动无关**，但在全量负载下**三次里两次**会撞上。
已单独修那条 teardown（`DeleteDespiteDetachRaceAsync`：最多等 15 秒，等不到就**照原样抛**，所以一个真卡住的挂载
仍然会让测试失败，不是把竞态盖掉）；修完 Core 连跑两遍 415/415。日后看到这一处 fail，先读 `Error Message`
再决定是不是真的挂载没释放。
**B6 收尾再跑：`Failed: 0`，Core 415 / UI 402，UI 阶段 3m1s** —— B6 只动 UI 侧（一张纯表 + 真窗口路由 + 命令派发），
所以 Core 计数与 B5 收尾一字不差；19 条新测试全在 `CloudBrowserShortcutTests`（3 条纯表 +
5 条真实冒泡 `KeyDown` 证明处理器挂在浏览器面板上 + 11 条命令本身）。这一轮**没有**再撞上上面那条 WinFsp flake。
**B7 收尾再跑：`Failed: 0`，Core 425 / UI 442，UI 阶段 3m3s** —— Core 的 +10 里有 9 条是 B7 前置 `54eb6f5` 那 9 条
`RcloneServeReadFactTests`（每条都打 bundled 二进制的 `--rc-serve` 有界读：range 越过对象末尾被夹而不是 416、
超上限仍报总大小、对象不存在答 404 而目录答 500 那句会被共用分类器读成连接问题、0 字节与起始越界的
`Content-Range` 是**任何解析器都不必读**的形状），第 10 条是 `LiveTextAccessibilityTests` 新加的 XAML 静态栅格
（浏览器那一行必须由容器自己带绑定来的名字）；UI 的 +40 是 `CloudBrowserGridViewTests` 20 +
`CloudBrowserPreviewTests` 20。这一轮也**没有**撞上 WinFsp flake。
**B8 收尾再跑：`Failed: 0`，Core 425 / UI 476，UI 阶段 3m7s** —— Core 计数与 B7 收尾一字不差，因为 B8 只动 UI 侧；
UI 的 +34 = `CloudBrowserBookmarkTests` 32 条（纯规则 10 + 文件 9 + 真窗口 13）+ `CloudEndpointRenameTests` 2 条。
⚠️ **这一轮中途红过一次**，而且是本批唯一一次由**真实外壳**抓出来的产品缺口：476 里 1 条 fail
（`UiAcceptanceTests.Sftp_endpoint_is_created_and_selected_through_the_real_endpoint_ui` →
`Missing UI element containing: ui_renamed_…`，`Invoke-UiScenario.ps1:339`）。当时 32 + 18 条进程内测试**全绿**，
根因是窗口内存里那两份书签列表 = 同一份用户选择的**第三份表示**（详见 §2 的 B8 那条 ⚠️）。
另两处红不是产品问题，都在 §2 的 B8 ⚠️ 里记着：一次**进程级文件锁**卡在停止 pumping 的线程上
（3 条 `CloudBrowserShortcutTests` 各卡 90 秒，**单跑任何一条又全绿**——最容易误判成别处 flake 的那种形状），
和一条**踩在时序上**的 B6 `args.Handled` 断言。这三处都不是"改测试让它过"：第一、第三条改了产品，第二条
是把断言挪回它真正能读到的时刻并写清为什么。
差值不必精确对上（上一行的 301 是 B2 收尾时的读数），以 `Failed: 0` 为准。

- 约 **3m30s**（UI 阶段占大头）。调用工具时**超时必须给 600000ms**，否则会被掐断在半路。
- 判定标准：日志里出现 **`Passed!  - Failed: 0` 汇总行**。只看日志尾部会误判 —— 被超时掐断时日志正好停在
  `A total of 1 test files matched the specified pattern.` 之后，看起来像正常进行中。
  ⚠️ 后台跑时还会遇到第二次同样的误判：**Bash 后台任务会在 UI 阶段还在跑的时候就报 completed**（父 pwsh 退出、
  `dotnet test` 孙进程继续），日志尾部暂时只有 Core 那一行。以「UiTests 那行的 `Passed!` 出现」为准，别信任务通知。
- 日志**写 `$env:TEMP`**，不要写进 `.fengsync-test/`：那个目录会被 `Reset-TestArtifacts` 清空，
  文件被占用时脚本 3 秒就退出（`The process cannot access the file ... because it is being used by another process`）。
  另坑：在 bash 工具里执行 pwsh 时，重定向要写成 `> /c/Users/.../x.log 2>&1`；写 `*>` 会被 bash 先展开，
  结果 `Test-All.ps1` 收到 `Configuration=AGENTS.md` 并报「Cannot validate argument on parameter 'Configuration'」。
- 读数会比文档 §3 的旧预期更高（A5/A6/B0 各加了测试）；以 `Failed: 0` 为准，不追死具体数字。
- **单独跑一个测试类**（改窗口代码后的快速回路）时，别把 `dotnet test` 接进管道：`... | tail -20` 会让日志在进程
  结束前一个字都不落盘，看着像卡死。写成 `> "$TEMP/x.log" 2>&1` 再 `tail`。B1 用的过滤器
  `--filter "FullyQualifiedName~CloudBrowserPaste|FullyQualifiedName~CloudTransferWindowBehavior"`（19 条）**1 秒**跑完。

### ② A7、B0、B1、B2、B3、B4、B5、B6、B7、B8 已实现并提交 ✅

A7 按 `feat`/`test`/`docs` 三层提交（`60733ae` / `d58cd4a` / `344d3cd`）。A1 的桌面场景缺口在进 B 组前补上
（`fd12512`，见 §3 遗留缺口）。B0 队列泛化同样三层：`af6fc4c` feat / `85160a9` test / `6be98b9` docs
（本节的这条 commit 行与 §2 表末行是 `6be98b9` 之后回填的）。**B1 浏览器粘贴**照旧三层：`3c9322b` feat /
`cd74a61` + `526a634` test / `4656a58` docs（本节的 hash 由 `4656a58` 的下一次回填补上）。
**B2 同名冲突**照旧三层：`dedc2b5` feat / `1649dc6` test / `0349467` docs（本节的这条 hash 行与 §2 表末行是
`0349467` 之后回填的）。**B3 属性/详情**照旧三层：`844b93b` feat / `7370af0` test / `ef211a3` docs（本节的这条 hash 行与 §2 表末行是
`ef211a3` 之后回填的）。**B4 分享链接**照旧三层：`3d1d532` feat / `1b4b7ed` test / `b5c0c91` docs（本节的这条 hash 行与 §2 表末行是
`b5c0c91` 之后回填的）。**B5 回收站**照旧三层：`f61c1a1` feat / `0bbea38` test / `7d013a6` docs，外加两条**不属于
回收站**、由这一批顺手查实并修好的测试提交：`a11b3f5`（B4 的十条窗口测试因为没 `await` 而从未真正断言，现在有了
`UiTestAwaitGuardTests` 守着）与 `e66d42c`（`RealWinFspMountIntegrationTests` 的 teardown 与 WinFsp 异步 detach 竞态，
是 B5 门禁连跑三次的两处 flake 之一）。本节的这条 hash 行与 §2 表末行是 `7d013a6` 之后回填的。
**B6 键盘快捷键**照旧三层：`f45e06b` feat / `2299460` test / `1893946` docs（本节的这条 hash 行与 §2 表末行是
`1893946` 之后回填的）。
它没有往 Core 加东西，也没有新造一条产品路径：一张纯表把按键映射成命令，命令一律调按钮本来就在调的那个方法。
**B7 网格视图 / 缩略图 / 内置预览**拆成四层：`1334311` feat(core)（`AppDataPaths.CloudThumbnails` +
`ApplicationSettings.CloudBrowserView` + 校验）/ `c59ac45` feat（三个新服务文件 + `CloudPreviewPrompt` + 窗口两种画法）/
`c66bb6c` test（UI 40 条 + 1 条 Core 侧 XAML 静态栅格 + `sftp-ui` 两条 toggle 断言）/ `546e5b5` docs（AGENTS.md 三条实测规则 + 本文件），
前面还有一条**不属于本批产品路径**的 Core 地基 `54eb6f5`（`--rc-serve` 有界读 + 9 条事实）。
本节的这条 hash 行与 §2 表末行是那四条之后回填的。
**B8 收藏 / 最近访问**拆成七条：`0686a4a` feat（两个新服务文件，含 gate 区间全部 `ConfigureAwait(false)`）/ `ad66e38` feat（窗口的两个下拉与「收藏此目录」按钮，含重命名后**先重读书签再刷新**那一行）/
`73aed4e` feat（重命名搬第四份文件）/ `fad7f90` test（`CloudBrowserBookmarkTests` 32 条 + 5 个既有 harness 的数据目录隔离 + B6 那条 F2 断言改成事后读）/
`121acf1` test（`CloudEndpointRenameTests` 那条拆成两份 + 新增的第四份恢复）/ `a749142` test（`Invoke-UiScenario.ps1` 的收藏与重命名步骤）/ `61d60a4` docs（AGENTS.md 的规则 + 本文件）。
本节的这条 hash 行与 §2 表末行是那几条之后回填的。
**A 组 + B0 + B1 + B2 + B3 + B4 + B5 + B6 + B7 + B8 至此收尾。**

### ③ 提交粒度（一直照这个走）

按层次拆 `feat` / `test`（`docs` 单独），用**显式路径** `git add`（**不要 `git add -A`**）。**只提交不 push。**

---

## 2. 已完成的工作

| commit | 批次 | 内容 |
| --- | --- | --- |
| `88c7a15` | 0 | `chore(tech-debt)` —— 技术债 7 项（24 文件） |
| `b1631c4` | A0+A1 | `feat(endpoints): provider catalog, and OneDrive/Dropbox/Box/WebDAV`（21 文件） |
| `e18462d` | A2 | `feat(endpoints): rename an endpoint by rewriting its rclone.conf section` |
| `324ff7e` | A2 | `test(endpoints): cover endpoint rename, its rollback and profile migration` |
| `23a945f` | A2 | `test(ui): rename through the real shell, and tolerate a collapsed progress window` |
| `5bafb2b` | — | `docs: replace the gap analysis with a single handoff document` |
| `65de367` | A3+A4 | `feat(endpoints): search, order and check every endpoint from the list` |
| `1264812` | A3+A4 | `test(endpoints): cover the endpoint list's search, order and batch probe` |
| `52b50ac` | A5 | `feat(settings): let the user pin a proxy for the rclone processes we start` |
| `8a6995a` | A5 | `test(proxy): cover the setting, the resolver fallback and the shell wiring` |
| `f4c2476` | A6 | `feat(endpoints): file endpoints under groups and tag them` |
| `6d2001d` | A6 | `test(endpoints): cover grouping, tagging and the heading each row carries` |
| `60733ae` | A7 | `feat(endpoints): export and import endpoints without their credentials` |
| `d58cd4a` | A7 | `test(endpoints): cover the export shape and the import file's validation` |
| `344d3cd` | A7 | `docs: record A7 done and archive A3-A6 in the handoff` |
| `fd12512` | A1 缺口 | `test(ui): walk every provider form in the endpoint editor`（7 张 descriptor 表单全走一遍，含「不得出现别家字段」） |
| `af6fc4c` | B0 | `feat(transfers): copy between two cloud endpoints through the queue` |
| `85160a9` | B0 | `test(transfers): cover the copy payload and where real rclone writes it` |
| `6be98b9` | B0 | `docs: record B0 done and the copy invariants that B1 must keep`（AGENTS.md 不变式 + 本文件） |
| `3c9322b` | B1 | `feat(browser): copy, cut and paste files between cloud endpoints` |
| `cd74a61` | B1 | `test(browser): cover the paste decisions and the calls they make` |
| `526a634` | B1 | `test(browser): cover the cut whose source delete was refused` |
| `4656a58` | B1 | `docs: record B1 done and the paste rules that B2 must keep`（AGENTS.md 不变式 + 本文件） |
| `dedc2b5` | B2 | `feat(browser): keep both files when a cloud name is taken` |
| `1649dc6` | B2 | `test(browser): cover each conflict answer and the renames they authorise` |
| `0349467` | B2 | `docs: record B2 done and the conflict answers that B3 must keep`（AGENTS.md 不变式 + 本文件） |
| `844b93b` | B3 | `feat(browser): read one object's details instead of trusting the listing row` |
| `7370af0` | B3 | `test(browser): pin the properties panel and the stat facts it rests on` |
| `ef211a3` | B3 | `docs: record B3 done and the stat facts that B4 must keep`（AGENTS.md 不变式 + 本文件） |
| `3d1d532` | B4 | `feat(browser): hand out a cloud link only after saying what it grants` |
| `1b4b7ed` | B4 | `test(browser): pin the link shape against a real S3 server` |
| `b5c0c91` | B4 | `docs: record B4 done and the link facts that B5 must keep`（AGENTS.md 不变式 + 本文件） |
| `f61c1a1` | B5 | `feat(browser): move files into an endpoint's own trash instead of deleting them` |
| `0bbea38` | B5 | `test(browser): cover what the trash takes from the cloud and what it says back` |
| `a11b3f5` | B5 顺手 | `test(ui): await the window harness so its assertions can fail`（B4 的十条窗口测试**从未真正断言**；`UiTestAwaitGuardTests` 把这条变成会失败的检查） |
| `e66d42c` | B5 顺手 | `test(mount): remove the temporary tree through WinFsp's asynchronous detach`（B5 门禁连跑三次的两处 flake 之一，根因是 teardown 竞态） |
| `7d013a6` | B5 | `docs: record B5 done and the trash facts that B6 must keep`（AGENTS.md 规则 + 本文件） |
| `f45e06b` | B6 | `feat(browser): give the file browser a keyboard that only routes` |
| `2299460` | B6 | `test(browser): pin the keystroke table, its typing gate and the pane it belongs to` |
| `1893946` | B6 | `docs: record B6 done and the keystroke rules that B7 must keep`（AGENTS.md 规则 + 本文件） |
| `54eb6f5` | B7 前置 | `feat(core): read at most the first N bytes of a cloud object through --rc-serve`（Core 侧有界读 + `RcloneServeReadFactTests` 把状态形状钉成 bundled v1.75.0 实测的那几种） |
| `1334311` | B7 | `feat(core): remember the browser's view and give its tiles a cache folder` |
| `c59ac45` | B7 | `feat(browser): draw the same rows as tiles, and preview only what we can really render` |
| `c66bb6c` | B7 | `test(browser): pin the tile round, the preview refusals and both drawings of one list`（UI 40 条 + 1 条 Core 侧 XAML 静态栅格 + `sftp-ui` 两条 toggle 断言） |
| `546e5b5` | B7 | `docs: record B7 done and the view and preview rules that B8 must keep`（AGENTS.md 规则 + 本文件） |
| `0686a4a` | B8 | `feat(browser): keep the places worth going back to`（`CloudBrowserBookmarks` 纯规则 + `CloudBrowserBookmarkStore` 落盘） |
| `ad66e38` | B8 | `feat(browser): offer the remembered places as jumps and a favourite button`（两个下拉 + 一个跟随目录的按钮，含重命名后先重读书签那一行） |
| `73aed4e` | B8 | `feat(endpoints): move the remembered places with a renamed endpoint`（重命名一起搬第四份文件） |
| `fad7f90` | B8 | `test(browser): pin the bookmark rules, the file it writes and the jump it drives`（32 条 = 纯规则 10 + 文件 9 + 真窗口 13；另把 5 个既有窗口 harness 的 `FENGSYNC_DATA_DIR` 隔离补齐，并把 B6 那条同步读 `args.Handled` 的 F2 断言改成事后读） |
| `121acf1` | B8 | `test(endpoints): prove the rename carries the bookmarks and unwinds all four files` |
| `a749142` | B8 | `test(ui): walk the favourite button, a jump and the renamed bookmark in the real shell` |
| `61d60a4` | B8 | `docs: record B8 done and the bookmark rules that B9 must keep`（AGENTS.md 规则 + 本文件） |

### 批次 0 —— 技术债 7 项（`88c7a15`）

`Endpoints.cs` 抽 `ReadPreferredHash(JsonElement)`（`StatAsync` 与 `ScanEntriesAsync` 同口径读 `Hashes`）；
`AppDataPaths.MountCache` + `RcloneDaemon.BuildArguments(configPath, port, cacheDirectory)` 纯函数化（含 `--cache-dir`、`--rc-serve`）；
`Mount/MountOptions.cs` 删死代码、`MountAsync` 返回值 `Task<Guid>` → `Task`；
`FengSync.Cli` / `FengSync.Updater` 补 `<Version>`；`.gitignore` 加 `.claude` 与 `.workbuddy-ai/`；
`RcloneUiError.DescribeDetail` + `CloudTransferRecord.ErrorDetail`；`MountPickerDialog` 盘符分支改走 `MountPointInspector.Validate`；
**12 处动态文本 `TextBlock` 删静态 `AutomationProperties.Name` 并补 `AutomationId`**（`AGENTS.md` 规则）。
新测试：`EndpointSchemesTests`、`RcloneDaemonArgumentsTests`、`ExecutableVersionTests`、`LiveTextAccessibilityTests`，`CloudFeatureBehaviorTests` +4。

### A0 —— Provider descriptor 重构（`b1631c4`）

把原来 12+ 处散落的 switch / `StartsWith` / 硬编码 XAML 面板收敛成两层单一来源：

- **`src/FengSync.Core/EndpointSchemes.cs`** —— 全仓库唯一的 scheme ↔ rclone backend ↔ `EndpointType` 映射表。
  API：`AllSchemes` / `CloudTypes` / `SchemeFor` / `RcloneTypeFor` / `DisplayNameFor` / `IsRemote` / `SchemeOf` /
  `TypeForScheme` / `TypeForRcloneType`（`alias` → S3）/ `IsSelfDescribingRcloneType` / `BuildUri` /
  `RemoteNameOf` / `RenameRemote`。
- **`src/FengSync/Services/CloudProviderCatalog.cs`** —— UI 侧描述符：`CloudFormField(Key,Label,Kind,Required,AutomationId,DefaultValue,Hint,EditLabel)`、
  `CloudProviderDescriptor(Kind,NamePlaceholder,NameAutomationId,RootAutomationId,Fields,RequiresOAuth,SupportsTrash,SupportsPublicLink,DefaultRoot,ConstantParameters,CrossFieldRule)`
  + 派生 `RcloneType`/`UriScheme`/`DisplayName`/`IsS3Compatible`/`MergeParameters`；**7 条描述符，Google Drive 排第一**。
- `ProviderKind` 枚举**已彻底删除**，全部改用 `EndpointType`；`CloudEndpointService.ProviderKind` 不再存在。
- ⚠️ **`EndpointType` 的枚举值只能追加**：`BaselineTransaction` 与 `TransferResumeStateStore` 都持久化 `Type.ToString()`。

### A1 —— 4 个新 Provider（`b1631c4`）

新增 **OneDrive / Dropbox / Box / WebDAV**；`RcloneOAuthFlow.CreateGoogleDriveAsync` → `CreateAsync(remoteName, descriptor, fields, ...)`。
S3 兼容的腾讯 COS / 阿里 OSS / 华为 OBS / 七牛 / 网易：**零代码路径**，rclone 的 s3 backend 自带这些 provider，
只需调 `PreferredS3Providers` 排序。

### A2 —— 端点重命名（`e18462d` + `324ff7e` + `23a945f`）

**实现时推翻了原计划的做法**，因为实证发现「`config/create` → 迁移 → `delete`」走不通：

- 实测（bundled rclone v1.75.0，RC `config/create`）：对 OAuth 后端（drive/onedrive/dropbox/box），
  `config/create` **一定会跑 backend 的配置状态机** —— 返回 `State: "*oauth-confirm,teamdrive,oauth,"`，
  继续驱动会走到 `oauth-islocal → oauth-authorize`（**要浏览器**）。`opt.nonInteractive=true` 不会跳过，只自动取默认值。
  所以「用 `config/create` 克隆一个 OAuth 端点」= **要求用户重新授权**，而不是复制 token。
  对照：`sftp` 的 `config/create` 返回没有 `State` 字段，是干净的。
- 实测：**运行中的 `rclone rcd` 会重新读取 `rclone.conf`** —— 手写/改写一个段之后，下一次 `config/listremotes`
  与 `config/get` 立刻能看到新名字与新内容；随后 daemon 自己调 `config/create` 写回配置时**不会覆盖**手写进去的段。
  → **不需要重启 daemon**。

最终做法：**直接改写 `rclone.conf` 的段**（值逐字节不变）→ 用 daemon 回读验证 → 再迁移两个 JSON → 任一步失败**三个文件一起还原**。

- `src/FengSync.Core/RcloneConfigFile.cs`（新）—— 纯 ini 段手术：`ContainsSection` / `RenameSection(content, old, new, valueOverrides?)` / `RemoveSection`。
  按 `\n` 切分（`\r` 留行尾）以保住 CRLF 与「末尾有没有换行」；**段头只认第 0 列**，所以多行值（PEM）的缩进续行永不被误判成段头。
- `EndpointSchemes.RenameRemote` 用 **Ordinal** 比较：改 `docs` 绝不碰 `docs2`，也不碰 `C:\docs` / `\\server\share\docs`。
- `src/FengSync/Services/CloudEndpointRename.cs`（新）—— `InspectAsync` / `Validate`（纯函数）/ `RewriteProfiles` / `RenameAsync`。
- UI：`RemoteEndpointManagerWindow` 的端点按钮第二行加「重命名」。
  ⚠️ 按钮名叫 **`RenameEndpointButton`**（不是 `RenameButton`）—— 文件浏览器已有一个 `RenameButton`，重名会 `CS0102`。
- 测试：`RcloneConfigFileTests`（Core，10）、`CloudEndpointRenameTests`（16，含真 rclone 的 SFTP 与 S3 alias 端到端 + 两个注入失败的还原用例）、
  `UiFeatureBehaviorTests` +2；`sftp-ui` 桌面场景加了「改名 → 再浏览 → 用新 URI 同步」。
- `AGENTS.md` 里有一条对应的不变式（搜 `Renaming an endpoint cannot go through rclone`），改这块前先读它。

**顺带修掉一个门禁 flake**（`23a945f`）：`Invoke-UiScenario.ps1` 的 `Capture-Element` 在桌面无关模式下仍先做
「元素必须有可见矩形」的检查，再判断「截图跳过」；一个完成太快的本地同步（进度窗正在收起、矩形已归零）
会让整个套件失败（`local` 场景就这么挂过）。已把该检查移进真正的截图分支。

### A3+A4 —— 端点搜索/排序 + 「测试全部」（`65de367` + `1264812`）

- `src/FengSync/Services/CloudEndpointList.cs`（新）—— 纯函数：`CloudEndpointSortColumn { Name, Provider, Health }`；
  `Matches(account, query)`（**每个**空白分词都要命中 `name + provider + type + prefix + Display`，第二个词是收窄）；
  `Arrange(rows, query, column, descending)`（名称**恒为最终 tie-break 且不反转**）；`Rank(health)` 升序 = 待办清单
  `NeedsLogin → Unreachable → Unknown → Checking → Connected`。
- `CloudEndpointStatusService` 加 `ProbeConcurrency = 3` + `RunBoundedAsync<T>`（`SemaphoreSlim` 限流、单条失败被收集、取消不算失败、`acquired` 防误释放）。
- UI：`RemoteEndpointManagerWindow` 端点 GroupBox 改为 2 行 Grid（`Auto` 工具条 + `*` 列表），工具条是 **`WrapPanel`**
  （左列仅 340px，必须能换行）：`CloudEndpointSearch` / `CloudEndpointSort` / `CloudEndpointSortDirection` / `CheckAllCloudEndpoints`。
- 测试：`CloudEndpointListTests`（13）、`CloudEndpointCheckAllTests`（12）、`UiFeatureBehaviorTests` +1 个 STA 测试；`sftp-ui` 场景加 4 个控件矩形断言与搜索/排序/全检回读。

### A5 —— 代理（`52b50ac` + `8a6995a`）

**机制原本就在，缺的只是接线**：`RcloneProxyOptions` / `RcloneProxyResolver.Resolve`（优先级 Application > Environment > WinInet）
与 `RcloneDaemon.StartAsync(..., RcloneProxyOptions?)` 早已接受代理，但没人从设置里读。

- `ApplicationSettings.RcloneProxy`（**注意字段名是 `RcloneProxy`，不是计划里写的 `CloudProxy`**）。命名跟随 rclone 而非页面：
  每个进程只有一个 daemon，代理在**进程启动时固定**，它同时管挂载、SFTP 服务和同步运行 —— 所以设置页文案**没有**
  沿用计划里「仅影响云盘」的说法。
- `RcloneProxySettings.From(settings)`（Core）把设置转成 `RcloneProxyOptions`；`LoadAsync` 读盘后解析。
- `RcloneLifecycleHost.ProxyOptionsProvider`（`Func<CancellationToken, Task<RcloneProxyOptions?>>?`）在启动锁内、
  子进程创建前**只调一次**；provider 抛异常则**记日志并回退到环境**，绝不因此拒绝启动 daemon。
- **两个入口都接线**（`FengSync.App` 与 `DriveHub.App`）—— 各自独立 daemon，接一个不接另一个会静默失效。
- ⚠️ **实证修正**：rclone 的 HTTP 客户端**不读 `ALL_PROXY`**（用死端口逐个验证：`ALL_PROXY` 那轮仍能到达目标，
  `HTTP_PROXY`/`HTTPS_PROXY` 那轮 `proxyconnect tcp` 失败）。`RcloneProxyResolver` 现在会把只有 `all_proxy`
  的值镜像进 `http`/`https`。地址只校验**可达性**（不能含空格、必须有 host），不校验 scheme。
- 测试：`RcloneProxySettingsTests`（Core）、`RcloneProxyResolverTests`（Core）、`CloudProxySettingsUiTests`（UI）；`sftp-ui` 场景加了设置往返。

### A6 —— 分组/标签（`f4c2476` + `6d2001d`）

计划里写的是「给 `CloudEndpointMetadata` 加 `Group`/`Tags`」，实现把**业务逻辑**抽成纯函数而非堆在窗口里：

- `CloudEndpointMetadata` 加 `Group`（空 = 未分组）与 `Tags`（`IReadOnlyList<string>?`）。两者都是**本应用自己的概念**，
  rclone 无处存放，只进 `cloud-endpoints.json`，**绝不进 `rclone.conf`**；旧文件缺这两个字段时按 null 读，向后兼容。
- `src/FengSync/Services/CloudEndpointOrganization.cs`（新）—— `NormalizeGroup` / `NormalizeTags` / `ParseTags`
  （接受全角 `，；`、顿号 `、`、换行）/ `FormatTags` / `GroupLabel`（未分组统一显示为「未分组」）/ `Merge`（保存分组时
  优先用盘上记录，端点是手工加的、没有 metadata 时新建一条，保证「保存分组绝不静默失败」）/ `Preserve`（创建/编辑端点
  重建记录时**保留**用户的分组与标签 —— 否则改一次根目录就把归档抹掉）/ `Describe`（状态文案，单独可测）。
- UI：`CloudEndpointOrganizationWindow`（新）编辑单个端点的分组/标签；`RemoteEndpointManagerWindow` 列表**按组分区**显示，
  搜索也命中标签。
- 测试：`CloudEndpointOrganizationTests`（Core/UI 纯函数 ~195 行）、`CloudEndpointListTests` +116、`UiFeatureBehaviorTests` +95；`sftp-ui` 场景加了组织编辑往返。

### A7 —— 端点导出/导入（**已提交**：`60733ae` + `d58cd4a` + `344d3cd`）

- `src/FengSync/Services/CloudEndpointPortability.cs`（新）—— 纯逻辑与不可信输入校验：
  `CloudEndpointExport` / `CloudEndpointExportBundle` / `CloudEndpointDraft` / `CloudEndpointImport`；
  `Serialize` / `ToExport` / `Parse` / `ValidateRemoteName` / `ValidateParameterName` / `UniqueName`；
  源生成 context 用 camelCase + `WriteIndented`，文件可读且可 diff。
- **导出只读参数名，不读值**：`CloudEndpointService.LoadParameterNamesAsync(account)` 取
  `GetRemoteParametersAsync` 的 `Keys`（S3 强制前缀端点读隐藏 backing remote），`BuildExportBundleAsync` 组装。
  导出文件的结构里**没有放凭据的字段** —— 安全来自形状，而不是靠调用方自觉。
- **导入是不可信输入**：`Parse` 逐条校验 —— 版本必须为 `1`；`type` 必须能映射到 `EndpointSchemes` **且**有
  `CloudProviderCatalog.TryFor` 的 descriptor；remote 名拒绝 `:` `/` `\`、前后空白、以 `.` 开头、超 64 字符
  （防段头注入）；参数名只允许字母数字与 `_ -`；同文件重名按大小写不敏感拒掉后来的。坏行丢弃并返回消息，
  不让一行毁掉整个迁移。
- `CloudEndpointService.SaveOrganizationAsync` 在编辑器创建端点后补写分组/标签（编辑器不负责归档）。
- `CloudEndpointEditorWindow` 新增 `CloudEndpointEditorWindow(CloudEndpointDraft)` **预填新建**模式：固定服务、
  预填名称与根目录，名称可编辑、凭据全空，保存走既有 `EnsureRemoteAsync` / `SaveS3Async` 路径。
  `LoadProvidersAsync` 与 `ApplyProviderDefaults` 对 draft 各加了一处保护（不覆盖预填的 provider、不 seed 默认 region）。
- UI：`RemoteEndpointManagerWindow` 工具条加 `导出…` / `导入…`（`ExportCloudEndpoints` / `ImportCloudEndpoints`，
  Tertiary 样式）。导出走 `SaveFileDialog`；导入走 `OpenFileDialog` → 校验 → 逐端点打开编辑器，**冲突名用
  `UniqueName` 自动改名而不是覆盖**，取消即跳过，结束时刷新并汇总“成功/跳过”。
- 测试：`CloudEndpointPortabilityTests`（11 纯函数：导出形状、不含凭据、往返、版本、坏 JSON、注入名、未知类型、
  坏参数名、重名、`UniqueName`）；`UiFeatureBehaviorTests` +2 STA（draft 预填 SFTP 与 S3）；`sftp-ui` 场景的
  工具条矩形断言加上两个新控件。

### B0 —— 队列泛化：远端→远端复制（**已提交**：`af6fc4c` + `85160a9` + `6be98b9`）

- `EndpointTransfer.CopyServerSideAsync(source, target, sourcePath, destinationPath, operationId, totalBytes,
  activeTransfers, progress, ensureDestinationAsync, ct)`（`src/FengSync.Core/Execution/EndpointTransfer.cs`）——
  两端都必须是 `RcloneEndpoint`，否则**在任何字节移动之前**抛 `NotSupportedException`；不 staging、不 publish，
  直接把 `destinationPath` 交给 `operations/copyfile`。本地端点被拒是**有意的**：直接写目标名会绕过
  Feng Sync 的临时文件 + 原子发布，一个取消的复制就会留下与完整文件无法区分的截断文件。
- `CloudTransferDirection` 追加 `Copy`（**枚举只能追加**：journal 存的是数字）。
- `CloudTransferTask` 加 `TargetUri` / `TargetPath`，`CloudTransferRecord` 加两个**可选尾字段**（照 `ErrorDetail` 的做法，
  旧 journal 文件照样能读）。⚠️ **与本文原计划的偏差**：B0 原来只写 `TargetUri`，实现时发现跨端点粘贴还需要一个
  **与源路径无关的目标路径**，所以 `TargetPath` 一起做掉了 —— 和 A2 那次一样，属于实现期修正，后续 B1 直接用它。
- `CloudTransferQueue` 加 `EnqueueCopy(源URI, 源路径, 目标URI, 目标路径, 大小)`、`HasCopyTasks`，
  `RunAsync` 的 `Copy` 分支：两端都 `EndpointFactory.CreateWithClient(uri, client)`，**跳过**
  `CopyToStagingAsync` / `PublishAsync` / `PrepareRetryAsync` / `DiscardStagingAsync`（finally 里的丢弃也带 `!isCopy` 守卫）。
  缺目标（只有手改的 journal 文件能造出这种行）在**建端点之前**就失败，不发任何 RC 请求。
- 构造参数加了 `Func<CancellationToken, Task<RcloneRcClient>>? client`（默认仍走 `AppServices.Current.RcloneHost`）——
  这是 `CloudFileManagerService` 早就有的接缝：`RcloneLifecycleHost` 是 sealed 且 `GetClientAsync` **会真起 rclone**，
  没有这个接缝就没法在 UiTests 里断言复制的载荷。
- 任务文案：`DirectionText`=复制、`CanContinueFromStaging`（只有 Download 为真）、`RestartText` 在 Paused 时
  对复制仍显示**重试**、取消文案「已取消，重新复制会从头开始」；`CloudTransferWindow` 的 hint 在 `HasCopyTasks` 时
  追加「云盘之间的复制…只能整个文件重新复制」—— 这是 §5 B0 那条「UI 必须明说」的落点。
- 测试：`tests/FengSync.Tests/ServerSideCopyTests.cs`（真 rclone，两个 alias remote）4 条 —— 目标名精确落位
  （含 `ensureDestinationAsync` 先建父目录、顺序可证）、复制不搬走源、**重试覆盖已存在目标**、失败/源不存在时
  目标目录为空、本地端点两端各拒一次；`tests/FengSync.UiTests/CloudTransferCopyTests.cs`（假 RC）8 条 ——
  `srcFs/srcRemote/dstFs/dstRemote` 与 `_group` 精确断言、`core/stats` 用的是同一个 group、不发
  `movefile`/`deletefile`/任何含 `fengsync.partial` 的载荷、失败时 `Failed`+`ErrorDetail` 带后端原文且不 materialise
  目标、取消时 `job/stop` 真的发出去、journal 往返带回两个端点、旧 journal 仍可读、行文案纯函数；
  `CloudTransferWindowBehaviorTests` +1 STA（有复制才提复制）。

### B1 —— 文件浏览器的复制 / 剪切 / 粘贴（**已提交**：`3c9322b` + `cd74a61` + `526a634` + `4656a58` docs）

- 纯规则在 `src/FengSync/Services/CloudBrowserClipboard.cs`：`CloudClipboardMode`（Copy / Cut —— **两者的全部差别就是粘贴后删不删源**）、
  `CloudClipboardItem(Source, Entry)`（**行必须带着它来自的端点**：用户可以跨到别的端点再粘贴，脱离了 remote 的 listing 路径没有意义）、
  `CloudPasteTarget`、`CloudPastePlan.Targets` / `.Skipped` / `.Conflicts`、
  `CloudPastePlanner.Build(items, destination, destinationDirectory, alreadyThere)`。目标目录的**现有清单是参数**而不是在这里现取 ——
  冲突必须在**排队之前**发现，同时让规则保持纯函数。
- 三条规则的落点：① **文件夹永不粘贴**（队列的单位是一个对象，浏览器的答案一直是「先进入文件夹再选文件」），而且把
  「N 个文件夹没有被复制」**合并成一行**而不是每行重复一次；② **同端点同路径 = 自复制**直接 skip —— rclone 会报成功，
  用户会盯着一趟什么也不会改变的传输；③ 同名判定用 `OrdinalIgnoreCase`，且**目录标记也算占名**
  （没有真目录的 backend 上，文件夹标记占掉的正是粘贴要写的那个 key）。
- **两侧各自剥自己的根，只剥一次**：`CloudFileManagerService.RelativeToEndpoint(account, remotePath)`（由窗口里的私有
  `ToEndpointRelative` 提升为公共服务，窗口 6 处调用点一并改）。`CloudPasteTarget` 因此同时带 `DestinationPath`
  （相对 remote 根，`CloudFileManagerService` 用它）和 `SourceRelativePath` / `DestinationRelativePath`
  （相对端点根，队列与 `EndpointSchemes.BuildUri` 用它）。alias 型 S3 端点的 `RootPath` 是空的，所以没得剥 ——
  这条由 `A_paste_strips_each_endpoints_own_root_and_only_once` 钉住。
- 窗口侧（`RemoteEndpointManagerWindow`）：右键菜单加 **复制 / 剪切**（作用于整个多选），工具条加 `PasteButton`
  （`AutomationId="CloudPaste"`，放在 删除 与 下载 之间）。内部接缝 `SetClipboard` / `PlanPasteAsync` /
  `ExecutePasteAsync` / `ClipboardItems` / `ClipboardMode` / `ShowListing`，构造函数新增
  `internal RemoteEndpointManagerWindow(CloudFileManagerService?, CloudTransferQueue?)` —— 两个 shell 仍走无参形式。
  ⚠️ `PlanPasteAsync` 与 `ExecutePasteAsync` 的形状在 **B2** 改了：前者返回 `CloudPasteOffer(Plan, TakenNames)`，
  后者的 `overwrite: bool` 换成 `placements`（见 §2 的 B2）。
- 执行分工：**复制一律进队列**（`EnqueueCopy`，因此有进度 / 取消 / 重试 / 历史，同端点内的复制也一样）；
  **同端点的剪切走 `operations/movefile`**（`CloudFileManagerService.MoveAsync`，字节不动，和 重命名 是同一个调用），
  所以它既不留重复也不需要二次确认。
- 删除只在**复制真的完成之后**：`DeleteSourceWhenCopied` 等 `_transfers.WaitForAsync(task)`；返回 false（失败/取消）就只报
  「源文件仍然保留」，删除本身抛错就报「已复制但源未删除」而**不报成功**（用户此刻有两个文件，报成功会把重复藏起来）。
  粘贴前的确认用 `CloudDeleteSafety.Describe` + `SupportsTrash`（**能不能撤销问的是源端点**，不是目标端点），
  批量阈值 10 的第二次确认照旧保留。
- ⚠️ **本批抓出的两条生产 bug**（都是测试先红、再改代码）：
  1. **纯复制也会排队删源** —— 第一版无条件调 `DeleteSourceWhenCopied`，把用户以为可加的粘贴变成数据丢失。
     现在 `if (cutting)` 才安排删除，测试用「复制之后 `Assert.Empty(deleted)`、不发 `movefile`/`deletefile`」钉住。
  2. **复制结果被刷新吃掉** —— `BrowseAsync` 会自己占用状态行（「正在读取目录…」）并且**不还原**，
     所以先写结果再刷新时，界面最后停在一个早已结束却没转完的 spinner。现在刷新在前、写状态在后，同一次 dispatcher 操作里完成。
- 测试：`tests/FengSync.UiTests/CloudBrowserPasteTests.cs`（B1 落 14 条，B2 之后该文件 19 条，**真窗口 + 假 RC**）—— 空剪贴板（按钮 off + 「剪贴板是空的」）、
  复制记住来源端点、剪切的预告（「已剪切 2 项」「文件夹不会被粘贴」「无法恢复」）、文件夹永不粘贴、跨端点两侧路径各自成立、
  自复制 skip、冲突计数、两行 = 两条 `operations/copyfile` 且 `srcFs/dstFs` 是**两个不同**端点、冲突行拿到 Skip 时只排非冲突行（B1 时这是 `overwrite: bool`，B2 换成 `placements`）、
  同端点剪切只发一条 `movefile` 并清空剪贴板、跨端点剪切**复制完成后**才删源并写成「已剪切到 files」、拒绝复制时 `Failed` +
  「源文件仍然保留」且删除记录为空、**拒绝删除时报告「已复制但源未删除」并带上原因**（而不是报成功）、
  离开来源端点后粘贴仍落在目标端点的目录里。
  `CloudFeatureBehaviorTests` +3 STA 纯规则（大小写/斜杠不同写法仍算同一个文件、skip 原因合并成一行、两侧各自剥根一次）；
  `ServerSideCopyTests` +2 真 rclone 实证（结论见 §4）；`CloudPaste` 的 `AutomationId` 在窗口测试里直接断言。
- ⚠️ 断言状态行时**别指望后端原文**：窗口那句经过 `RcloneUiError.Describe(ex, "cloud-delete")`，403 会被映射成
  「云端拒绝了此操作。检查目标目录权限和远程端点授权…」。后端原文只在队列层可断言
  （`CloudTransferTask.ErrorDetail`，`CloudTransferCopyTests` 就是这么钉的）。
- **桌面场景没动**：`Invoke-UiScenario.ps1` 里没有枚举云盘工具条的断言，所以新增工具条按钮不需要改场景脚本。
  日后若要断言「工具条上有哪个按钮」，改动点是 `UiAcceptanceTests.cs` + `Invoke-UiScenario.ps1` 两处（见 §7）。

---

### B2 —— 上传与粘贴的同名冲突：`KeepBoth` 与逐文件询问（**已提交**：`dedc2b5` feat / `1649dc6` test / `0349467` docs）

- 规则全在 `src/FengSync/Services/CloudConflictResolution.cs`：`CloudConflictChoice {Overwrite, KeepBoth, Skip}`、
  `CloudConflictAnswer(Choice, ApplyToAll)`、`CloudConflictPlacement(Original, Destination, Choice)`、
  `CloudConflictResolver.ChoiceFor / AskFor / Apply`。**上传和粘贴从此共用同一套答案** —— 以前上传只有一次性
  YesNoCancel 弹窗（「全部覆盖 / 全部跳过 / 取消」），粘贴连问都没有。
- 设置项 `CloudUploadConflictPolicy` **追加** `KeepBoth`（append-only 铁律：`Ask=0 / Overwrite=1 / Skip=2 / KeepBoth=3`，
  设置 JSON 用默认 `JsonSerializer`，枚举按数字落盘）。`SettingsWindow` 下拉加第 4 项「保留两者（重命名）」，
  `ConflictIndex` / `ConflictPolicy` 两边同步；越界仍回落 `Ask`（`An_unrecognised_conflict_selection_falls_back_to_asking` 没动）。
- **`Ask` 必须映射成 `null`**：一个写着「每次询问」的设置不能被实现成默认答案，`ChoiceFor` 返回 null 就是「去问人」。
- **`Apply` 按输入顺序每条目的地给一条 placement**（skip 也占一条并写明是 skip）。理由不是美学：上传的行是**按位置**
  和答案配对的，而两个被拖进来的文件夹可以有**同名文件**，这一对指向**同一个远端路径** —— 按名字查表会把两条都指向
  同一个改名结果，第二份覆盖第一份，正是 `KeepBoth` 要避免的那种丢失。
  `Two_files_dropped_onto_one_remote_name_are_renamed_apart_rather_than_onto_each_other` 钉住它。
- **改名只相对自己落进的目录判断空闲**：`Apply` 收 `Func<string, ICollection<string>> takenOf`（目录 → 名单），
  每产生一个改名就把名字**加回**该集合。`CloudFileNames.KeepBothName`：`x.pdf → x (1).pdf`、
  `archive.tar.gz → archive.tar (1).gz`（**扩展名不动**）、大小写不敏感比对、**截到 255 字符**让 backend 收得下这个名字、
  最多试 9999 次后交出最后一个候选（不再无限数下去）。
- 弹窗 `src/FengSync/Views/CloudConflictPrompt.cs`（代码搭的，和 `TextPrompt` 一路，样式经 `TextPrompt.Resource<T>` 取）：
  四个选择 **取消 / 跳过 / 保留两者（存为“…”）/ 覆盖**，其中 **跳过是默认按钮**（回车不得替换文件），
  「把这次选择应用到其余 N 个同名文件」只在 `remaining > 1` 时可选。提问是**整批一次** `AskFor` 而不是每目录一次 ——
  否则拖一棵跨目录的树会每个目录再问一遍，而「应用到全部」的承诺覆盖的是整个 drop。
- 窗口接缝：`PlanPasteAsync()` 现在返回 `CloudPasteOffer(Plan, TakenNames)`（**同一次目录读取**既用来判冲突、又用来给
  改名当名单，不再 `ListAsync` 第二次）；`ExecutePasteAsync(plan, placements)` 取代 `overwrite: bool`；`AskConflict`
  是逐文件提问的接缝，未安装时才走真弹窗。粘贴用 `Indexed(placements)` 按目的地查表（粘贴的目的地唯一），
  上传用位置配对（见上）。
- ⚠️ **本批抓出的两条生产/测试坑**：
  1. **接缝的 `null` = 用户取消整批**，不能被 `??` 读成「没装接缝」：那样会打开真弹窗，而 `async void Paste_Click`
     里的「Cannot set Owner property to a Window that has not been shown previously」把**测试宿主进程直接打崩**
     （是 abort 不是 fail，整条 UI 套件停在半路，日志只剩 6 条读数）。
  2. **改名必须同时改写队列用的端点相对路径**：只改完整路径的话 `dstRemote` 会回到用户想避开的那个名字。
     现在改名落在 `CloudFileManagerService.Parent(relative)` 之下，`A_keep_both_rename_lands_in_the_directory_on_screen…`
     断言的是 `files:` 的 `dstRemote = shared/incoming/sub/a (1).txt`（队列把端点根再拼回去，所以断言里带 `shared/`）。
- 状态行：粘贴 `{N} 个同名文件改名为保留两者`；上传 `已加入传输队列：N 个文件。其中 M 个同名文件改名为保留两者。`。
  取消上传报「已取消上传。」、取消粘贴报「已取消粘贴。」，全跳过分别报「已跳过全部同名文件。」/「已跳过 N 个同名项目。」。
- 测试：`CloudFeatureBehaviorTests` 纯规则 **12 条**（`ChoiceFor` 三条 policy + `Ask` 留空；`AskFor` 的应用到全部、
  逐条问与剩余计数、取消≠全跳过；`Apply` 的 skip 占位、只换末段、同名两条分开改名、目录各自空闲；
  `KeepBothName` 的编号、扩展名、大小写、255 截断、9999 收敛）+ 设置项往返加 `KeepBoth → 3`；
  `CloudBrowserPasteTests` 真窗口 **5 条**（`KeepBoth` 落在改名后的远端名、改名仍停在屏上目录里、问题看到被占的名字与
  未决条数、应用到全部只问一次且两条各自改名、取消则一条 `copyfile` 都不发且剪贴板还在）。
  ⚠️ 这几条要走 **`PasteButton.RaiseEvent`**（只有真按钮路径才经过 `PlaceDestinations`），需要两个冲突名时给假 RC 传
  `ListedNames`（默认只有 `a.txt`）。
- 旧的 `CloudDeleteSafety.OverwriteFor`（`bool?` 三态）**删掉**，连同它的测试：那是「一次问完全批」时代的形状，
  留着就有第二个答案来源。

---

### B3 —— 属性 / 详情（**已提交**：`844b93b` feat / `7370af0` test / `ef211a3` docs）

- **改读对象，不再读屏上那行**。列表是一次快照：它可能还写着 backend 已经删掉的文件，也永远不带 MIME 类型。
  新腿 `RcloneRcClient.StatAsync(fs, remote, hashTypes)` → `RcloneStatEntry` → `CloudFileManagerService.StatAsync`
  → `CloudItemStat`，`operations/stat` 一次调用只问一个对象，**不列目录**（`PlanPasteAsync` 那种「读一次目录两用」
  的形状在这里不适用）。
- 规则全在 `src/FengSync/Services/CloudItemDetails.cs`：`CloudDetailRow(Label, Value)` /
  `CloudDetailsPanel(Rows, CanComputeHash)` / `CloudItemStat(Exists, IsDirectory, Size, ModifiedUtc, MimeType, Hashes)`
  （`Missing` 就是「不在了」那个值）/ `For` / `ForSelection` / `OffersHash` / `HumanSize` /
  `HashPreference = md5 → sha1 → sha256`（**与同步引擎 `ReadPreferredHash` 同一条链**，浏览器和引擎对
  「这个文件的校验值是什么」不能有两个答案）。
- **面板不许做两件事：声称一个它没数过的大小，显示一个它没读过的校验值。** 文件夹的大小写 `未统计`（递归统计是 B9），
  打开属性时 `hashTypes` 传空 → 校验值行是 `未计算`；**只有 `CloudDetailsPanel.CanComputeHash` 决定按钮出不出现**，
  不去嗅探哪一行写着「未计算」——措辞和按钮必须归同一个文件管。
- 代价是实测的：256 MiB 的本地对象，不问 0.15 s、问 md5 0.49 s、问 md5+sha1+sha256 0.86 s，
  **每个被点名的类型大约再走一遍对象**。所以这次读取放在弹窗自己的「计算校验值」按钮后面，并在按钮旁写明
  「云端对象会因此走完一遍下载」。
- 弹窗 `src/FengSync/Views/CloudDetailsPrompt.cs`（代码搭，和 `TextPrompt` / `CloudConflictPrompt` 一路；
  `TextPrompt.NewWindow` 因此从 private 变 internal，两个小弹窗共用同一族窗口参数）。两列 Grid 装进
  `ScrollViewer`（`MaxHeight 420`，AutomationId `CloudDetails`），行标签/值各自带 `CloudDetailsLabel{i}` /
  `CloudDetailsValue{i}`；**关闭同时是 `IsDefault` 和 `IsCancel`** —— 回车必须落在那个什么都不改的按钮上，
  绝不能落进一次全对象扫描。
- 窗口接缝：`DetailsAsync(entry, withHash)`（读失败也返回行：列表里那些仍然成立的事实 + 一行 `读取详情` 写明谁拒的），
  `BuildRowMenu()` 从 `FileList_ContextMenuOpening` 里**抽出来**（菜单内容因此能在不开窗口的前提下断言），
  多选的 属性 走 `ForSelection`。`属性` 在菜单**最后一项、分隔线之后**（照 Explorer：只读答案不能混在会改云端的动作里），
  单行是 `属性`、多行是 `查看所选 N 项的属性`。
- ⚠️ **本批抓出的两条坑**：
  1. **dangling else 把校验值行整个吃掉了**：`if (read?.Hashes.Count > 0) foreach (…) if (TryGetValue) rows.Add(带类型的行); else if (文件夹) …`
     —— 这条 else 链挂的是**内层** `if`，于是「没有校验值」时**一行都不产生**（4 条测试报 `Sequence contains no matching element`），
     「有校验值」时反倒多出一条裸 `校验值`。修法是给外层 `if` 加大括号。**这是本次实现里唯一被测试抓出来的生产缺陷**，
     纯规则那 12 条先写好才抓得到。
  2. **假 RC 拼错 JSON 会被窗口读成「后端拒绝」**：手拼 `"Hashes"` 时多包了一层花括号，`JsonDocument` 的解析异常走进
     `DetailsAsync` 的 `catch`，面板显示出 `读取详情` 行 —— 一次假双崩溃伪装成了一次真实的后端行为，而且**看着像通过了**。
     现在假 RC 把拼好的 item 再过一遍 `JsonDocument.Parse` 才发出，坏 JSON 在**写它的那条测试**里当场炸。
- 文案（`CloudFeatureBehaviorTests` 钉死）：大小 `4.0 KB（4,096 字节）`、`未统计`、校验值三态
  `未计算 / 此后端未提供校验值 / 文件夹没有校验值`、`云端已没有这个文件，请刷新目录。`；
  多选是 `选择 N 项 / 文件 n 个 / 大小（没有文件时写「没有文件」）/ 文件夹 m 个（大小未统计）/ 最近修改 / 所在端点`，
  **只有文件贡献字节**。位置行给 `路径 = nas:team/a.txt`（rclone 真会收的地址）、`所在端点`、以及
  `端点根目录 = /team`（S3 的 alias 强制前缀再加「（由 rclone 强制，无法越出）」）。
- 顺带修掉列的读数：`CloudFileEntry.SizeDisplay` 以前把 500 KB 显示成 `0.0 MB`，现在和面板走同一个 `HumanSize`
  （`The_size_units_are_the_same_words_the_list_column_uses` 同时钉两处，Theory 覆盖 0 / 512 / 512_000 / 2_097_152 / 3_221_225_472）。
- **同步引擎的 `RcloneEndpoint.StatAsync`（父目录列表 + `ReadPreferredHash`）没动**：属性这条腿是浏览器自己的，
  把引擎的比较路径改成单对象 stat 属于另一件事。
- 测试：**12 条纯规则**（`CloudFeatureBehaviorTests` 的 `---- details (属性)` 区）+ **12 条真窗口**
  （`CloudBrowserDetailsTests`：假 RC 记下每次 `operations/stat` 的 `remote` 与 `hashTypes`，旋钮
  `Gone / RefusesStat / HoldsNoChecksums / Directory / MimeType / Size`，**默认只有被点名才给 md5**——一个不问就答校验值的
  假双会让「打开属性不花钱」这条测试白白通过）+ **7 条真 rclone**（`RcloneStatFactTests`，alias remote + `RcloneDaemon`，
  钉住 `{"item":null}`、MimeType 恒在、`hashTypes` 是名字数组、`bogus` 被当场拒、目录被问也不给校验值）。
- 面板在进程内**不打开**（`ShowDialog` 没人能答；owner 从未 `Show()` 过会抛，见 §4 的 WPF 表），所以两条接缝都得是 internal。
  确认过没有桌面场景断言行菜单，因此新增的 属性 项不会挂 `Invoke-UiScenario.ps1`。

### B4 —— 分享链接（**已提交**：`3d1d532` feat / `1b4b7ed` test / `b5c0c91` docs）

- **这是第一个把访问权交给「别人」的动作**：链接一旦生成，任何拿到它的人都能读那个文件，不需要登录用户的账号；
  而它能活多久、能不能撤回，**都是 backend 的决定，本应用一句都不能承诺**。所以形状必须是「先用用户的话说清楚，
  答是才动手」：确认走 `ConfirmShare` 接缝（默认真 `TextPrompt.Confirm(..., danger: true)`，Enter 落「取消」），
  而那句话本身归 `CloudShareLinks.ConfirmationFor` 定 —— 弹窗不许自己措辞。
- **只挂在单文件行菜单上**（`复制分享链接…`，在 下载…/打开 之后、分隔线与 重命名… 之前）：**文件夹没有、多选没有**。
  这不是保守：实测对 S3 **前缀**调 `operations/publiclink` 答 404 `object not found`（一个前缀不是一个可分享对象），
  所以文件夹在**本地**就答 `NotAFile`，不去把 backend 的错误话冒充成产品语义；而「假设它能分享」会一次交出整棵子树。
- 路：`RcloneRcClient.PublicLinkAsync(fs, remote)`（`src/FengSync.Core/Endpoints.cs`，只认应答里的 `url` 字符串，
  缺了返回 `null`）→ `CloudFileManagerService.PublicLinkAsync` → 窗口 `ShareLinkAsync(entry)`（分支与后果）
  → `ShareAsync` 把结果写进 `StatusText`。纯规则在 `src/FengSync/Services/CloudShareLink.cs`。
- `CloudShareLinkOutcome` **七态各有自己一句不重叠的话**（`Copied / CopiedNotStored / Declined / Unsupported /
  Missing / NotAFile / Failed`，`Every_outcome_gets_a_sentence_of_its_own` 钉的是「七句互不相同」）。
  **`CopiedNotStored` 必须存在**：链接已经在云端生成了，只是剪贴板没收下（`Clipboard.SetText` 会被别的进程占住），
  这句话让用户自己复制；说成「没有取到分享链接」是**谎报** —— 用户会去重问一次云端已经答过的请求，
  而把一条已经存在的链接当成没有，比把一条不存在的链接当成有，更容易让人以为什么都没泄漏。
- ⚠️ **两条 rclone 的静默坑**：
  1. **载荷里多写的字段被静默忽略**。`expire` / `unlink` 这类字段发出去不会报错，但 S3 **忽略 `unlink`**，
     `expire` 超上限时被 clamp（rclone 自己的 NOTICE：`Public Link: Reducing expiry to 1w …`）。
     所以这一次发的**就是 `fs` + `remote` 两个字段**，一个都不多 —— 「参数传了」和「参数起了作用」是两件事，
     后者要 per-backend 实证，而本应用没有那份矩阵。
  2. **「不支持公开链接」那句 500 会被共用的 `RcloneFailureClassifier` 读成 `Temporary`**（`httpStatus >= 500` 那一档）。
     于是 `CloudShareLinks.FromFailure` 必须**先读文字**（`IsUnsupported`）**再看 category**，否则一个本地/alias 端点
     会被报成「连接暂时中断，检查网络后重试」，用户重试三次然后怀疑自己的网络。这条坑有专门的测试，它同时断言
     `Category == Temporary` **和** `FromFailure → Unsupported` —— 别把它「顺手改成按 category 分发」。
- 剪贴板走 `CopyLink` 接缝：这是仓库里**第一次**碰系统剪贴板，而 UI 套件是**桌面无关**的，测试不许把用户的复制缓冲区换掉。
  所以 `CloudBrowserShareTests` 断言的是接缝**收到了什么字符串**（`Assert.Equal([Link], copied)`），不是剪贴板本身。
- **实证手法（`RclonePublicLinkFactTests` 4 条，全离线、不碰任何真云、不用任何凭据）**：用 bundled 二进制
  **自己起一个 loopback S3** —— `rclone serve s3 <dir> --addr 127.0.0.1:<port> --auth-key AK,SK`
  （**不是** `--access-key-id`；bucket 就是子目录），再起一个 `rclone rcd --rc-serve` 并写一个 `s3` 段指回它
  （`env_auth=false` / `endpoint` / `force_path_style` / `region us-east-1`），于是 `{"url": …}` 那个线形是
  **rclone 亲手答的**，不是测试自己编的（断言它是绝对 http Uri、路径含 `bucket/note.txt`）。四条钉的是：
  local/alias 的 500 措辞含 `support public links`、s3 的 `{"url"}`、目录 404、已消失对象 404（`Category == NotFound`）。
  而 `expire` 被 clamp、`unlink` 被 S3 忽略、`fsinfo {fs}` 的 `Features.PublicLink`（local `false` / s3 `true`）
  是**同一套 loopback 上手工 curl 探到的**，**没进测试**：它们约束的是**措辞**（不许承诺有效期/撤回），不是代码路径，
  钉一条断言反而会把「我们不发这些字段」误写成义务。`fsinfo` **故意没用**：为一个开关多花一次往返，
  而「backend 拒了」这条话术本来就得能处理。
- 测试：**13 条纯规则**（9 `[Fact]` + 1 四条用例的 `[Theory]`，`CloudFeatureBehaviorTests` 的
  `---- share link (分享链接)` 区）+ **10 条真窗口**（`CloudBrowserShareTests`：菜单给的与不给的条件、
  载荷 `Fs == "nas:"` 与 `Remote == "team/a.txt"`、**用户没答应之前一次 RC 都不发**（`Assert.Empty(standIn.Shares)`）、
  逐态措辞、剪贴板抛异常时的 `CopiedNotStored`、状态行同时说「已复制到剪贴板」和「无需登录」）+ **4 条真 rclone**。
- 确认过的现状：行菜单**没有**桌面场景断言（上面 B3 的最后一条已经查过一遍），所以多出来的 `复制分享链接…` 不会挂 `Invoke-UiScenario.ps1`。

### B5 —— 回收站 / 软删除（**已提交**：`f61c1a1` feat / `0bbea38` + `a11b3f5` + `e66d42c` test / `7d013a6` docs）

- **形状是被 rclone 逼出来的，不是选出来的**：RC **没有** `operations/trash`（404 `couldn't find method`，
  `CloudRecycleBinFactTests` 第一条钉住）。所以「软删除」在这个应用里只能是**一个应用自己的文件夹**
  `.fengsync-trash`，而且必须落在**端点根**下（`CloudRecycleBin.RootOf`）—— 不是 rclone remote 的根：一个用 alias
  强制前缀的 S3 端点，其 `RootPath` 是空串，把镜像写到 remote 根就等于叫它写到自己被允许触碰的范围之外
  （`Assert.Equal(".fengsync-trash", CloudRecycleBin.RootOf(""))` 就是这条的现成断言）。
- **两条腿不能合成一条**：文件用 `operations/movefile`（同步、会自己建父目录），文件夹用 `sync/move`（异步 job，
  落到已存在路径是**合并**，并把源目录留成一个**空壳**）。`CloudFileManagerService.MoveIntoAsync(path, path,
  isDirectory)` 是这唯一的分岔处；壳的清理走 `RemoveIfEmptyAsync`，它**先 `operations/stat`** 再 `rmdir` —— 因为
  rmdir 打不存在的路径答 **HTTP 500**，而共用分类器把 500 读成 `Temporary`，于是「已经没了」会被报成
  「rclone 与云端的连接暂时中断」（§4 的表把这一对钉在一起）。
- **落点空闲名必须本地算**：movefile 对已存在的目标名是**覆盖**。所以 `PlacementsFor` 先 `NamesAsync` 拿目标目录的
  **文件与目录**名单，被占才用 `CloudFileNames.KeepBothName`（`a.txt → a (1).txt`），并且**每个目录单独记账**，
  一批里两个同名文件不会撞第二次。还原同理：名字被占就让位，而不是覆盖用户后来放上去的那个。
- 规则全在 `src/FengSync/Services/CloudRecycleBin.cs`：`CloudRecycleOutcome` **七态**（`Moved / Restored / Declined /
  InsideTrash / IsTrashFolder / NotInTrash / Failed`），`Explain` 一态一句。其中 `IsTrashFolder` 与 `NotInTrash` 是
  两条**不同**的拒绝：前者是「回收站文件夹本身不能移进回收站」，后者是「这个文件没有被本应用移到回收站，
  还原不了」—— 合成一句就等于让用户以为还原坏了。
- **只有一套删除门槛**（§5 那条接线的落实）：`清空回收站` 不新造确认框，它把选中的行交给已有的
  `ConfirmAndDeleteAsync(entries, insideRecycleBin: true)`；`CloudDeleteSafety.Describe` 因此只多一个措辞开关，
  多出来的那句是「这些项目已经在 `.fengsync-trash` 里，删掉以后本应用的「还原」也帮不上了。」，
  阈值 10 与它的二次确认**一字未改**。`ConfirmDelete` 接缝让这两问可断言：3 项=**一个**问题，11 项=**两个**。
- 「回收站」按钮是**先 mkdir 再进去**，因为 `operations/mkdir` 幂等（重复调成功）、会建父目录，且当同名的是一个
  **文件**时**报错而不是替换** —— 于是这个顺序不需要额外的存在性检查，也不会出现「点进去还是原目录」的假象。
- 测试：**13 条纯规则**（`CloudFeatureBehaviorTests` 的 `---- recycle bin (回收站)` 区，含 `RootOf("")` 与
  `IsTrashView` 的三种位置）+ **17 条真窗口**（`CloudBrowserRecycleTests`，stateful 假 RC）+ **10 条真 rclone**
  （`CloudRecycleBinFactTests`）。
- ⚠️ **本批顺手查实的一个既有缺陷（不属于回收站，单独提交）**：`CloudBrowserShareTests` 的十条窗口测试全是
  `public void` 而**没有 `await`** 那个返回 `Task` 的 `WithWindow`，于是断言跑在没人观察的后台任务上 ——
  **十条从未真正生效**。改成 `async Task` + `await` 之后立刻暴露第一条 `NullReferenceException`：它没先 `Select`
  一行就取行菜单，而 `BuildRowMenu()` 在空选择下**按设计**返回 `null`（`CloudBrowserDetailsTests` 早就钉着这条），
  所以修的是测试、不是产品。同一形状另有两处后果值得记住：不 await 时这些线程彼此重叠，两个同时建 WPF
  `Application` 会让整轮 **Test Run Aborted**（单独跑那个类时立刻重现，全量里靠时序侥幸）。
  `UiTestAwaitGuardTests`（Core 阶段，扫 `tests/FengSync.UiTests/*.cs`）现在把这条变成会失败的检查，
  规则同步写进 `AGENTS.md`；`CloudBrowserPasteTests` / `CloudBrowserDetailsTests` / 本批的
  `CloudBrowserRecycleTests` 照旧是 `async Task` + `await`，所以它们一直是活的。
- 另记一条**没顺手改**的缺口：**重命名**撞到一个已存在的名字仍是覆盖/合并（`RenameEntryAsync` 没有冲突检查）。
  B5 没动它，`RenameAsync` 的文档注释里如实写着，留给后续批次按 §5 的形状补（它同样需要「先问列表再决定」）。
- 门禁：`Failed: 0`，**Core 415 / UI 383**（UI 3m8s），数字的来龙去脉与两处 flake 的处置都在 §1①。

### B6 —— 键盘快捷键（**已提交**：`f45e06b` feat / `2299460` test / `1893946` docs）

- **键盘只做路由，不做规则**：`CloudBrowserShortcuts.Resolve(key, ctrl, shift, typing)` 是一张**纯表**，一个按键
  只映射到一个命令（F5→刷新、Del→移到回收站、Shift+Del / Ctrl+Del→删除、F2→重命名、Ctrl+A/X/C/V→全选/剪切/
  复制/粘贴），表认不出的键一律返回 `null`。命令的执行体（`RemoteEndpointManagerWindow.HandleKeyAsync`）里
  **一句规则都没有**：每个命令直接调按钮本来就在调的那个方法（`BrowseAsync` / `RecycleSelectedAsync` /
  `DeleteSelectedAsync` / `RenameSelectedAsync` / `PasteAsync` / `SetClipboard`）。这条是这一批的核心决定，
  因为快捷键最容易长出的东西就是**第二套更便宜的拒绝语和确认框**。
- 为此把四个 `*_Click` 处理器各自抽成一个 internal 方法（`RenameSelectedAsync`、`DeleteSelectedAsync`、
  `RecycleSelectedAsync`、`PasteAsync`），处理器退化成一行的委托。**副带查实并修掉一处既有分岔**：
  `BuildRowMenu` 的「删除…」原本是直接 `ConfirmAndDeleteAsync(selected)`，绕过了 B5 那句「已经在回收站里」的
  措辞开关；现在它和按钮、Shift+Del、Ctrl+Del 同走 `DeleteSelectedAsync`，四条路**问的是同一个问题**
  （`The_row_menu_the_button_and_the_keyboard_ask_one_delete_question_in_a_trash_view` 钉住，探针验证过：
  把那行改回直调，这条立刻 fail）。
- **处理器挂在浏览器面板（`Grid Grid.Column="2"`）的冒泡 `KeyDown` 上**，不是挂在窗口上，也不是 Preview：
  端点列表与挂载列表各自留着它们自己的键。这一条不是讲道理，是被**真实路由事件**钉的 —— 在 `EndpointList` 上
  raise 一个 F2，窗口的命令一条都不许动（`A_keystroke_in_the_endpoint_list_reaches_no_browser_command_at_all`）。
  把 XAML 里那句 `KeyDown="FileBrowser_KeyDown"` 删掉，恰好三条 raise-event 测试 fail，这就是「真的挂上了」的证据。
- **typing 闸门的顺序是有原因的**：F5 在闸门**之前**判（在地址框里也要能刷新），闸门之后才轮到其余命令。
  实测到的 WPF 行为决定了闸门只对某些键真正起作用：`TextBox` **自己吃掉** Del 与 Ctrl+A（它们根本冒泡不上来），
  但**不吃** F2 —— 所以没有闸门，敲了一半的地址栏会被 F2 打开一个重命名问题。把 `IsTyping` 强行改成 `false`，
  `The_address_bar_keeps_its_own_editing_keystrokes_and_still_answers_the_refresh` 立刻 fail，闸门不是装饰。
  另外「表不认的键保持 unhandled」也是实测结论：WPF 的 `ListBox` 没有内建 Ctrl+A，裸字母留给 type-to-select，
  handler 若一概 `Handled = true` 就把列表的按字母选中悄悄弄坏了。
- **`PromptRename` 是重命名问题的接缝**（`Func<CloudFileEntry, string?>?`，`null` = 用户取消，与其余接缝同一契约），
  所以 F2 的两条测试（单选打开、多选不打开）**永远不会碰到真模态框**。
- 测试：**19 条**（`CloudBrowserShortcutTests`）—— 3 条纯表（一键一命令、Del 的三种拼法、文本框保留编辑键）+
  5 条真实冒泡 `KeyDown`（F5 重读目录、F2 只重命名选中那行、不认的键不认领、地址栏、端点列表）+
  11 条命令本身（Del 移两条且不删、空选择说的仍是本命令那句、Shift/Ctrl+Del 一问删三项、回收站视图里 Del 说
  「已经在回收站里」且什么都不问、F2 多选不开口、Ctrl+A 只选过滤后可见的 3/4 行、全选后 Ctrl+C 装 4 项、
  Ctrl+X 承诺删除、空剪贴板的 Ctrl+V 说清缺什么、复制的是文件夹时粘贴同时说「没有可粘贴的项目」和「没有被复制」
  而不向云端发 `copyto`）。
- ⚠️ **本批没有证明的两件事**（别把它们当成已验证）：① **物理修饰键** —— `Keyboard.Modifiers` 读的是真实键盘，
  合成事件改不了它，所以带 Ctrl/Shift 的用例是**把修饰键当事实传进** `HandleKeyAsync` 的，只有 F5 / F2 / Z 这类
  「意义不随修饰键变化」的键才走 raise-event；因此「XAML 那个处理器读修饰键读对了」这一环只有代码在担保。
  ② **桌面场景可达性** —— `Invoke-UiScenario.ps1` 发的是 posted 消息，同样伪造不出修饰键，所以**没有**加键盘的
  桌面步骤。附带一条**故意没做**的：按钮 tooltip 与行菜单里**没有**写快捷键文字（键盘可用但不易被发现），
  留给后续批次顺手补。重命名撞名仍是覆盖，那条 B5 记的缺口本批未动。
- 门禁：`Failed: 0`，**Core 415 / UI 402**（UI 3m1s）—— Core 计数与 B5 收尾相同，因为 B6 只动 UI 侧。


### B7 —— 网格视图 / 缩略图 / 内置预览（**已提交**：`1334311` feat(core) / `c59ac45` feat / `c66bb6c` test / `546e5b5` docs）

- **一个行集合，两种画法**（本批的核心形状）。`RemoteEndpointManagerWindow` 只有一份 `_entries`（`VisibleEntries`
  是它过滤+排序后的读数），键盘、剪贴板、回收站、删除阈值全读它。所以网格视图**只是** `FileList.View = null`
  加上一对 `ItemTemplate`/`ItemsPanel`：不建第二个 `ObservableCollection`、不加第二个控件、切换时**不发
  `operations/list`**。两条相反的做法都能"看起来能用"，但两份列表会让 `Ctrl+A` 与行菜单各说一套。
- **tile 的图片是"查"出来的，不是"读"出来的**：模板里的 `Image` 绑一个窗口作用域的 `CloudTileSourceConverter`，
  它的 `Peek` 委托在构造函数里接到 `CloudThumbnailStore`，**只读缓存、绝不在 converter 里发起加载**（converter
  里联网 = 每个可见行每次 layout 在 UI 线程上发一次请求，且无法取消）。真正拉字节的是 `RestartTileRound`：
  按**当前** `_entries` 里 `OffersThumbnail` 的行发起，换目录/换视图/换搜索词时 `_tileRound?.Cancel()` 掐掉上一轮
  （故意不 `Dispose` —— 在途的 read 还会往那个 source 注册续传，Dispose 会在 read 里抛）。tile 落地用
  Background 优先级**合并**的 `Items.Refresh()`，一轮结束补一次 Normal 优先级刷新，所以 `await window.PendingThumbnails`
  才真的等价于「画完了」而不是「请求发完了」。
- **预览是 13 个具名结果，不是一个 null**。`CloudBrowserPreview.DecideFor` 先看列表这一行（文件夹 / 能解码的图 /
  像图但没解码器 / 文本表内 / 有格式但本程序没渲染器 / 后缀都不认，再叠加 0 字节与超上限），**读之前**就能开口；
  `OutcomeOf` 只把云端的答案（`Read`/`TooLarge`/`Missing`/`Failed`）映射成状态；字节是不是真图、是不是能读的文本，
  由 `DecodePicture`/`ExtractText` 各自回答（`UndecodableBytes`/`Binary`/`NotUtf8`）。每个状态一句自己的话
  （`ReasonFor`），所以面板上不会出现「无法预览」这种什么都没说的事。窗口侧 `PreviewAsync` 是接缝，
  `CloudPreviewPrompt` 只是那层壳 —— 模态框在进程内没人能答。
- **上限是被解释过的数字，不是拍的**：tile 8 MiB / 解码到 64 px（装饰，用户没要求花带宽，可能还没落地就滚走了），
  预览 32 MiB 图 / 256 KiB 文本（一次**显式点击**才付的钱，且 256 KiB 已经是几十页滚动），预览解码到 1024 px
  （面板 1000 DIP 宽），并发 2（与传输队列同一口径），解码内存 512 张 ≈ 8 MB，缩略图缓存 64 MiB 后按最旧修剪。
  缩略图与预览**互不复用缓存**：一个是 64 px 的装饰、一个是 1024 px 的内容，用同一份键就会有一边在偷看另一边。
- **明确不做**（写进代码里的拒绝，不是漏掉）：PDF / office / 音视频没有渲染器（`net10.0-windows` 的 WPF 没有，
  `Windows.Data.Pdf` 是 WinRT），照旧走「下载后用外部程序打开」；非 UTF-8/UTF-16 的文本**直接拒绝并说明不猜代码页**
  （没有 `System.Text.Encoding.CodePages`，猜错的中文看着像文件损坏）；WebP/AVIF/HEIC/SVG/RAW 保留文件图标并说明
  是「本程序没有它的解码器」。双击不可预览的行**仍然**走原来的下载+外壳打开路径，行菜单里**不给**「预览」
  （只能拒绝的命令是噪音，不是快捷方式）。
- **三条实测到的 WPF 事实**（全部写进 AGENTS.md，见 §4 末尾三行）：① 解码失败有三种异常形状，其中
  `FileFormatException` 派生自 `SystemException`，`IOException` 过滤器接不到，两条解码路径必须共用
  `CloudBrowserTiles.RefusesDecode`；② 绑了 `GridView` 的 `ListView` **不写任何本地** `ItemTemplate`/`ItemsPanel`
  （实测 `unset`），所以列表视图必须 `ClearValue` 复原 —— `ItemsPanel = null` 是本地 null，会让**一行都不呈现**
  （可见单元格 2 → 0）而集合完整无缺；③ 网格 tile 内部的文字**不入 UIA 树**（`RawViewWalker` 下 grid 的
  `DataItem` 零子节点，`Name` 是整条 `CloudFileEntry` 记录的 `ToString()`），所以行必须自己带
  `AutomationProperties.Name`。
- ⚠️ **②是本批唯一一次真实门禁 fail 的根因，也是「进程内测不到」的样本**：原来的断言
  `Assert.Null(window.FileList.ItemTemplate)` 把 bug 状态读成正确（本地 null 也返回 null），只有真实外壳能看见 ——
  `sftp-ui` 场景在第一次浏览就找不到 `remote-proof.txt`。修完之后给场景补了两条 toggle 断言（切到网格、切回列表，
  各自要求行按名字找得到**且** `BoundingRectangle` 有尺寸），并**分别用负向跑验证过**：去掉
  `ItemContainerStyle` 那个 setter，网格那条 fail；把 `ItemsPanel = null` 放回去，第一次浏览就 fail。
- ⚠️ **本批没有证明的三件事**（别当成已验证）：① **`ItemsSource` 被换成副本**这一类破坏在进程内**测不出来**
  （探针查实：从未 `Show()` 的控件不靠视觉树维持选中，`SelectedItems` 原样留着，那条逐行 `ReferenceEquals` 测试
  照样绿），所以「只有一份集合」只有 diff 本身 + 场景的行可达性在担保；② **像素**：`TileFor` 只证明 converter
  交得出一张图，不证明它被画在 64×64 的方块里（真实外观靠 §2 记的那次 `-ShowOnScreen` 截图人工看过一次）；
  ③ **预览面板那层壳**（`CloudPreviewPrompt`）没有任何断言 —— 测的是 `PreviewAsync` 交回来的
  `CloudPreviewPanel`，弹窗本身只在代码里。附带一条**故意没做**的：按钮 tooltip 与行菜单里仍未写快捷键文字（B6 留的）。
- 测试：**40 条新增**（`CloudBrowserGridViewTests` 20 + `CloudBrowserPreviewTests` 20）+ 9 条 Core 侧真 rclone
  事实（`RcloneServeReadFactTests`，`54eb6f5`，把假 RC 的状态形状钉成 bundled v1.75.0 实测的那几种）+ 1 条
  XAML 静态守卫（`LiveTextAccessibilityTests.The_cloud_file_list_names_every_row_from_the_item_it_shows`：浏览器那一行
  必须**由容器自己带名字**，且必须是绑定而非字面量 —— 这是上面第 ③ 条事实的唯一廉价格栅，而**删掉那个
  `ItemContainerStyle` 立刻 fail** 已实跑验证）+ 桌面场景两条
  toggle 断言。守卫**逐条探针验证过会 fire**：`FileFormatException` 从共用谓词里删掉 → 缩略图与预览各 1 条立刻
  fail；`OutcomeOf` 不再认 `TooLarge` → 2 条 fail；去掉「不可预览不读字节」的前置拒绝 → 2 条 fail；行菜单去掉
  `OffersPreview` 闸门 → 1 条 fail；`ApplyView` 尾部不再 `RestartTileRound` → 换目录那条读目标 fail；
  `ItemsPanel = null` → 视图那条 `UnsetValue` 断言 fail。
- 门禁：`Failed: 0`，**Core 425 / UI 442**（UI 3m3s）—— Core 从 415 起跳是那 9 条 `RcloneServeReadFactTests`
  （`54eb6f5`，真 `--rc-serve` 有界读的事实，每条都打 bundled 二进制）+ 本批那条 XAML 静态栅格；
  UI 从 402 起跳是本批的 40 条。这一轮**没有**撞上 B5 那条 WinFsp detach flake。


### B8 —— 收藏 / 最近访问（**已提交**：`0686a4a` + `ad66e38` + `73aed4e` feat / `fad7f90` + `121acf1` + `a749142` test / `61d60a4` docs）

- **两条承诺，两套规则，一个类**（本批的核心形状）。「收藏」是用户挑的：顺序归他，重复添加**不挪位**，
  永不为容量修剪；「最近访问」是应用写的队列：新的在前、一处一份、`RecentLimit = 20`。
  `CloudBrowserBookmarks` 把两者写成不同方法（`AddFavorite` vs `RecordVisit`）而不是一个 `Add` 带开关 ——
  合成一张"书签表"就等于让"某人路过看一眼"可以悄悄改掉"用户精心排过的那一份"。
- **写盘的只许是地名**。`cloud-bookmarks.json` 的一行只有 `{Remote, Path}`：rclone 端点名 + 端点内的一个目录，
  **没有能放 token / 密码 / key 的字段**（照 A7 端点导出那条规矩）。两份列表**一次写完**（分开写会留下
  「收藏已修剪、最近访问还是旧的」这种半状态），落盘走 Guid 临时文件 + `File.Move(…, true)`。
  读写不对称是有意的：`LoadAsync` 把读不懂的文件挪成 `.corrupt-<时间戳>.bak` 再当空读（那是用户唯一一份选择，
  覆盖它比读空更糟），而 `RenameRemoteAsync` 内部那次读**不**备份 —— 它紧接着就要在同一份文件上改写，
  留一份"备份"只会让人以为还能回到重命名之前。
- **一次跳转只列一个目录**（本批的实测点）。跨端点跳转必须先**接管**那一行所属的端点，而不是"跟随选中"：
  跟随会先为目标端点列它自己的根，一次跳转因此打**两**次 `operations/list`。`SwitchEndpoint` 采纳端点而不浏览，
  于是 `Assert.Equal([$"{Files}:{FilesInbox}"], standIn.Listed)` 数死那**一**次；`NavigateToAsync` 也从 private
  提成 internal，因为测试驱动的就是这条真实路径。假 RC 把 `operations/list` 的 payload 按 `remote` 建树、
  却记 `fs + directory` —— 只记 `remote` 的话，跨端点跳转会被读成"同一处"，这条断言就什么也没证明。
- **只有列成功才算一次访问**：记录挂在 listing 之后，失败的那次既不写盘也不建文件；`IsNewestVisit` 那道闸门
  让"站在原地按刷新"不产生任何写入。这条是用**删掉文件**证明的（不是比时间戳）：刷新后文件仍不存在，才算闸门真的挡住。
- **端点不在屏上就隐藏，不删**（`VisibleFor` / `HiddenCount`）：`rclone.conf` 这一次读不到，不该清空用户挑过的收藏；
  ToolTip 说出藏了几份。跳转列表的行来自 `_accounts`（当前可见端点），和 `JumpToAsync` 能选中的集合**是同一批** ——
  原先一个读全量、一个读可见，会开出"点不动的行"。
- **标签与前缀的规则是实测出来的**：`LabelFor` 把端点名放前面（同一个目录名可以存在于五个端点，能区分它们的只有路径）；
  被 alias 强制前缀的端点，它的"根"存的是 `<prefix>` 而不是空串（`CloudBrowse.Normalize(path, root)` 会把越过根的路径
  夹回来），所以 `根目录` 只出现在真的没有前缀的端点上 —— 这是本批**第一条 fail 的根因**，且是**测试期望写错**、
  不是产品写错的样本。显示用端点表里那份大小写（重命名过大小写的地方要跟着表读），匹配用 `OrdinalIgnoreCase`
  （rclone 自己就这么找 section），路径用 `Ordinal`（后端可能大小写敏感，只差大小写的两个目录是两个地方）。
- **重命名搬第四份文件**。A2 原本只搬 `rclone.conf` / `cloud-endpoints.json` / `profiles.json`；收藏是一条指针，
  留在旧名上就指向一个 rclone 已不再有的 section，而用户看到的是"收藏空了"、原因看不见。
  `RenameRemoteAsync` 一次读-改-写，两份列表都没变就**一个字都不写**。改写排在 profiles **之前**是有理由的：
  把 `profiles.json` 读回来这一步可能被一份无人控制的输入抛（`ProfileStore.LoadAsync` 没有恢复路径），
  而现在四份 `backups.Capture(...)` 是一份也不会漏的。
- ⚠️ **把一条永真的回滚断言拆成两条会 fail 的**：原来"daemon 没确认 → 什么都没搬"只断言三份文件，第四份是顺带绿的。
  现在一条断言"验证成功之前谁都不写"（三份 + 第四份的 before 快照），另一条
  `A_rename_that_failed_after_the_bookmarks_moved_puts_all_four_files_back` 用一份写坏的 `profiles.json`
  逼出第四份的**恢复**路径。
- ⚠️ **磁盘搬完了，窗口内存里那份还没搬** —— 本批**唯一由真实外壳抓到**的产品缺口，也是「第三份表示」这条教训的实例。
  `CloudEndpointRename.RenameAsync` 把收藏文件里的端点名改对了，可 `RenameEndpoint_Click` 紧接着 `RefreshAsync()`；
  刷新会启动一次 listing（`ApplyEndpointView` 末尾那句 fire-and-forget `if (browse) _ = FollowSelectionAsync();`），
  而 listing 末尾记一次访问用的是**窗口内存里那两份旧列表** —— 刚迁移好的文件于是被写回旧端点名，
  "重命名带着收藏走"这条承诺在真 RC 上原地作废。修法一行：`await LoadBookmarksAsync();` 排在
  `await RefreshAsync();` **之前**（先重读，再给任何写入创造机会）。
  **为什么 32 条进程内书签测试 + 18 条重命名测试全绿却漏了它**：进程内那几条测的是 `RenameRemoteAsync` 这个
  **动作**做对了没有，场景测的是"重命名之后那个下拉里旧名字的行**一行都不剩**、且新那一行**点得动**"这条
  **状态**；状态要经过窗口自己那份内存，动作测试永远看不见。修复前 `sftp-ui` 在 `Invoke-UiScenario.ps1:339`
  报 `Missing UI element containing: ui_renamed_…`，修复后同一 harness 同一台机绿（不是 flake 归因，这条守卫**实测证过会 fire**）。
- ⚠️ **同一形状的缺口本批没堵**：书签列表是**每个窗口各自一份内存副本**。同一进程里第二个已经打开的管理窗口
  不知道这次重命名，它下一次 listing 仍会拿自己的旧列表覆写那份文件。单窗口场景证明不了这句话，也没有测试覆盖它，
  所以下面那条「没有证明」清单之外还要再算上这一条（它是第四条，不是那三条里挤出来的）。
  B9 若把写入改成"读-改-写"、或让任何写入前先重读，就把这条一并收掉；不改就留着这句，别当成已解决。
- ⚠️ **本批没有证明的三件事**（别当成已验证）：① 「按钮跟着目录走」在真实外壳里只测到点一下标签翻成 取消收藏，
  翻到别的目录后按钮自己重算只有进程内那条断言担保；② 满 20 条的最近访问在真实下拉里能否滚到底**没测**
  （进程内测的是 `RecordVisit` 的裁剪）；③ `BookmarkNotice` 那句状态栏话只断言了窗口属性上的文字，
  状态栏 `TextBlock` 的可见性没有场景步骤。桌面场景只挑**只有真实外壳能证明**的四件事：三个控件在工具栏换行后
  `BoundingRectangle` 仍在屏内（`Assert-RectangleInside`）、翻状态按钮**不带**静态 `AutomationProperties.Name`
  （所以"取消收藏"是作为**可访问名**被 `Find-Name` 找到的，见 §4 的 UIA 事实）、下拉能 `ExpandCollapse` 且行能
  `Select-Ui`（SelectionItemPattern）、重命名之后旧端点名的收藏**一行都不剩**且那一行点得动（真 RC + 真盘上的文件）。
- ⚠️ **本批查实的两处异步陷阱，都是「测试先撞上、产品也在悬崖边」**：
  ① **文件锁不能依赖调用方的上下文**。`CloudBrowserBookmarkStore` 的 gate 是**进程级**的，而它里面每一个 await
  原本都回到「谁调用我」的 `DispatcherSynchronizationContext`。于是一次 listing 记下的访问写入，如果它所在的线程
  在写入落地前停止 pumping（测试线程退出，或应用正在关闭），那个 `finally { Gate.Release(); }` 就永远不会跑 ——
  收藏文件从此**锁死整个进程**，后面每一个会写书签的窗口测试都卡在闸门上，各自被 harness 的看门狗砍在 90 秒。
  实测过程：`CloudBrowserShortcutTests` 单跑 19 条里 3 条红（两条 1m30s 超时 + 一条 43ms 断言 false），
  单跑其中任何一条**又全绿**；给 gate 加临时 trace 后看到 `save held thread=29` 之后**再没有** `save release`。
  修法是把 gate 区间里的每个 await 写成 `ConfigureAwait(false)`（`CloudThumbnailStore` 早就这么做的同一条规矩），
  并补了第 9 条文件级测试 `A_write_whose_thread_stopped_pumping_still_frees_the_file_for_the_next_reader`
  —— 它从一个「发出写入就停止 pumping 并退出」的 STA 线程之后去读，**并且探针验证过会 fire**：
  只把 `SaveAsync` 里那一处 `.ConfigureAwait(false)` 撤掉，这条 10 秒内 fail 并报「后来的读取再也进不去」。
  **同一形状但本批故意没动**：`CloudEndpointMetadataStore`、`CloudEndpointRename` 的静态 gate 与
  `Filtering` 的按路径写锁 —— 没有测试能证明它们卡住，就不改（见 AGENTS.md 那条）。
  ② **`FileBrowser_KeyDown` 是在 `await HandleKeyAsync(...)` 的续传里才 `e.Handled = true` 的**，所以
  「按键被认领」这句话**读不到同步值**。B6 那条 F2 测试原来在 `WaitUntil(FileMoves == 1)` 之后立刻
  `Assert.True(args.Handled)`，靠的是「整条命令刚好在等 move 的那几轮里也跑完了」—— B8 给 listing 末尾加了一次
  书签写入，多出一个续传轮次，这条就红了（**不是**产品坏了，是断言本来就踩在时序上）。已改成先
  `WaitUntil(() => args.Handled, "F2 走完命令后没有被认领")` 再断言，并在注释里写清 `Handled` 为什么只能事后读。
- 测试：**32 条新增**（`CloudBrowserBookmarkTests`：纯规则 10 + 文件 9 + 真窗口 13）+ `CloudEndpointRenameTests`
  2 条（1 条扩成四份文件、1 条新的回滚）+ 桌面场景两段；另外把 **5 个既有窗口测试的 harness** 补上
  `FENGSYNC_DATA_DIR` 隔离（B8 之后窗口会写盘，这五个文件此前只在同一份真实 app data 上跑过），并把 B6 那条
  F2 断言改成事后读（见上第 ② 条）。守卫**逐条探针验证过会 fire**：`IsNewestVisit` 闸门去掉 → 「刷新不重写作」fail；
  `SwitchEndpoint` 换成跟随 → 「一次列表」fail（第一次探针因为 `SwitchEndpoint` 仍在而未生效，注释掉之后重跑才确认）；删掉
  `backups.Capture(bookmarks.FilePath)` → 四文件回滚 fail；不调 `RenameRemoteAsync` → 重命名携带收藏 fail；
  `SaveAsync` 的 `.ConfigureAwait(false)` 撤掉 → 「没人接应的写入也要放行下一个读者」fail；
  重命名后不调 `LoadBookmarksAsync` → `sftp-ui` 场景在等 取消收藏 之前先找不到重命名后的那一行（本批修复前实测如此，
  这一条**只有真实外壳能证明**，见上面那个 ⚠️）。
  反过来还**撤回了一处自证无用的产品改动**：探针显示 `ShowListing` 里补 `UpdateFavoriteToggle()` 没有任何测试依赖，
  于是把那段删掉、并把那条测试重新命名、重新限定范围。
- 门禁：`Failed: 0`，**Core 425 / UI 476**（UI 3m7s）—— Core 与 B7 收尾一字不差（425），UI 从 B7 的 442 涨到 476，
  差的 34 条正是 B8 新增的 32 条书签 + 2 条重命名；`sftp-ui` 场景那两段算在既有条目里，不改计数。
  ⚠️ 这一条门禁**跑过一次红的**（476 里 1 条 fail = `sftp-ui`，即上面那个「窗口内存第三份表示」缺口），
  修完才重跑成现在这份；别把它当成「一次通过」。


---

## 3. A3/A4 的设计要点与遗留缺口

代码已提交，文件与测试清单见 §2 的 A3+A4。这里只留**日后改这块必须知道的三件事**和**一处尚未补的缺口**。

### ⚠️ 三个必须知道的设计要点（最容易踩）

1. **`_browsedEndpoint` 守卫的第一版是错的**（本批自己抓出来的）：重建 `_accounts` 会先抛一次
   **「无选中」**的 `SelectionChanged`，把 `_browsedEndpoint` 清成 `null`，于是**每次击键仍然会重新列一次远端目录**
   —— 一次网络往返。修法：重建期间用 `_rearranging` 静音事件，由 `ApplyEndpointView` 在重建**结束后**
   自己调 `FollowSelectionAsync()` 决定要不要浏览。事件处理器只负责**用户手势**。
2. **空结果必须显式 `ClearListing()`**：`_accounts.Clear()` 之后 `SelectedIndex = -1` 在**已经**是 -1 时**不触发事件**，
   所以「过滤命中 0 行」要自己清空文件面板并把 `_currentPath` 置空；否则会出现「列表空、面板还显示上一个端点的文件」。
3. **`CheckAllButton.IsEnabled = _accounts.Count > 0`**（跟随**可见行**，不是 `_loaded`），`CheckAll_Click` 取 `_accounts` **快照**
   （搜索框在跑的过程中是活的，过滤变了不能让进度读数和总结统计到另一批）。`DescribeCheckAll(0,0,0)` 有单独分支
   —— 「0 个端点全部连接正常」是一句真话，但意思正好相反。

### 遗留缺口

- ~~**A1 未覆盖的新 Provider 编辑表单**~~ —— **已补（2026-10-04，`fd12512`）**。`sftp-ui` 桌面场景现在把
  `CloudProviderDescriptor` 生成的 7 张表单全部走一遍：选服务 → 进连接步骤 → 断言该 descriptor 声明的每个
  AutomationId 都是**有尺寸的**控件、名称框与根目录框在窗口内可达，并断言**别家 Provider 的字段没出现在这张表单上**
  （编辑器按当前输入写 rclone 参数，残留字段会把另一个后端的键写进这个 remote）。
  S3 走自己的 bespoke 面板，放在最前面（它要先加载 provider 元数据）。新增 Provider 时**必须**在这张表里加一行，
  场景会自动要求它的字段可寻址 —— 这就是「加 Provider 只是加一行」那条约定的现成反例。

---

## 4. 已实证的事实（不要再猜）

### rclone（v1.75.0，bundled，tags=cmount）

| 事实 | 影响 |
| --- | --- |
| `config/create` 对 OAuth 后端**必跑**配置状态机（`State: "*oauth-confirm,teamdrive,oauth,"`），`opt.nonInteractive=true` 不跳过 | 克隆 OAuth 端点 = 要求重新授权；重命名只能改写 conf |
| 运行中的 `rclone rcd` **会重新读取** `rclone.conf`，且**不覆盖**手写进去的段 | 改 conf 不需要重启 daemon |
| `File.Move(tmp, path, true)`（换 inode）与原地写一样能被拾取 | 原子写 conf 是安全的 |
| config RC 命令集**没有 rename** | 见上 |
| curl 调 RC **必须带** `-H "Content-Type: application/json"` | 否则报 `Didn't find key "name" in input` |
| 挂载选项键名**大小写不敏感**，但**未知键被静默忽略**（挂载照常成功） | 拼错 = 静默失效，唯一防线是断言发出去的 JSON 键名 |
| `readOnly` 属于 **`vfsOpt`** 而非 `mountOpt` | D 组必读 |
| `_config:{CacheDir}` 被**静默忽略**；只有 `rclone rcd --cache-dir` 生效（进程启动时解析一次） | 缓存目录不能靠 RC 改 |
| duration 字段拒绝纯数字，接受字符串 `"5s"` / `"1h"` | |
| `config/create` 同名是**整段替换**；写回 obscured 值**必须** `opt.noObscure=true` | 否则二次加密静默损坏密码 |
| `mountPoint` 路径**必须不存在**；卸载要用反斜杠形式 | |
| **同一个 fs 两端**调 `operations/copyfile`：目标名落在源旁边、**源原样保留**（字节 hash 相同） | B1 的依据：同端点内的复制不需要第二套机制，也不会把 复制 变成 剪切（`ServerSideCopyTests`，真 rclone + alias remote） |
| **同一个 fs 两端**调 `operations/movefile`：目标处出现、**源目录里不再有这个对象** | B1 的依据：同端点的剪切用 move 而不是「复制 + 删源」——字节不动，也就不需要删除确认；`RcloneRcClient.MoveFileAsync` 走 `CallAsync`（载荷里**没有** `_async`，也不返回 jobid），所以它是同步等回复的那类调用，和 `copyfile` 的 `_async` 腿不同。`RemoteEndpointManagerWindow` 的 重命名 早就在用同一条腿 |
| `operations/copyfile` **原生支持跨 fs**；`drive` backend 默认 `use_trash=true` | B8/B9 的地基 |
| **`operations/copyfile` 中途只有 rclone 自己的 `<name>.<id>.partial`**，目标名要等 job 成功才出现；`job/stop` 后目标目录**为空**（日志 `Removing partially written file on error`）；对**已存在**的目标名是直接**覆盖** | B0 的依据：跨端点复制可以不过 Feng Sync 的 staging，失败/取消不会留下半成品，重试前不需要清理。**残余风险**：daemon 被硬杀时 rclone 的 `.partial` 会留在目标目录，Feng Sync 叫不出它的名字，也就清不掉 |
| `operations/copyfile` 走 `_async`+`_group` 时，进度只能从 `core/stats` 读；RC **没有** offset 写，所以远端→远端**不可能续传** | 复制行的按钮只能是「重试」，传输窗必须明说会从头再来 |
| rclone 的 s3 backend 自带 **53 个 provider**（含 `TencentCOS`/`Alibaba`/`HuaweiOBS`/`Qiniu`/`NetEase`），**没有 Backblaze** | 「加国产 S3 兼容」是零代码路径；B2 走 s3 的通用 `Other` provider |
| OAuth 状态机对所有 OAuth backend **完全相同** | Drive/OneDrive/Dropbox/Box 共用一套流程 |
| `rclone mount` 的 fs 串是 `<name>:`，在**挂载时**解析 | 改名时若有活跃挂载 → 直接阻断（A2 已实现） |
| rclone 的 HTTP 客户端**只读 `HTTP_PROXY`/`HTTPS_PROXY`**，**不读 `ALL_PROXY`**；RC 无全局 proxy 透传 | 代理只能靠子进程环境变量传；`RcloneProxyResolver` 把 only-`all_proxy` 镜像进 http/https（A5） |
| 手工 curl 探测 daemon：启动参数是 `--rc-addr/--rc-user/--rc-pass`（**不是** `--rcaddr`），就绪探测调 `core/version`（**没有** `rc/version` 这个方法） | 下一次做 §6 的实证不必再撞一遍 |
| `operations/stat {fs, remote}` **存在**，答 `{"item": {...}}`；**对象不存在时答 `{"item": null}` 而不是报错** | B3 的地基：「云端已没有这个文件」是一个**值**（`CloudItemStat.Missing`），不需要分类异常，也不该拿旧行的数字继续填面板（`RcloneStatFactTests`） |
| `operations/stat` 与 `operations/list` 的每个对象都带 **`MimeType`**（目录是 `inode/directory`），而浏览器的条目模型从来没读过它 | 属性里 `MIME 类型` 这行只能来自 stat；列表扩展名不是答案 |
| **校验值不在 `opt` 里点名就不给**：`opt.hashTypes` 要的是**名字数组** `["md5"]`；位域 `opt.hashes` **被静默忽略**；`opt.HashTypes`（大写）报 `ListJSONOpt.hashTypes of type []string`；未知名字报 `unknown hash type "bogus"`；`hashTypes: []` 合法；**目录永远不带 `Hashes`** | 三种拼错的后果里有两种是**静默**的，读起来跟「这个后端没有校验值」一模一样 —— 所以 `CloudItemDetails.HashPreference` 是唯一被发出去的名单，而「没算过」和「此后端未提供」必须是两行不同的话（`RcloneStatFactTests` 用 `bogus` 把这条路钉死） |
| 256 MiB 本地对象实测：不问校验值 **154 ms**、问 md5 **487 ms**、问 md5+sha1+sha256 **856 ms** | 每个被点名的类型大约再走一遍对象 → 校验值读取**只能由用户显式触发**，打开属性不得问 |
| `operations/hash`、`operations/checksum`、`operations/get`、`operations/geturl` 在 v1.75.0 **不存在**（404 `couldn't find method`）。**但 `operations/hashsum` 存在**：载荷是 `{"fs","remote","hashType"}`（**单个**类型字符串），答 `{"hashType":"md5","hashsum":["<hex>␣␣a.txt"]}`（sum-file 那种「哈希 + 两个空格 + 路径」的行），未知类型 500 `unknown hash type "bogus"`，`download:true` 被接受 | B3 选 `stat + opt.hashTypes` 而不是它：一次调用能同时拿回大小/时间/MIME **和三个校验值**，而 hashsum 要**每个类型各一次往返**才凑齐同一条链 —— 不是「没有别的路」，是这条路更贵。B4 若要做「复制哈希」再回来读这行格式 |
| `operations/publiclink {fs, remote}` **存在**，支持的 backend 答 `{"url": "<link>"}`（用 bundled 二进制 `rclone serve s3` 起 loopback S3 实测，`RclonePublicLinkFactTests`） | B4 的地基：链接是 backend 给的**不透明字符串**，本应用只负责复制，不许解析、不许改写、不许由它推断有效期 |
| **`operations/publiclink` 的多写字段被静默忽略**：`expire:"1h"` **会**生效（签名里 `X-Amz-Expires=3600`），超过上限时 rclone 打 NOTICE `Public Link: Reducing expiry to 1w as off is greater than the max time allowed` 并**clamp 到 604800**；`unlink:true` 被 **S3 忽略** | 不能因为「发了参数」就以为「参数生效」：有效期/撤回**不能进措辞**。B4 因此只发 `fs` + `remote` |
| **目录（S3 前缀）**调 `operations/publiclink` 答 404 `object not found`；**已消失的对象**同样 404；**没有这功能的 backend**（local / alias）答 HTTP 500 `"Local file system at //?/C:/… doesn't support public links"` | 文件夹在**本地**就拒（`NotAFile`），不去转译 backend 的话；而那句 500 会被 `RcloneFailureClassifier` 读成 `Temporary`（`httpStatus >= 500`），**必须先读文字再看 category** |
| `operations/fsinfo {fs}` **存在**，答里带 `Features`，其中 `PublicLink` 是布尔（local `false`、s3 `true`） | **故意没用**：为一个开关多花一次往返不值得，而且「backend 拒了」这条话术本来就必须能处理。要按功能隐藏菜单项时再回来读它 |
| 手工探测 RC **没有 `/rc/` 前缀**：`RcloneDaemon.BaseUri` 是 `http://127.0.0.1:<port>/`，`CallAsync` 按 `new Uri(baseUri, operation)` 打 | 上一次探测把每个方法都写成 `/rc/<method>`，于是**连 `operations/stat` 都 404**，一度误判成「方法不存在」。`core/commands`、`rc/commands`、`core/help`、`options/info` 在 v1.75.0 确实**不存在**，`--rc-enable-mgmt-apis` 是**未知 flag**（daemon 退出码 2），所以方法名单只能靠逐个试 |
| RC **没有** `operations/trash`（404，`TechnicalMessage` 含 `couldn't find method`） | **B5 的地基，也是 §6 第 1 条的答复**：没有「交给 backend 回收站」这条路可问，软删除**只能**是应用自己的文件夹。`--drive-use-trash` 是 backend 的**配置**开关，不是 RC 能调的动作，所以它救不了 S3/SFTP，也不改变形状 |
| `operations/movefile` 的**目标父目录不存在时它自己建**（`reports/2026/top.txt` 一次落全） | 还原不需要先 mkdir；同时意味着**落点算错就真落错了**，没有「目录不存在」这一层保护 |
| `operations/movefile` 对**已存在**的目标文件是**覆盖**（内容换成进来的那份，源消失） | 空闲名必须**本地算**（问目录列表，别问 move 的答复）。B5 的 `PlacementsFor` 因此先 `NamesAsync` 再决定用原名还是 ` (1)` |
| `operations/movefile` 的目标名是一个**目录**时报错（文字含 `directory`），且**源原样留着** | 撞到一个文件夹时 rclone 不合并也不覆盖，所以「移进去」失败要说清是谁挡路；源没动，因此失败不算半个删除 |
| `sync/move {srcFs, dstFs}` 落到**已存在**目录 = **合并**：同名以**进来的一方**为准，目标独有的文件留下，而**源目录变成一个空壳**（`GetFileSystemEntries` 为空） | B5：文件夹进/出回收站后要清那个壳，但**清之前必须确认它真的空了** —— 顺手 `purge` 会把用户可能还在用的内容一起带走 |
| `sync/move` 落到**新路径** = 整棵树搬走、**源目录不再存在** | 壳的清理只能是「空了才 rmdir」，不能假设每次都留壳（`RemoveIfEmptyAsync`） |
| `operations/rmdir` 打**已不存在**的路径：HTTP **500** + 操作系统自己的措辞 → 共用分类器读成 **`Temporary`**；同一时刻 `operations/stat` 答 `{"item": null}` | 「还在不在」要用 `stat` 问，**不要**拿 `rmdir` 的报错猜，否则一次正常的重复清理会被报成「rclone 与云端的连接暂时中断，检查网络后重试」（`CloudRecycleBinFactTests` 把这两行钉在一起） |
| `operations/mkdir` **幂等**（重复调成功）、**会建父目录**，但目标名已被一个**文件**占着时报错而不是替换（那个文件内容不变） | B5 的「回收站」按钮可以放心做成「先 mkdir 再进去」；而回收站名字被文件占用时是一次**明确失败**，不会出现「点进去还是原目录」的假象 |
| 移走一个**文件**不会带走它所在的目录（回收站里的 `deep/nested` 还原后仍在） | 只有**目录**移动会留壳；文件路径上的空目录一律由用户自己决定，应用不顺手删 |
| `operations/list` 的载荷**同时**带 `fs`（`nas:`）和 `remote`（`team/docs`，即 fs **内部**的那一段路径） | B8 的地基：一个按 `remote` 建树、却只记 `remote` 的假 RC，会把「跳到**另一个**端点的同一处」读成「同一处列了两次」，于是「一次跳转只列一个目录」这条断言什么也没证明。假 RC 因此记 `fs + directory`，`Assert.Equal([$"{Files}:{FilesInbox}"], standIn.Listed)` 才有意义 |

### WPF / 测试

| 事实 | 影响 |
| --- | --- |
| `UiConsistencyGuardrailTests.PageLocalButtonVisual` 的正则**只对 `<Button>` 元素的属性区**生效 | 给 `TextBox` 设 `Padding` 是安全的；`<Button>` 上一律不能写 `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush` |
| `LiveTextAccessibilityTests` 扫两件事：**`TextBlock` 的动态文本**（不许带静态 `AutomationProperties.Name`，必须有 `AutomationId`）＋（B7 起）**浏览器那一行必须自己带绑定来的名字**（`FileList` 的 `ListViewItem` 样式里 `AutomationProperties.Name="{Binding …}"`，字面量也算 fail） | `TextBox`/`ComboBox`/`Button` 上的静态 `AutomationProperties.Name` 是允许的；行名之所以进栅格是因为 tile 内部文字不入 UIA（见下面那行）|
| 无桌面 WPF 测试模板 = `RunSta(Action)` + `EnsureApplicationResources()` + **从不 `Show()`** | 毫秒级，不需要前台会话 |
| UI 测试**默认桌面无关**：窗口停 `-32000`，输入走 UIA pattern / posted 消息；`-ShowOnScreen` 恢复前台 | 不得引入合成鼠标移动（`DesktopFreeUiHarnessTests` 会拦） |
| `RcloneLifecycleHost.GetClientAsync` **会真的起 rclone 进程**，而 `IAppServiceHost.RcloneHost` 是 **sealed 具体类**（无法替换） | 「不碰 rclone」的测试必须靠**不触发 `BrowseAsync`**，并用「`StatusText` 没被写过」来证明（`AssertNoDirectoryListing` 就是这个）。B0 给 `CloudTransferQueue` 也加了同款**可选 RC 客户端工厂**构造参数（`CloudFileManagerService` 早就有），于是「队列发了什么载荷」能在假 RC 上断言（`CloudTransferCopyTests`）；没有接缝的那条腿照旧靠不起 daemon 的写法 |
| 桌面场景里 `AutomationProperties.Name` 会**覆盖** `Content` 派生的名字 | 别想用 UIA 的 `Name` 去断言一个「标签会变」的按钮；改断言它的 `SelectionPattern` / 布局 |
| WPF 跨进程 UIA 客户端拿不到 `TextBlock` 的 `TextPattern` | 断言文本要靠 `Name`（因此动态文本 `TextBlock` 必须**去掉**静态 `Name`） |
| **一个进程/AppDomain 只能有一个 `System.Windows.Application`**，而 `Application.Current` **只有创建它的那个线程看得见**；一个还活着（或正在关闭）的实例会让别的线程 `new Application()` 直接抛「Cannot create more than one…」/「The Application object is being shut down」 | B1 的窗口测试每条跑在自己的 STA 线程上，**不能**各自重建 Application。规则：**只在 `Application.Current is null` 时构造并配置它，并记住自己是否拥有它**；拥有它的那个线程负责在结束时 `Shutdown()` + `dispatcher.InvokeShutdown()` 把它释放掉，否则下一个建窗口的测试类会死在 `new Application` 上而不是自己的断言上（`CloudBrowserPasteTests` 的 `MergeApplicationThemes()` 返回 bool 就是为了这个）。`IClassFixture` 共享 Application 的写法**试过且不行**：`Dispose` 里无论 `BeginInvokeShutdown` 还是 `BeginInvoke(Shutdown)` 都 release 不掉，因为 WPF 只在其 dispatcher 关闭时销毁 Application |
| `ShutdownMode` 默认 `OnLastWindowClose`：测试里第一次 `window.Close()` 会**连带结束整个 Application 和正在 pumping 的 dispatcher**，粘贴剩下的续传就永远不会跑；在一个**不属于本线程**的 Application 上设 `ShutdownMode` 会 `VerifyAccess` | 自己建的 Application 一律设 `OnExplicitShutdown`；别人的**只读不写**。窗口可能已被测试自己关掉，收尾 `Close()` 要容许 `InvalidOperationException` |
| `Dispatcher.InvokeAsync` 的续传只在**它自己的线程 pumping 时**才执行；线程停在 `Thread.Join` 里 = 续传永不跑（窗口那句状态行就是续传） | 异步窗口行为必须在 STA 线程里 `Dispatcher.PushFrame(frame)` pumping，任务完成时把 `frame.Continue = false`。**并且要有 watchdog**：`System.Threading.Timer` 到点 `dispatcher.BeginInvoke(() => frame.Continue = false)`，否则一条卡住的粘贴会挂住整个 UI 套件（B1 撞到过一次 >10 分钟无输出的后台跑） |
| **弹窗（`ShowDialog`）在进程内测试里没人能答**，而给 `Window.Owner` 赋一个**从未 `Show()` 过**的窗口会抛 `InvalidOperationException`；这异常在 `async void` 事件处理器里是**未处理异常 → 测试宿主进程崩溃**（`Test host process crashed`，整条 UI 套件被 abort，不是某条测试 fail） | 凡是要问用户的决定都走**可注入接缝**（B2 的 `AskConflict`），弹窗只做接缝外面那层壳；接缝返回 `null` 表示**用户取消**，所以判断「有没有接缝」要用 `AskConflict is { } ask`，**不能用 `??` 兜到真弹窗**（B2 就这么崩过一次测试宿主） |
| **`Clipboard.SetText` 是进程级、机器级的状态**，需要 STA，而且会被别的进程占住而抛异常 | B4 的剪贴板写入走 `CopyLink` 接缝：桌面无关的套件**不许换掉用户的复制缓冲区**，而「云端给了链接、剪贴板没收下」必须是一句**独立的话**（`CopiedNotStored`），不是「没有取到分享链接」 |
| **返回 `Task` 的窗口 harness（`RunSta(Func<Task>)` / `WithWindow(...)`）不 `await` 就等于没测**：断言在后台线程上继续跑，失败落进一个没人观察的 Task，测试**报绿**；更糟的是这些线程彼此重叠，两个同时建 WPF `Application` 会把整轮 run abort 掉（`Test Run Aborted`，只单独跑那个类时立刻重现） | **B5 查实的既有缺陷**：`CloudBrowserShareTests` 十条全是 `public void` + 不 await，因此**从没真正断言过**，而其中一条一旦真正跑起来就 `NullReferenceException`（它没先 `Select` 一行就取行菜单）。已修：十条改 `async Task` + `await`，并按 `CloudBrowserDetailsTests` 的形状补上选中行；`UiTestAwaitGuardTests`（Core 阶段）扫 `tests/FengSync.UiTests/*.cs`，凡是调用返回 Task 的 harness 而语句前缀里没有 `await`/`return`/`=>` 就失败。**规则：要么用同步的 `RunSta(Action)`（`void` 测试是对的），要么 `async Task` + `await`，中间态不存在。** |
| 「移动之后云端还剩什么」这类断言要求假 RC **是 stateful 的**（mkdir 建目录、move 建父目录、没建过的目录答 404），否则批量结束后的那次 `BrowseAsync` 会因为**错误的原因**成功或失败 | B5 的 `CloudBrowserRecycleTests`：`Tree(paths)` 从完整文件路径反推目录表，`MovesLeaveTheirSource` / `RefusesMoveOf` 两个开关把「合并留壳」和「落到目录被拒」这两条**相反**的真实 rclone 行为分别打开，所以「只在源还留着时才清壳」是被钉住的，不是被 stub 掉的理由 |
| **`KeyEventArgs` 没有单参构造**：唯一可用的公开构造是 `(KeyboardDevice, PresentationSource, int, Key)`，第二个参数**不许为 null**，而 `InputManager.PrimaryKeyboardDevice` 是**实例属性**（`InputManager.Current.PrimaryKeyboardDevice`） | B6 的键盘测试这样造真实路由事件：临时 `new HwndSource(new HwndSourceParameters())`（隐藏、零尺寸）当 source，`RaiseEvent` 之后**立刻 dispose**（路由是同步的，届时已经用完）。拿不到 source 就退化成直接调 internal 方法，那就测不到「挂在哪个元素上」了 |
| **`RaiseEvent` 出来的 `KeyEventArgs`，其 `OriginalSource` 就是被 raise 的那个元素**（实测；`e.Source` 因此是同一个东西、冗余） | typing 闸门 `e.OriginalSource is TextBox` 在真实输入与测试事件下读**同一个字段**，所以「地址栏里的 F2 不该打开重命名」这类断言能用 raise-event 来证。反过来说：断言 `Handled` 时要按实测写 —— 在 `TextBox` 上 raise `Delete` 得到的是 `true`（控件自己认领了），不是「冒泡上来被窗口处理」 |
| **`TextBox` 会自己吃掉 Del 与 Ctrl+A（它们根本不冒泡），但不吃 F2**；`Keyboard.Modifiers` 读的是**物理键盘**，合成事件改不了它；WPF 的 `ListBox` **没有**内建 Ctrl+A，裸字母留给 type-to-select | 三处后果：① typing 闸门只对 F2 这类键真正起作用，把 `IsTyping` 写死成 `false` 立刻让地址栏那条测试 fail（闸门不是装饰）；② 带修饰键的用例只能把 Ctrl/Shift **当参数传进** `HandleKeyAsync`，只有 F5/F2/Z 这类「意义不随修饰键变化」的键才走真实事件，因此「XAML 处理器读修饰键读对了」这一环**未被自动化证明**，桌面场景也伪造不出修饰键所以**没有**加键盘场景；③ 表不认的键必须保持 unhandled，否则列表的按字母选中被悄悄弄坏 |
| **WPF 解码失败的异常形状有三种，且都不在 `IOException` 那一族里**（对 bundled WPF 实测）：字节没有任何 imaging component 认领 → `NotSupportedException`（"No imaging component suitable"）；容器根本认不出来（例如只有 8 字节 PNG 签名）→ `FileFormatException`（0x88982F07，**派生自 `SystemException`**，所以 `when (ex is IOException …)` 的过滤器**接不到它**）；声称的格式而流不完整 → `InvalidOperationException` | 两条解码路径（`CloudThumbnailStore.Decode`、`CloudBrowserPreview.DecodePicture`）**必须共用** `CloudBrowserTiles.RefusesDecode`。漏掉一种不是「少一张缩略图」，是异常顺着 tile 轮次**逃出到 UI 线程上把窗口炸掉**（B7 第一版就炸在 `NotSupportedException`；`FileFormatException` 那条由 `A_container_the_decoder_does_not_recognise_leaves_no_tile_either` 钉住） |
| **`ListView.View = null` + 换 `ItemTemplate`/`ItemsPanel` 就是同一份行集合的第二种画法**：`Items.Refresh()` **不会**清掉 `SelectedItems`（实测：每条 tile 落地都刷一次，选中行始终在） | B7 的网格切换只动 `View`/`ItemTemplate`/`ItemsPanel`，**不换 ItemsSource、不发 `operations/list`**。`The_toggle_redraws_the_same_rows_and_keeps_which_ones_were_selected` 逐行 `ReferenceEquals` + 选中存活，`Switching_the_view_does_not_re_list_the_directory` 断言 POST 为空。**但探针查实**：把 `ItemsSource` 换成副本（`_entries.ToList()`）在**进程内测不出来** —— 从未 `Show()` 过的控件不靠视觉树维持选中，`SelectedItems` 原样留着，那条测试照样绿。所以「只有一份集合」这一半只有代码与 §2 记的桌面场景在担保，别把它当成被自动化钉住了 |
| **`DataTemplate` 里的 `IValueConverter` 只拿得到那一行**，模板本身够不着窗口（store、当前端点都在窗口上）；而 converter 里发起读取等于**每个可见行每次 layout 都在 UI 线程上联网、且无法取消** | 缩略图走窗口作用域的 `CloudTileSourceConverter` 资源，`Peek` 委托在构造函数里接线，**只读缓存不加载**；加载由 `RestartTileRound` 按当前 `_entries` 里 `OffersThumbnail` 的行发起，换目录时 `_tileRound?.Cancel()` 掐掉上一轮（**故意不 Dispose**：在途的 read 还会往那个 source 上注册续传，Dispose 会在 read 里抛）。tile 落地用 **Background 优先级合并的 `Items.Refresh()`**，一轮结束再补一次 Normal 优先级刷新，所以 `await window.PendingThumbnails` 才等价于「画完了」 |
| **绑定了 `GridView` 的 `ListView` 不写任何本地 `ItemTemplate`/`ItemsPanel`/`ItemContainerStyle`（三者实测都是 `unset`）**，行是靠 `ListView` 自己的默认样式画出来的；因此 `ItemsPanel = null` 是一个**本地 null 值**而不是「恢复默认」，它让**一行都不呈现**（实测：可见单元格文字从 2 条变 0 条），而集合里每条行都还在 | 列表视图必须用 `ClearValue(ItemsControl.ItemsPanelProperty)` 复原，**不能**赋 `null`。这条是 B7 唯一一次真实门禁 fail 的根因（`sftp-ui` 在第一次浏览就找不到 `remote-proof.txt`）。进程内那条 `Assert.Null(window.FileList.ItemTemplate)` **恰好把 bug 状态读成正确**（本地 null 也返回 null），所以断言改成 `ReadLocalValue(...) == DependencyProperty.UnsetValue`；探针验证过：把 `= null` 放回去，这条立刻 fail |
| **网格画法里 tile 内部的文字根本不入 UIA 树**（实测：`RawViewWalker` 下 grid 的 `DataItem` **零子节点**，而它的 `Name` 是数据对象的 `ToString()`，即整条 `CloudFileEntry { … }` 记录）；列表画法的行则把单元格文字作为子 `Text` 暴露 | 行必须**自己带名字**：`FileList.ItemContainerStyle` 上 `Setter Property="AutomationProperties.Name" Value="{Binding NameDisplay}"`。这一个 setter 同时修掉两件事 —— 网格行从此按文件名可寻址（`Find-Name`），以及**两种画法**的行都不再对读屏念整条记录。桌面场景两条 toggle 断言都同时要求 `BoundingRectangle` 有尺寸：那才是「画出来了」而不是「树里有个节点」 |
| **桌面场景启动的是 `tests/FengSync.UiTests/bin/.../FengSync.exe` 那份副本**，`dotnet build src/FengSync` **不会**刷新它 | 只建 `src/FengSync` 之后跑场景，跑的是**旧二进制**，会「绿」在它本该检查的那个改动上（B7 排查时踩过一次：同一份 bug 源码，旧副本跑绿、重建 UiTests 后立刻在第一次浏览 fail）。改完应用要么建 `tests/FengSync.UiTests`，要么建解决方案 |
| **一个 Content 是字符串的 `Button` 在 UIA 里同时是「按钮」和「一个同名子 Text」**（实测：`Find-Name $manager '取消收藏'` 找得到，因为 Content 成了内部 `TextBlock` 的 Name），而 `Find-Name` 打的是 NameProperty 的**精确**匹配（不是子串） | 一个**会翻状态**的按钮因此**不许**带静态 `AutomationProperties.Name` —— 静态名会顶掉 Content 派生的名字，那正是批次 0 那条 `LiveTextAccessibilityTests` 规则要防的事，只是这一轮防的是按钮而不是 `TextBlock`。B8 的收藏按钮照此做：场景先按 收藏此目录 找到并点它，再按 取消收藏 等它翻过来 |
| **`ComboBox` 的行要先 `ExpandCollapsePattern.Expand()` 才进 UIA 树，选中行靠 `SelectionItemPattern`（`Select-Ui`）**；而一份被整份替换的 `ItemsSource` 若把 `SelectedItem` 留着，第二次点同一行就**不再抛** `SelectionChanged` | 跳转列表在场景里因此可达（展开 → 按包含端点名的行找 → 选），但「选完立刻归零」这句留给进程内测试断言（`WaitUntil(() => window.FavoriteJumpBox.SelectedItem is null)`）：桌面场景里等一次事件重抛是拿不准的。归零也必须**在应用这一行之前**做（`_applyingJump` 那层），否则归零自己又抛一次事件 |
| **窗口一开始写盘，测试就得隔离 `FENGSYNC_DATA_DIR`，而且要在建窗口之前**（`AppDataPaths.Root` 每次被读时都解析环境变量，但 `CloudBrowserBookmarkStore` 把路径**在建的时候就定下来**，之后改环境变量对它无效） | B8 的 `WithWindow` 在 try 里换环境变量、在 `finally` 里还原环境变量 + `AppServices.Install(previous)` + 删临时目录，并把 `window.Close()` 包成允许 `InvalidOperationException`（窗口可能已被测试自己关掉）。同一形状补进了**5 个既有窗口测试**的 harness（Details / Paste / Recycle / Share / Shortcut）—— B8 **之前**窗口什么都不写，所以那五个 harness 从来没换过数据目录，B8 之后不换就会写进开发者自己的 app data 并在下一次运行读回来 |
| **进程级文件锁里的 await 默认回到调用方的 `DispatcherSynchronizationContext`**：那个线程一旦停止 pumping，`finally { Gate.Release(); }` 就永远不跑，锁**再也没人解**（实测：`save held` 之后没有 `save release`，后面每条会写书签的窗口测试各卡 90 秒） | `CloudBrowserBookmarkStore` 的 gate 区间因此全部 `ConfigureAwait(false)`（`CloudThumbnailStore` 同规则），并由 `A_write_whose_thread_stopped_pumping_still_frees_the_file_for_the_next_reader` 钉住 —— 撤掉其中一处立刻 fail（10 秒内，不是挂死）。**同一形状未改**：`CloudEndpointMetadataStore` / `CloudEndpointRename` / `Filtering` 的写锁，因为没有测试能证明它们卡住。生产侧的对应风险是「应用正在关闭时的一次写入」与「任何在 UI 线程上 block 的调用」 |
| **`FileBrowser_KeyDown` 的 `e.Handled = true` 写在 `await HandleKeyAsync(...)` 之后**，所以 raise-event 之后**同步**读 `args.Handled` 读到的一定是 `false` | B6 的 F2 断言原来靠 `WaitUntil(FileMoves == 1)` 顺手等完剩下的轮次，B8 给 listing 末尾多加一次书签写入就把它打回红（断言踩在时序上，不是产品坏）。规则：**断言「按键被认领」必须等命令整体跑完**（`WaitUntil(() => args.Handled, …)`），而断言「没被认领」可以同步读 |
| **一份用户选择可以有第三份表示：磁盘上的文件、纯规则类、以及窗口内存里那份列表**。前两份有测试，第三份只在真实外壳里暴露 | B8 的 `sftp-ui` fail 就是这么来的：`RenameRemoteAsync` 改对了磁盘那份，而窗口仍然拿着旧名字，紧随其后的那次 listing 记一次访问、把文件写回旧端点名，重命名原地作废（修法是重命名后**先重读再刷新**）。教训不是「再补一条单测」，而是**任何改动一份持久表示的写路径，都要问谁还内存着同一份**；进程内的动作测试（"这次调用改了什么"）结构上看不见它，只有走完整状态的场景看得见。**未堵**：同一进程里第二个已打开的窗口照旧会拿自己的旧副本覆写 |

---

## 5. 剩余路线图（B1 → E）

> 这一节同时是那份 307 行改进计划的替代。**A0–A7、B0、B1、B2、B3、B4、B5、B6 与 B7 已完成**（见 §2），本节的 A5/A6/A7/B0/B1 只留设计与规则的留档。
> **剩余项的顺序与依赖**：B 与 C 共享「队列泛化」，所以 **B0 必须在 B 的跨端点复制之前落**（已落）；
> D 与 A/B/C 完全解耦；E 是收尾集成。**下一批是 B9–B11（B1、B2、B3、B4、B5、B6、B7、B8 已落，见 §2）。**

### A7. 端点导出/导入（原 A9）—— ✅ 已完成，见 §2

**实现已完成**（见 §2 的 A7）；下面是设计与安全规则的留档。地基齐了：`CloudEndpointMetadataStore` 已有原子写（temp + `File.Move(..., true)`）与 `Gate` 串行化；
`RcloneConfigFile` 已有第 0 列段头的解析基础。**复用它们，不要新造写盘逻辑。**

**导出什么**（非敏感子集，**值一律留空**）：
- `cloud-endpoints.json` 的记录本身不含凭据：`RemoteName` / `Type` / `Provider` / `Bucket` / `Subdirectory` /
  `AliasOf` / `Group` / `Tags` 直接可导。
- 每个端点的**参数名清单**：从 `rclone.conf` 的段里读**键名**。⚠️ `sftp.pass` 是 obscured、`s3.secret_access_key` 是明文
  —— **两种都只写键名，不写值**。
- 文件头带 `Version` + 生成时间，方便将来演进与拒绝不认识的版本。

**导入怎么做**：让用户逐端点补参数，再走**既有的创建路径**（`CloudEndpointEditorWindow` / `CloudEndpointService`），
由它把值写进 `rclone.conf`。两条硬约束：**导入不得直接写 `rclone.conf` 的凭据段**；
**导入的 JSON 不可信** —— 远端名与路径要校验，防止 `:` 段头注入或 `../` 越界。

**测试点**（照 §7 铁律）：
- 断言导出内容**不含** conf 里的密码/secret/token/obscured 串（用已知口令的 SFTP 与 S3 端点跑）。
- 参数名清单含 `host`/`user`/`pass`，但 `pass` 的值为空。
- 导入被篡改的 JSON（多出段头、远端名带 `:`）应被**拒绝**而不是写坏 conf。
- 往返：分组/标签/别名（S3 强制前缀）都保留。

### B0. 队列泛化（**前置**，与批次 C 共用）—— ✅ 已完成，见 §2

代码与测试清单在 §2 的 B0。下面是 **B1 接手时必须知道的三条规则**：

- **复制进队列，不进轻量直连**（§5「已选定的设计决策」第 1 条）：跨端点复制真的走网络字节流，可能很久，
  用户需要进度 / 取消 / 重试 / 历史。B1 只需组装 `EnqueueCopy(源URI, 源路径, 目标URI, 目标路径, 大小)`。
- ⚠️ **远端 → 远端无法 append，会退化为不可续传** —— 设计取舍不是 bug，UI 已经明说
  （`HasCopyTasks` → hint，`RestartText`/`CanContinueFromStaging` → 行内按钮）。**不要**为了「看起来能续传」
  去给它造 staging：rclone 没有 offset 写，造出来的 staging 既校验不了也接不上。
- **journal 的新字段只能是可选尾字段**（`TargetUri` / `TargetPath`），`CloudTransferDirection` 只能追加。
  复制的目标**永远不写进 `LocalPath`** —— 那是处处按本地路径解释的字段，写进去下一版会把它当下载去建目录。
- 要断言「复制到底发了什么」就注入假 RC 客户端（`new CloudTransferQueue(journal, clientFactory)`）；
  要断言「字节到底落在哪」才用 `tests/FengSync.Tests` 的真 rclone + alias remote。两种都在 §2 的 B0 里有现成样板。

### B1–B11. 文件浏览器（缺口最大的一组）

> 需求盘点原本列了 21 项（B1–B21）；其中 P0 的 7 项 —— 新建文件夹、重命名、删除、多选批量、搜索、排序、
> 面包屑 + 地址栏 —— **已在批次 1 落地**。下表是**剩余 11 项**，「现状 / 缺口」与「参考做法」两列是从原盘点文档
> 一并搬过来的，以免那份文档消失后丢掉判断依据。**B1、B2、B3、B4、B5、B6、B7、B8 已落地（见 §2）**，下表从 B9 起都是待做项。
> 给 B6 的一条现成接线：`SetClipboard(Copy/Cut, 多选行)` 和 `Paste_Click` 已经是内部方法，
> Ctrl+C / Ctrl+X / Ctrl+V 只需把键盘事件指到它们，**不要**另造一套剪贴板状态。
> ✅ **B6 照此落了**：剪贴板状态**一个都没新造**，`SetClipboard` 与（抽出来的）`PasteAsync` 就是 Ctrl+C/X/V 的执行体；
> 同一个理由适用于其余三个键 —— Del / Shift+Del / Ctrl+Del / F2 / Ctrl+A 分别调 `RecycleSelectedAsync` /
> `DeleteSelectedAsync` / `RenameSelectedAsync` / `FileList.SelectAll()`，键盘只是**第二只手**，不是第二套规则。
> 给 B9 的一条现成接缝（B3 的属性就是照这个形状读的）：`PlanPasteAsync()` 已经把目标目录的**一次读取**
> 同时用于判冲突和取名，递归统计大小也要读目录时照这个形状走，别再 `ListAsync` 第二次。
> 给 B5 的一条现成接线：删除**已经**有 `ConfirmAndDeleteAsync`（含批量阈值 10 的二次确认与 `CloudDeleteSafety`），
> 软删除要改的是「问的那句话」和**删完之后的说法**，**不要**另造第二套删除门槛。
> ✅ **B5 照此落了**：`ConfirmAndDeleteAsync` 只多出一个 `insideRecycleBin` 措辞开关，阈值、二次确认与
> `DeleteEntriesAsync` 一字未改；「清空回收站」的问题现在有 `ConfirmDelete` 接缝可断言，所以「11 项仍是**两个**问题」
> 是真的被钉住的，而不是靠人记得去看。
> 给 B7 的一条约束：浏览器**只有一份行集合**（`VisibleEntries`：搜索框与排序之后的可见行），键盘（B6）、
> 剪贴板（B1）、回收站（B5）读的都是它。网格视图／预览因此应当是**同一份集合的另一种画法**，不是第二个列表 ——
> 一旦复制出第二个，`Ctrl+A` 与行菜单就会各说一套（另见 §3：重建 WPF 列表会抛一次「无选中」事件）。
> ✅ **B7 照此落了**：网格切换**只动** `FileList.View`/`ItemTemplate`/`ItemsPanel`，`ItemsSource` 仍是那一份 `_entries`，
> 切换前后逐行 `ReferenceEquals`；行怎么到达缩略图缓存也不是第二个 view model，而是**窗口作用域的
> `CloudTileSourceConverter` 资源**（`Peek` 委托在构造函数里接线，只读缓存、绝不加载 —— converter 里联网等于
> 每个可见行每次 layout 都在 UI 线程上发起不可取消的读取）。
> ✅ **B8 照此落了**：收藏与最近访问**没有**变成第二个列表控件，也没有新增一份行集合 —— 两个 `ComboBox` 绑的是
> `CloudBrowserBookmarks.JumpItems(...)` 现算出来的行，`JumpToAsync` 走的仍是 B7 之前就在的那条 `NavigateToAsync`。
> 顺带查实并修好一条**跨批次的腿**：A2 的端点重命名原本只搬 `rclone.conf`、`cloud-endpoints.json`、`profiles.json`
> 三份文件，收藏一旦落盘就成了**第四份按端点名存东西**的文件，`CloudEndpointRename` 现在把它一起备份、一起改、
> 一起回滚。**日后任何新增的「按端点名存东西」的文件都必须进那一次 rename**，否则重命名会把用户记住的地方清空。
> 给 C7 的一条现成接缝：B4 已把 `PublicLinkAsync` 与「链接是 backend 给的不透明字符串」这条钉好，队列行菜单的
> 「复制链接」直接复用 `CopyLink` 那个接缝，别在 C 组再摸一次系统剪贴板。

| 本计划 | 原编号 | 内容 | 现状 / 缺口 | 参考做法 |
| --- | --- | --- | --- | --- |
| B1 | B8–B9 | 复制 / 剪切 / 粘贴 + 跨端点复制 | ✅ **已完成**（`3c9322b` + `cd74a61` + `526a634`，见 §2）。跨端点复制进队列（`EnqueueCopy`），同端点的剪切走 `operations/movefile`，文件夹一律 skip 并说明原因，源删除只在它自己的复制完成之后才发生 | 网盘标配；Air Explorer 支持云到云 |
| B2 | B10 | 上传冲突细化 | ✅ **已完成**（`dedc2b5` + `1649dc6`，见 §2）。`KeepBoth`（`x.pdf → x (1).pdf`，相对目标目录的空闲名）与**逐文件询问 + 应用到全部**都齐了，上传与粘贴走同一个 `CloudConflictResolver`；`每次询问 / 直接覆盖 / 跳过同名文件 / 保留两者（重命名）` 四项进设置 | 网盘标配 |
| B3 | B11 | 属性 / 详情 | ✅ **已完成**（`844b93b` + `7370af0`，见 §2）。单对象 `operations/stat` 给大小/时间/MIME/校验值，列表快照只兜底；不存在的对象说「云端已没有这个文件」而不是编一套值；校验值**只在用户按「计算校验值」时**才读（一次点名约多走一遍对象）；文件夹大小仍是 `未统计`（留给 B9） | 网盘有「详细信息」 |
| B4 | B12 | 分享链接 | ✅ **已完成**（`3d1d532` + `1b4b7ed`，见 §2）。单文件行菜单 `复制分享链接…` → 先确认（说清「任何拿到链接的人无需登录即可读取」）→ `operations/publiclink {fs, remote}` → 复制到剪贴板。文件夹与多选一律本地拒绝（实测前缀答 404）；措辞不承诺有效期也不承诺撤回（backend 决定）；「云端已生成、剪贴板没收下」是独立一态 | 网盘核心功能之一 |
| B5 | B13 | 回收站 | ✅ **已完成**（hash 见 §2 的 B5 表行，同 §2 的 B5 小节）。RC 没有 trash 动词（§6 第 1 条），所以是应用自己的 `.fengsync-trash`，**位于端点根**（被 alias 强制前缀的 S3 端点根是空串，写 `RootOf("")` 钉住）；文件走 `operations/movefile`、文件夹走 `sync/move` + 只清空壳；还原回到它来的目录，名字被占就让位 ` (1)`；「清空回收站」问的还是 `CloudDeleteSafety` 那**一道**门槛（多一句「已经在回收站里」），阈值 10 的二次确认原样保留 | 网盘默认「移到回收站」，可还原 |
| B6 | B14 | 键盘快捷键 | ✅ **已完成**（`f45e06b` + `2299460`，见 §2）。F5 / Del / Shift+Del / Ctrl+Del / F2 / Ctrl+A / Ctrl+C / Ctrl+X / Ctrl+V 全无第二套规则：一张纯表（`CloudBrowserShortcuts.Resolve`）把按键映射成命令，命令调按钮已有的方法；处理器挂在**浏览器面板**的冒泡 `KeyDown` 上，端点与挂载列表保住自己的键；typing 闸门排在 F5 之后（`TextBox` 自己吃掉 Del 与 Ctrl+A，却不吃 F2） | 通用 |
| B7 | B16–B17 | 网格视图 / 缩略图 / 内置预览 | ✅ **已完成**（hash 见 §2 的 B7 表行）。网格是**同一份 `_entries` 的第二种画法**（只换 `View`/`ItemTemplate`/`ItemsPanel`，逐行 `ReferenceEquals` 钉住，切换不发 `operations/list`）；缩略图只给 WPF 内建能解码的格式，8 MiB / 64 px / 2 并发 / 64 MiB 缓存都有名字；内置预览只画**能解码的图片**与 **UTF-8/UTF-16 文本**，PDF/office/音视频**明确不做**（`net10.0-windows` 没有渲染器），不猜代码页，不可预览的行**一个字节都不读**，预览也不进缩略图缓存 | 网盘有列表/网格切换与在线预览 |
| B8 | B18 | 收藏 / 最近访问 | ✅ **已完成**（hash 见 §2 的 B8 表行）。两个列表是**两条不同的承诺**：收藏的顺序归用户（重复添加不挪位、永不为容量修剪），最近访问是应用写的队列（新的在前、一处一份、上限 20）。规则全在 `CloudBrowserBookmarks`（纯），落盘在 `CloudBrowserBookmarkStore`（`cloud-bookmarks.json`，**只存端点名与路径，一个凭据字段都没有**，照 A7 导出的同一条规矩）；两个 `ComboBox` 跳转列表 + 一个「收藏此目录 / 取消收藏」按钮，端点不在屏上时**隐藏而不是删掉**（`rclone.conf` 偶尔读不到不该清空用户挑过的列表），ToolTip 说出藏了几份 | 网盘有「我的收藏」「最近」 |
| B9 | B19 | 文件夹大小 | 需要递归统计，没有「计算大小」入口 | 网盘可在文件夹属性里看到 |
| B10 | B20 | 拖出整个文件夹 | **明确不支持**（代码里提示用户先进入文件夹） | 网盘 / Explorer 支持整目录拖出 |
| B11 | B21 | 版本历史 | 无；部分后端（GDrive、开了版本控制的 S3）可能支持 | 网盘有「历史版本」 |

B 组还有一条**只在这里出现**的约束：**并发数从 `const` 改为可配时**，要保证 `SemaphoreSlim` 重建不破坏在途任务；
`ActiveCount` 与 `MaxConcurrentTransfers` 在 `CloudTransferQueue.RunAsync` 里是**当作进度参数**传给
`EndpointTransfer.CopyToStagingAsync` 的，所以改并发会**同时影响续传判定**。
（B 组其余约束与 §7 的通用铁律重合，不再重复。）

### C1–C7. 传输队列

| # | 内容 | 关键改动 |
| --- | --- | --- |
| C1 | 限速（C5） | 用 RC `core/bwlimit`（已实证存在）设全局速率；设置页加「上传/下载限速」；队列在任务开始/结束时应用。**先实证作用域是全局还是 per-fs** |
| C2 | 优先级 / 排序（C7） | 队列现为 FIFO + waiter；加显式 `Order` 并让 `AcquireSlotAsync` 按序取；行内上移 / 下移 |
| C3 | 传输历史（C8） | 新增 `CloudTransferHistoryRepository`（照 `RunHistoryRepository` 的 **append-only** 模式）；`ClearFinished` 之前先归档 |
| C4 | 失败诊断（C9） | 消费批次 0 的 `CloudTransferRecord.ErrorDetail`；传输窗加「复制详情」「查看日志」 |
| C5 | 计划传输（C10） | 设置「仅在此时间段传输」；实现为**队列在窗口外暂停**，不引入系统级调度器 |
| C6 | 完成后动作（C11） | 队列 drained 时（复用已有的 `BatchCompleted` 事件）执行关机 / 休眠 / 退出，**需二次确认** |
| C7 | 队列右键菜单（C13） | 「打开所在文件夹」「复制链接」—— B4 的 `PublicLinkAsync` 与 `CopyLink` 接缝已落，直接复用，**不要**在 C 组再摸一次系统剪贴板 |

### D1–D6. 挂载

**先记住这串键名 —— 拼错会被静默忽略，这是 D 组唯一的防线：**

- `mountOpt`：`volumeName`、`networkMode`、`allowNonEmpty`、`allowOther`、`defaultPermissions`
- `vfsOpt`：`diskSpaceTotalSize`、`readOnly`、`cacheMode`、`cacheMaxSize`、`cacheMaxAge`、`cacheMinFreeSpace`、
  `cachePollInterval`、`writeBack`、`dirCacheTime`、`pollInterval`、`noChecksum`、`noModTime`、`noSeek`、`umask`
- ⚠️ **`readOnly` 在 `vfsOpt` 而不是 `mountOpt`**；`--volname` / `--network-mode` 在 `mountOpt`。

| # | 内容 | 关键改动 |
| --- | --- | --- |
| D1 | 高级选项（D1/D5） | `MountOptions`（`src/FengSync.Core/Mount/MountOptions.cs`，现为 static class + const）保留 `CacheRoot`/`CacheDirectoryFor` 静态 helper，新增**可序列化**的 `MountOptionsSettings` record；`CreateVfsOptions(settings)` + 新增 `CreateMountOptions(settings)`；`RcloneMountService` 里 `mountOpt = new { }` 改为透传。`MountPickerDialog` 加「高级选项」折叠区 |
| D1b | **`MountTarget` 不要改** | 不改 record（避免波及 `MountPickerDialog`、`RcloneMountServiceTests`、`MountPointInspectorTests`、`CloudEndpointEditTests` 四个测试文件），新增 `MountRequest(MountTarget Target, MountOptionsSettings? Options = null)` + `MountAsync(MountRequest)` 新重载，旧 `MountAsync(MountTarget)` 转发 |
| D2 | 开机自动挂载（D2） | 新建 `AppDataPaths.Root/mounts.json` + `MountRecordStore`（`RemoteName/Provider/MountPoint/Kind/RootPath/Options/AutoMount`，**不存凭据**）。恢复策略：**默认只提示「上次有 N 个挂载未恢复」+ 一键恢复**，每条记录 `AutoMount` **默认关**。钩子放 `App.OnStartup` 与 DriveHub 入口 —— **两个 exe 各自独立 daemon**，都要能恢复 |
| D3 | 真实容量（D3） | 挂载时用 `operations/about(fs)` 取 `total` 写 `vfsOpt.diskSpaceTotalSize`（int64 字节）。局限要写进 UI：**挂载时固定，配额变化后不更新**；`about` 返回空（S3/SFTP 常见）时**不传该键，绝不写 0** |
| D4 | 自动重连（D4） | 挂载点失效检测（`ScanAsync` 已有扫描）→ **提示重挂，不做静默后台重连** |
| D5 | 缓存管理（D6） | 显示 `MountOptions.CacheRoot` 占用并提供清理；`CacheMaxSizeBytes = 10GB` 改为可配 |
| D6 | UNC 挂载（D7） | **先实证** rclone `mountPoint` 是否接受 `\\host\share`；不可行就明确标注不支持 |

D 组的测试要点：`MountOptionsSettings` → RC 载荷的**键名断言**（防「拼错静默失效」的唯一手段）；
`MountRecordStore` 往返 + 不含敏感字段；恢复流程对「WinFsp 未装」「网络不通」的分支。

### E1–E5. 联动、设置与诊断

| # | 内容 | 关键改动 |
| --- | --- | --- |
| E1 | 从端点创建同步任务（E1） | 云盘管理窗加入口，`ProfileEditorWindow`（public）预填该端点一侧后打开 |
| E2 | 一键双向同步向导（E2） | 向导生成 `SyncProfile`，模式默认双向；复用 `RemoteEndpointPickerWindow` |
| E3 | 诊断导出（F5） | 导出应用日志 + 设置（**脱敏**）+ 环境信息（WinFsp、rclone 版本）；**不导出 `rclone.conf`** |
| E4 | i18n（F6） | 全中文硬编码 → 先抽资源键（`Strings.resx` 或 XAML `x:Static`），**只做 zh-CN 一份，不做语言切换 UI** |
| E5 | 首启引导（F7） | 首次进入云盘管理时引导连接 Google Drive |

### 明确降级 / 不做（避免假装完成）

| 项 | 结论 |
| --- | --- |
| E3 按需文件（Files On-Demand） | **不做**。真正的按需文件需要文件系统驱动 + 占位符语义，`rclone mount` 无法表达。替代：提供「不缓存、直读」挂载模式（`vfsOpt.cacheMode = "off"`），在 UI 说明代价（每次读都走网络） |
| B11 版本历史（B21） | **降级**：先实证 rclone 是否暴露版本列表；不可得则只给网页端入口并说明原因 |
| B7 缩略图（B16） | **限范围（已照此落地）**：仅 WPF 内建能解码的图片格式、按需拉到 `AppDataPaths.CloudThumbnails`；**不做视频缩略图**（要抽帧就得引 ffmpeg 或 `--rc-serve` 之外的一条进程，而缩略图只是装饰）；WebP/AVIF/HEIC/SVG/RAW 也不做（`PresentationCore` 没有它们的解码器，`net10.0-windows` 又拿不到 WinRT imaging），这些行**保留文件图标并说明是哪一种** |
| B7 内置预览（B17） | **不做 PDF / office / 音视频**：`net10.0-windows` 的 WPF 没有任何 PDF/DOCX 渲染器，`Windows.Data.Pdf` 是 WinRT 且不在该 TFM 的引用面上，所以照旧「下载后用外部程序打开」，预览面板只说这件事而**不假装**画了半页。文本只接 **UTF-8 / UTF-16（带 BOM）**：非 UTF-8 的字节**直接拒绝并说明不猜代码页**（要读 GBK 得加 `System.Text.Encoding.CodePages` 这个包，本批没引，因此不许出现「看起来像文件损坏的问号」） |

### 已选定的设计决策（可推翻，推翻请说明）

1. 跨端点复制**进传输队列**（而非轻量直连路径）—— 跨端点复制真实走网络字节流，可能很久，用户需要进度/取消/重试/历史；同端点复制也以队列任务呈现，保证「所有云盘操作都能在传输窗看到」的一致性。
2. D2 恢复**默认只提示 + 一键恢复**，`AutoMount` 默认关 —— 自动挂载拖慢启动、WinFsp 可能未装、网络可能不通，静默失败更难排查。
3. A6 首批 Provider 选 **OneDrive / Dropbox / WebDAV / Box** —— 覆盖主流；S3 兼容的 COS/OSS/B2 复用现有 S3 路径，只加 descriptor 行。
4. A3 重命名遇**活跃挂载则阻断**，提示先卸载 —— 挂载的 fs 串是 `old:`，改名后挂载点静默失效，自动重挂风险高于收益。
5. **先做 Provider descriptor 重构再加 Provider** —— 12+ 处硬编码使出错面随 Provider 数量线性增长。
6. `MountTarget` record **不改**，新增 `MountRequest` 包裹 —— 避免波及 4 个既有测试文件。

### 风险与验证清单

| 风险 | 怎么验证它真的对 |
| --- | --- |
| `SettingsWindow` 的 AutomationId switch 漏注册 → 静默不保存 | 每个新设置项都跑 UI 场景：改值 → OK → 重开断言已持久化；对照五处清单（见 §7） |
| 新设置字段漏加 `ConfigurationValidator` 规则 → 不被夹取 | 加规则 + 单测：越界值应被拒绝 |
| 新增 `<Button>` 撞样式白名单 | 跑 `UiConsistencyGuardrailTests`；需要新样式就先加进 `DesignTokens.xaml` 并同步白名单 |
| **挂载选项键名拼错被 rclone 静默忽略** | 唯一手段：断言发往 `mount/mount` 的 JSON 键名；关键项（`readOnly`/`diskSpaceTotalSize`）加「负值应报错」探针 |
| ✅ **已核（B0）**：服务端复制无 staging，失败留半成品 | 实证过（见 §4：`operations/copyfile` 中途只有 rclone 自己的 `.partial`，`job/stop` 后目标目录为空，成功才有目标名）；`ServerSideCopyTests` 用真 rclone 钉住「失败 / 源不存在时目标为空」，`CloudTransferCopyTests` 用假 RC 钉住「失败时没有 publish、没有 deletefile、载荷里不出现 staging 名」。残余：daemon 被硬杀会留下 rclone 自己的 `.partial`，Feng Sync 清不掉 —— B1 的复制 UI 不要把「目标目录有个不认识的 .partial」当成本程序的 bug |
| 端点重命名迁移中途失败 | 单测覆盖每步注入失败，断言 conf 与两个 JSON 回到原状；S3 场景断言 `<old>_s3` 不残留 |
| 跨端点 move 的 delete 失败 → 重复文件 | ✅ **已覆盖（B1）**：`A_cut_whose_delete_was_refused_reports_the_duplicate_instead_of_a_success` 用假 RC 拒绝 `operations/deletefile`，断言状态行是「已复制但源未删除」+ 原因，而不是「已剪切」 |
| 键盘快捷键劫持地址栏/搜索框输入 | ✅ **已核（B6，进程内）**：`CloudBrowserShortcutTests` 用真实路由事件在 `AddressBox`/`EndpointList` 上 raise F2/Del，断言窗口命令一条都不动；§4 的 WPF 表里记着实测依据（`TextBox` 自己吃掉 Del 与 Ctrl+A、但不吃 F2，`RaiseEvent` 的 `OriginalSource` 就是被 raise 的元素）。**残余**：带 Ctrl/Shift 的组合键只能把修饰键**当参数传进** `HandleKeyAsync`（`Keyboard.Modifiers` 读物理键盘，合成事件改不了），所以「XAML 处理器读修饰键读对了」这一环只有代码在担保，桌面场景也因此没加键盘步骤 |

---

## 6. 实现前必须先实证的点（不准猜，先验证再写）

1. ✅ **已实证（B5，2026-10-04，真 rclone + `alias` remote，`CloudRecycleBinFactTests` 10 条）**：问题「`use_trash=false`
   的 alias 方案在 v1.75 的 RC 路径下是否可用」**问错了方向** —— RC **根本没有** `operations/trash`（404
   `couldn't find method`），所以不存在「让 rclone 把删除变成回收」这条可配置的路；`--drive-use-trash` 是 backend 的
   **配置项**，既不是 RC 动词，也救不了 S3/SFTP。回收站因此只能是**应用自己的文件夹**。同批钉住的其余事实全部进了
   §4 的 rclone 表：movefile 建父目录 / **覆盖已存在的目标名** / 落到目录被拒且源不动；`sync/move` 落到已存在目录
   **合并并留下空壳**、落到新路径则源消失；rmdir 打不存在的路径答 **500**（被分类器读成 `Temporary`），而
   `operations/stat` 对同一路径答 `{"item": null}`；mkdir **幂等**、建父目录、**不许**顶掉同名文件；
   移走一个文件不会带走它的目录。实现与测试见 §2 的 B5。
2. ✅ **已实证（A5，2026-10-03）**：RC **没有**全局 proxy 透传；rclone 的 HTTP 客户端**只读 `HTTP_PROXY`/`HTTPS_PROXY`，
   **不读 `ALL_PROXY`**（死端口法验证：`ALL_PROXY` 那轮仍到达目标，`HTTP_PROXY`/`HTTPS_PROXY` 那轮 `proxyconnect tcp` 失败）。
   代理只能靠**子进程环境变量**传；`RcloneProxyResolver` 已把只有 `all_proxy` 的值镜像进 http/https。实现见 §2 的 A5。
3. ✅ **已实证（B4，2026-10-04，bundled 二进制自起 loopback `rclone serve s3` + `RclonePublicLinkFactTests` 4 条）**：
   `operations/publiclink {fs, remote}` **存在**，支持的 backend 答 `{"url": "<link>"}`；**没有这功能的 backend**
   （local / alias）答 HTTP 500 `"… doesn't support public links"`；**目录（S3 前缀）与已消失的对象都答 404
   `object not found`**。多写的字段**静默忽略**（这几条是同一套 loopback 上**手工 curl** 探的，没进测试）：`expire:"1h"` 生效（签名里 `X-Amz-Expires=3600`），超上限时被 clamp 到
   604800（rclone NOTICE `Public Link: Reducing expiry to 1w …`），`unlink:true` 被 S3 **忽略**。
   `operations/fsinfo {fs}` 的 `Features.PublicLink` 能回答「这个 backend 有没有这功能」（local `false` / s3 `true`），
   但 B4 **故意不用**——为一个开关多花一次往返，而失败话术本来就得处理。
   ⚠️ 那条 500 会被共用的 `RcloneFailureClassifier` 读成 `Temporary`，所以**先读文字再看 category**。
   **各 Provider 的支持矩阵仍未测**（只测了 s3 与 local/alias）：这不是缺口而是设计——B4 不预判，直接问、按答话分类，
   所以接一个新 backend 不需要补矩阵。结论已进 `AGENTS.md` 与上面 §4 的 rclone 表。
   手法备忘：`rclone serve s3 <dir> --addr 127.0.0.1:<port> --auth-key AK,SK`（**不是** `--access-key-id`，bucket 就是子目录），
   RC 路径**没有** `/rc/` 前缀（上次把每个方法都写成 `/rc/<method>`，于是**连 `operations/stat` 都 404**，一度误判成方法不存在）。
4. rclone `mountPoint` 是否接受 UNC 路径（**D6**）。
5. rclone 是否暴露版本列表（**B11**）。
6. `core/bwlimit` 的作用域是全局还是 per-fs（**C1**）。

7. ✅ **已实证（B0，2026-10-04）**：跨端点 `operations/copyfile` **不会**留下可续传的中途状态 —— 作业运行中目标只有
   rclone 自己的 `<name>.<id>.partial`，目标名要等作业成功才出现；`job/stop` 之后目标目录为空
   （rclone 日志 `Removing partially written file on error`）；对已存在的目标名是直接**覆盖**（重试前不需要清理）。
   结论已写进 `AGENTS.md` 的不变式列表，实现与测试见 §2 的 B0。**残余**：daemon 被硬杀时 rclone 的 `.partial` 会留下，
   Feng Sync 叫不出它的名字，也就清不掉。
   探测手法照 A2：起 `rclone rcd --rc-addr=127.0.0.1:<port> --rc-user=<u> --rc-pass=<p> --rc-serve`（**不是** `--rcaddr`），
   就绪探测调 `core/version`（**没有** `rc/version` 这个方法），curl 必须带 `Content-Type: application/json`。

8. ✅ **已实证（B1，2026-10-04，真 rclone + 两个 alias remote，`ServerSideCopyTests`）**：**同一个 fs 两端**的
   `operations/copyfile` 把目标名落在源的**旁边**并且**保留源**（hash 相同）；**同一个 fs 两端**的 `operations/movefile`
   让对象出现在目标处、**源目录里不再存在**。两条一起决定了 B1 的呈现：同端点内的 复制 不需要第二套机制，
   同端点的 剪切 用 move 而不是「复制 + 删源」，因此它不留重复、也不需要删除确认。结论已写进 `AGENTS.md` 的
   粘贴不变式和上面 §4 的 rclone 表。

9. ✅ **已实证（B3，2026-10-04，真 rclone + `alias` remote，`RcloneStatFactTests` 7 条 + 一次 curl 直打 daemon）**：
   `operations/stat` 存在且**对象不存在时答 `{"item": null}`**（不是错误）；每个对象都带 `MimeType`（目录是
   `inode/directory`）；**校验值只有把名字列进 `opt.hashTypes` 才给**，而位域 `opt.hashes` 与 `opt.HashTypes`
   分别是**静默忽略**和 `ListJSONOpt.hashTypes of type []string`；未知名字报 `unknown hash type "bogus"`；
   目录被点名也不带 `Hashes`。256 MiB 本地对象计时：不问 0.15 s / 问 md5 0.49 s / 问三个 0.86 s。
   顺带钉了 `operations/hashsum` 的**形状**（单个 `hashType`，答 `"<hex>␣␣path"` 行）与 `operations/hash`、
   `operations/checksum`、`operations/get`、`operations/geturl` 的**不存在**（404）。结论已进 `AGENTS.md` 与上面 §4。
   那次 curl 还顺手证实了 `operations/publiclink` **是**一个已注册方法（本地 backend 答 500
   `doesn't support public links`）—— 它的**完整**实证在第 3 条，由 B4 用 loopback S3 补上了 `{"url"}` 线形与 404 形态。
10. **B9（文件夹大小）动手前必须证的三件事**（第 9 条那套手法：本机 `rclone rcd` + curl 带 `Content-Type: application/json`，结论进 `AGENTS.md`）：
   ① RC **到底有没有** `operations/size` —— B3 那次把 `operations/hash`、`checksum`、`get`、`geturl` 都探成 404，
   而那份**否定清单里没有 size**，所以它可能存在但从没被问过；② 若存在，它对慢后端的**代价与可否取消**
   （一个十万对象的目录会让这一次点多久？取消后留不留半成品）；③ 若只能自己递归 `operations/list`，那次统计
   **算不算一次访问** —— B8 钉住的规则是「列成功才记访问」，而统计打的正是同一批 list 调用，别让一个用户没打算去的
   子目录顺手挤进「最近访问」把真正想去的那条顶出上限。

实证的方法照 A2 的做法：直接 curl 打本机 `rclone rcd` 的 RC（记得带 `Content-Type: application/json`），
把结论写进 `AGENTS.md` 的不变式列表，而不是留在会话里。

---

## 7. 项目约定与陷阱（每个子批次都要遵守）

### 门禁与提交

- 每个子批次结束后跑 `pwsh -NoProfile -File .\scripts\Test-All.ps1 -Level UiOffline`
  （约 2m30s，**工具超时给 600000ms**）；必须看到 `Passed!  - Failed: 0` 汇总行。
- 提交粒度按层次（`feat` / `test` / `docs`），**用显式路径 `git add`**，每个子批次 1–2 个 commit，**只提交不 push**。
- `AGENTS.md` / `README.md` 随行为变化同步更新。

### UI 护栏（`UiConsistencyGuardrailTests`）

- 每个 `<Button>` 必须显式使用白名单样式：`Primary/Secondary/Tertiary/Danger/Icon/DangerIcon/EndpointSwap/ChangeSummaryFilter`。
- 页面 XAML（`src/FengSync/**/*.xaml`）**不得覆盖** `Height/MinHeight/Padding/FontSize/Background/Foreground/BorderBrush`；
  内联 hex 只能出现在 `Themes/DesignTokens.xaml`；页面禁用 `Button.Template`。
- 动态文本 `TextBlock` 用 `AutomationId`，**不要**静态 `AutomationProperties.Name`。
- **代码里现搭的对话框**（`TextPrompt`、`CloudConflictPrompt`、`CloudDetailsPrompt`）写在 `.cs` 里，页面 XAML 的扫描看不到它们，
  所以那三条得作者自己守住，而这三处确实守着：样式一律经 `TextPrompt.Resource<T>` 从合并字典取
  （`Primary/Secondary/Tertiary/DangerButtonStyle`、`CaptionTextStyle`，内联 hex 为零）、可操作的控件
  （按钮、复选框、输入框）都有 `AutomationId`、**Enter 落在不改变任何东西的那颗按钮上**
  （冲突框的 `IsDefault` 是「跳过」，destructive 确认框的 `IsDefault` 是「取消」）。
  B4 没有新增代码搭弹窗：分享确认复用 `TextPrompt.Confirm(..., danger: true)`，所以 Enter 已经落在「取消」上。

### 设置项三件套（漏一件就静默失效）

新增设置字段必须**同时**改：

1. `ApplicationSettings`（`src/FengSync.Core/Configuration/`）
2. `ConfigurationValidator`（否则值**不被夹取**）
3. `SettingsWindow` 的 **五处**：`SettingsWindow.xaml`、`EditablePages`、`NavigateTo`、
   `BuildSettings`、`ApplyTextValue` 的 AutomationId switch（漏注册 → **静默不保存**）。

**例外（B7 起，形状要说清）**：由**拥有它的那个窗口自己保存**的状态不进第 3 处 —— `MainWindowSidebarWidth`（主窗口）
与 `CloudBrowserView`（云盘管理窗口的列表/网格开关）都是这一类：第 1、2 处照改，保存方是**该窗口**，且必须
**读-改-写**当前文件（`SettingsStore.LoadAsync` → `with { … }` → `SaveAsync`），否则会把别的 shell 在此期间改过的字段
一起写回默认值（`A_view_choice_is_remembered_without_overwriting_anybody_else_s_choice` 预写 `MainWindowSidebarWidth = 300`
来钉这条）。枚举型设置还要在**写回前**归一化（`Enum.IsDefined` 否则回落默认值），因为 `SaveAsync` 会跑
`ConfigurationValidator`，一个从文件里读出的越界值不该让这次保存整体失败。

### 其他铁律

- **破坏性操作**必须走 `CloudFileOperationSafety` 校验 + 二次确认，并保留批量阈值（10）。
- **不要在云盘路径上重写同步引擎**：上传/下载继续走 `CloudTransferQueue`（它复用同步引擎的 staging/resume/journal）；
  **远端→远端复制也进同一个队列**（B0），走 `EndpointTransfer.CopyServerSideAsync`，不要在 UI 里直接拼
  `RcloneRcClient.CopyFileAsync`；删除/移动仍通过 `IEndpoint` / `RcloneRcClient` 调用，不自己拼 rclone 命令行。
  B1 照的就是这条：**粘贴的跨端点复制一律 `EnqueueCopy`**，唯一允许直接调 move 的是**同端点内的剪切**（字节不动，没有可续传/进度可言）。
- **改并发数要同时看续传**：`ActiveCount` 与 `MaxConcurrentTransfers` 在 `CloudTransferQueue.RunAsync` 里是
  **当作进度参数**传给 `EndpointTransfer.CopyToStagingAsync` 的，所以调并发会影响续传判定；
  并发数从 `const` 改为可配时还要保证 `SemaphoreSlim` 重建不破坏在途任务。
- **凭据只留在 `rclone.conf`**：任何导出/持久化都不得写入 secret/token。
- **会对外交出访问权的动作**（B4 的分享链接是唯一一个）：云端被问**之前**先用用户的话说明白「谁从此能读什么」，
  措辞**不得承诺**本应用保证不了的东西（有效期、能否撤回 —— 那是 backend 的决定）；系统级状态（剪贴板）一律走接缝，
  「事情办了但没落地」必须是**独立一态**，不许复用「没办成」那句话。
- **`EndpointType` 枚举值只能追加**（`BaselineTransaction` 与 `TransferResumeStateStore` 持久化 `Type.ToString()`）。
- 新增 UI 场景要动**两处**：`tests/FengSync.UiTests/UiAcceptanceTests.cs` + `Scripts/Invoke-UiScenario.ps1`
  （`ValidateSet` 参数 + `switch` 分支）。
- 测试写法照抄既有模式：in-process 行为测试进 `tests/FengSync.UiTests`（照 `CloudFeatureBehaviorTests` 的
  `Add(queue, ...)` 直接操作集合，**避免启动真实 rclone**）；需要真 rclone 的进 `tests/FengSync.Tests`。

---

## 8. 关键文件索引

| 文件 | 作用 |
| --- | --- |
| `src/FengSync.Core/EndpointSchemes.cs` | **唯一的** scheme ↔ rclone backend ↔ `EndpointType` 映射表（含 `RenameRemote` / `RemoteNameOf` / `BuildUri`） |
| `src/FengSync/Services/CloudProviderCatalog.cs` | UI 侧 Provider 描述符（7 条）；加 Provider = 加一行 + 字段 |
| `src/FengSync.Core/RcloneConfigFile.cs` | 纯 ini 段手术（重命名端点的地基） |
| `src/FengSync/Services/CloudEndpointRename.cs` | 端点重命名：校验 → 备份 → 改写 → 回读验证 → 迁移 → 失败一起还原 |
| `src/FengSync/Services/CloudEndpointList.cs` | 端点列表的搜索与排序纯函数（批次 A3） |
| `src/FengSync/Services/CloudEndpointOrganization.cs` | 端点分组/标签的纯函数（批次 A6）；`Merge` / `Preserve` 是「保存归档绝不静默失败」的两处关键 |
| `src/FengSync/Services/CloudEndpointPortability.cs` | 端点导出/导入的纯逻辑与不可信输入校验（批次 A7）；导出结构里没有放凭据的字段 |
| `src/FengSync/Views/CloudEndpointOrganizationWindow.xaml(.cs)` | 编辑单个端点的分组/标签（批次 A6） |
| `src/FengSync.Core/Configuration/RcloneProxySettings.cs` | 把 `ApplicationSettings.RcloneProxy` 转成 `RcloneProxyOptions`（批次 A5） |
| `src/FengSync.Core/Rclone/Lifecycle/RcloneLifecycleHost.cs` | `ProxyOptionsProvider`：启动锁内、子进程创建前只读一次代理（批次 A5） |
| `src/FengSync/Services/CloudEndpointStatusService.cs` | 端点体检：`ProbeAsync` / `Classify` / `RunBoundedAsync` / `FormatBytes` |
| `src/FengSync/Services/CloudEndpointService.cs` | 端点 CRUD 与 URI 构造的门面（转发到 catalog + schemes） |
| `src/FengSync/Views/RemoteEndpointManagerWindow.xaml(.cs)` | **三合一**窗口：端点管理 + 挂载管理 + 文件浏览器。凡是「要问用户」或「碰系统状态」的决定都在这里以 internal 接缝露出：`AskConflict`（B2）、`DetailsAsync` / `BuildRowMenu`（B3）、`ConfirmShare` / `CopyLink` / `ShareLinkAsync` / `ShareAsync`（B4）、`ConfirmRecycle` / `ConfirmDelete` / `RecycleAsync` / `RestoreAsync` / `EmptyTrashAsync` / `OpenTrashAsync`（B5 —— 后者是「清空回收站问的那句话」，所以默认路径仍是真 `TextPrompt.Confirm`）、`PromptRename` / `HandleKeyAsync`（B6 —— 前者是重命名的问题，后者是键盘的入口：把修饰键与「正在输入」当参数接进来，测试因此不必伪造物理键盘也能走真表真派发） |
| `src/FengSync/Views/CloudEndpointEditorWindow.xaml(.cs)` | 新建/编辑端点表单（按 descriptor 生成，非 S3 部分无手写面板） |
| `src/FengSync.Core/Execution/EndpointTransfer.cs` | **唯一的** staging-aware 传输原语（同步引擎与云盘浏览器共用）。`CopyToStagingAsync`+`PublishAsync` 是可续传的上传/下载腿；`CopyServerSideAsync` 是两端都远端、**不 staging 不 publish** 的复制腿（B0） |
| `src/FengSync/Services/CloudTransferQueue.cs` | 云盘传输队列（staging / resume / journal / 并发 / 重试 / **远端→远端复制 `EnqueueCopy`**）；构造参数里的可选 RC 客户端工厂是「不起 daemon 也能断言载荷」的接缝 |
| `src/FengSync.Core/Endpoints.cs` | `RcloneRcClient`（RC 调用的唯一出口，路径**没有** `/rc/` 前缀）：B3 加的 `StatAsync(fs, remote, hashTypes)` 与 B4 加的 `PublicLinkAsync(fs, remote)`（只认应答里的 `url`，缺了给 `null` 而不是编一个空链接） |
| `src/FengSync/Services/CloudRecycleBin.cs` | 回收站的**全部规则**（B5）：`FolderName` / `RootOf` / `IsRoot` / `IsInside` / `IsTrashView` / `LandingDirectory` / `OriginalPath` / `OriginalDirectory` / `PlacementsFor`（撞名让位，文件与目录都算进「已被占用」）/ `SelectForMove` / `SelectForRestore` / `ConfirmFor` / `Explain`；`CloudRecycleOutcome` 七态 + `CloudRecyclePlacement` + `CloudRecycleResult`。类文档写了「为什么只能是应用自己的文件夹」和四条实测隐患 |
| `src/FengSync/Services/CloudFileManagerService.cs` | 文件浏览器服务层（列表/搜索/重命名/删除/**同端点移动 `MoveAsync`**/新建目录/**单对象 `StatAsync` → `CloudItemStat`**/**`PublicLinkAsync`**/**`MoveIntoAsync(path, path, isDirectory)` —— B5 的两条腿：文件 `movefile`、文件夹 `sync/move` + 只清还留着的壳（`RemoveIfEmptyAsync` 先 `stat`）/ `NamesAsync` / `ListOrEmptyAsync`**）；`RelativeToEndpoint` 是「剥的是**自己**端点的根、只剥一次」的唯一体（B1）；`SizeDisplay` 到这一步才格式化成 `CloudItemDetails.HumanSize`，列与属性面板因此同一套单位（B3） |
| `src/FengSync/Services/CloudBrowserClipboard.cs` | 粘贴规则的纯函数（B1）：`CloudClipboardMode` / `CloudClipboardItem`（行 + 它的来源端点）/ `CloudPasteTarget` / `CloudPastePlan` / `CloudPasteOffer(Plan, TakenNames)`（B2：目标目录**读一次**同时供判冲突与改名）/ `CloudPastePlanner.Build`。不碰 WPF 也不碰 rclone，所以落点与 skip 原因能直接测 |
| `src/FengSync/Services/CloudConflictResolution.cs` | 同名冲突唯一的答案处（B2）：`CloudConflictChoice` / `CloudConflictAnswer(Choice, ApplyToAll)` / `CloudConflictPlacement(Original, Destination, Choice)` / `ChoiceFor`（设置 → 整批答案，`Ask` 给 null）/ `AskFor`（逐条问 + 应用到全部）/ `Apply`（**按输入顺序一条一答**，改名相对各自目录的空闲名） |
| `src/FengSync/Views/CloudConflictPrompt.cs` | 冲突问题本身（B2）：取消 / **跳过（默认按钮）** / 保留两者（预览名）/ 覆盖 + 「应用到其余 N 个」。测试用窗口的 `AskConflict` 接缝代替它 |
| `src/FengSync/Services/CloudItemDetails.cs` | 属性/详情的**规则**（B3）：`CloudDetailRow` / `CloudDetailsPanel(Rows, CanComputeHash)` / `CloudItemStat`（`Missing` 是「云端已没有」这个值，不是异常）/ `For` / `ForSelection` / `OffersHash` / `HumanSize` / `HashPreference`（md5 → sha1 → sha256，与同步引擎同一条链）。措辞、缺项怎么说、文件夹大小不许谎报都在这一层，所以不开窗也能测 |
| `src/FengSync/Views/CloudDetailsPrompt.cs` | 属性面板本身（B3，代码搭，同 `TextPrompt` 一路）：只画 `CloudItemDetails` 决定的行，「计算校验值」按钮只在答案还可能改变时出现。它 `ShowDialog`，所以窗口测试走 `DetailsAsync` 接缝而不是真开它 |
| `src/FengSync/Services/CloudShareLink.cs` | 分享链接的**规则**（B4）：`CloudShareLinkOutcome` 七态 / `CloudShareLinkResult(Outcome, Url, Detail)` / `OffersLink`（只有文件）/ `ConfirmationFor`（那句话本体）/ `Explain`（一态一句、七句互不相同）/ `FromAnswer`（没有链接就是失败，不是空成功）/ `FromFailure`（**先读文字再看 category**）/ `IsUnsupported`。窗口的接缝是 `ConfirmShare`、`CopyLink`、`ShareLinkAsync`、`ShareAsync` |
| `src/FengSync/Services/CloudBrowserShortcuts.cs` | 浏览器键盘的**唯一一张表**（B6）：`CloudBrowserShortcut` 八态（`Refresh / Recycle / DeleteForever / Rename / SelectAll / Copy / Cut / Paste`）+ `Resolve(key, ctrl, shift, typing)`。**只有映射，没有动作**，所以「一键一命令」「Del 的三种拼法」「文本框只放行 F5」在不开窗口的前提下就可断言 |
| `src/FengSync.Core/Mount/MountOptions.cs` | 挂载参数（D 组的改造对象） |
| `src/FengSync.Core/Rclone/Transport/RcloneProxyConfiguration.cs` | 代理（A5 的现成机制） |
| `src/FengSync/Services/AppServices.cs` | `IAppServiceHost` / `AppServices.Install`（**两个 exe 共用服务宿主**） |
| `src/FengSync.DriveHub/` | 第二个入口 `DriveHub.exe`（独立 daemon） |
| `tests/FengSync.Tests/ServerSideCopyTests.cs` | 真 rclone（两个 alias remote）钉住跨端点复制的**落点**：目标名精确、源不消失、重试覆盖、失败不留对象、本地端点被拒（B0） |
| `tests/FengSync.UiTests/CloudTransferCopyTests.cs` | 假 RC 钉住复制的**载荷**：`srcFs/srcRemote/dstFs/dstRemote`+`_group`、`core/stats` 进度、取消发 `job/stop`、不许出现 movefile/deletefile/staging 名、journal 往返与旧文件兼容（B0） |
| `tests/FengSync.Tests/RcloneStatFactTests.cs` | 真 rclone v1.75.0（alias remote + 真 daemon）钉住 B3 的全部前提：`operations/stat` 的存在与 `{"item": null}` 线形、`MimeType`（目录 `inode/directory`）、**不问就没有校验值**、点名 `md5` 后答案等于本地算的 MD5、目录被点名也不给、`["bogus"]` 报 `unknown hash type`。这里钉的是「rclone 到底怎么答」，窗口的假 RC 只钉「我们到底怎么问」 |
| `tests/FengSync.UiTests/CloudBrowserDetailsTests.cs` | 真窗口 + 假 RC 钉住 B3 的浏览器侧决定：读的是 stat 而不是列表快照、打开面板**一个校验值都不点**（假 RC 只在被点名时返回哈希，所以「没问」是可断言的）、按钮才点名那三种、此后端没有校验值时问了也还说没有、只有后端知道的那一种类型也要显示、对象已消失时不报大小也不给按钮、被拒绝时仍显示列表并说谁拒的、文件夹永不获得按钮、所在端点与 rclone 地址、单行叫「属性」多行叫「属性（N 项）」、只读项排在行菜单最后且隔一条分隔线、空选择不该有行菜单。窗口接缝是 `DetailsAsync(entry, withHash)` 与 `BuildRowMenu()`；`FakeRclone` 用字段列表拼 JSON 并过一遍 `JsonDocument.Parse`，免得畸形应答被读成「后端拒绝」 |
| `tests/FengSync.Tests/RclonePublicLinkFactTests.cs` | 真 rclone v1.75.0 钉住 B4 的全部前提，**且不碰任何真云、不用任何凭据**：用 bundled 二进制起一个 loopback `rclone serve s3`（`--auth-key AK,SK`，bucket 是子目录）+ 一个指回它的 `s3` 段，于是 `{"url"}` 线形是 rclone 亲手答的（断言它是绝对 http Uri、路径含 `bucket/note.txt`）；local/alias 的 500 措辞含 `support public links`；目录 404、已消失对象 404（`Category == NotFound`）。端口用 `TcpListener` 现占现放，收尾杀进程树并删临时目录 |
| `tests/FengSync.UiTests/CloudBrowserShareTests.cs` | 真窗口 + 假 RC 钉住 B4 的浏览器侧决定：`复制分享链接…` 只在一个文件上出现（文件夹与多选没有）、载荷是 `Fs == "nas:"` 与 `Remote == "team/a.txt"`（假 RC 只记下这两个字段）、**用户没答应之前一次 RC 都不发**（`Assert.Empty(standIn.Shares)`）、复制到剪贴板的是应答里那个字符串本身、后端拒了什么都不复制且把那句 500 说成「不支持」、对象已消失说「请刷新目录」、应答没有链接时不复制、文件夹本地就答（不问后端）、状态行同时说「已复制到剪贴板」和「无需登录」、剪贴板抛异常时说的是「云端已经生成链接」而不是「没有取到」。接缝是 `ConfirmShare` / `CopyLink` / `ShareLinkAsync` / `ShareAsync`。⚠️ 这批**曾经**是十条 `public void` + 不 `await`，所以一条都没真正断言过（B5 查实并修好，见 §4 的 WPF 表与 `UiTestAwaitGuardTests`） |
| `tests/FengSync.Tests/UiTestAwaitGuardTests.cs` | 扫 `tests/FengSync.UiTests/*.cs`：凡是调用**返回 `Task`** 的窗口 harness（`RunSta` / `WithWindow`）而语句前缀里没有 `await`/`return`/`=>` 就失败；第二条自证「确实扫到了那四个用异步 harness 的窗口套件」，免得这个守卫某天扫了个空还一直报绿 |
| `tests/FengSync.Tests/CloudRecycleBinFactTests.cs` | 真 rclone v1.75.0（一条 `alias` remote，全离线、无凭据）钉住 B5 的**全部**前提：RC **没有** `operations/trash`（404 `couldn't find method`）；「目录不存在」是 404 而「空目录」是 `{"list":[]}`；movefile 建父目录、**覆盖**已存在的目标名、落到目录被拒且源不动；`sync/move` 落到已存在路径**合并**（进来的赢、目标独有的留下）并**留一个空壳**，落到新路径则源消失；rmdir 清壳一次成功、第二次答 **500**（分类成 `Temporary`）而 `stat` 答 `null`；mkdir 幂等 + 建父目录 + **拒绝**顶掉同名文件；文件在回收站与原位之间往返，只有镜像里的空目录留下 |
| `tests/FengSync.UiTests/CloudBrowserRecycleTests.cs` | 真窗口 + **stateful** 假 RC 钉住 B5 的浏览器侧决定：四个按钮的 AutomationId 与 还原/清空 的可见性、`ConfirmRecycle` 没答应之前**什么都不发**、文件只发一条 `movefile`（不发 deletefile/mkdir）、文件夹发 `sync/move` 且**只在源还留着时**才 rmdir 壳、落点被占就改存 `a (1).txt` 并说出来、还原回到原目录并在被占时让位、没进过回收站说 `NotInTrash`、已在里面说 `InsideTrash`、回收站文件夹本身不许移进去、半批失败仍算 Moved 但报「1 项没有成功」、行菜单按选择给不同命令、「清空回收站」3 项=**一个**问题（含「已经在 .fengsync-trash 里」）而 11 项=**两个**、从没用过的回收站说「是空的」且不删任何东西、「回收站」按钮先 mkdir 再进去 |
| `tests/FengSync.UiTests/CloudBrowserShortcutTests.cs` | 真窗口钉住 B6 的键盘，分三层：**3 条纯表**（一键一命令、Del 是那个有三种拼法才分出「可逆/不可逆」的键、文本框里只有刷新能走）+ **5 条真实冒泡 `KeyDown`**（面板上的 F5 重读目录、F2 只重命名选中那行、表不认的键**不认领**、地址栏保住编辑键却仍答刷新、端点列表上的键一条浏览器命令都不触发。造事件靠临时 `HwndSource` 给 `KeyEventArgs` 当 source，路由完立刻 dispose）+ **11 条命令本身**（修饰键当参数传入 `HandleKeyAsync`，走的仍是真表真派发）。`PromptRename` 接缝让重命名永不碰真模态框。⚠️ **未自动化证明**的两环（物理修饰键、桌面场景可达性）写在 §4 的 WPF 表与 §2 的 B6 小节 |
| `tests/FengSync.UiTests/CloudBrowserPasteTests.cs` | **真窗口 + 假 RC**钉住 B1 的浏览器侧决定：剪贴板带着来源端点、两侧各自剥根、文件夹与自复制的 skip、同端点剪切只发 `movefile`、跨端点剪切**复制完成后**才删源（拒绝复制 → 源保留且无删除；拒绝删除 → 报告重复而不是成功）。B2 之后**冲突不再是断言 `overwrite=false`**，而是走 `AskConflict` 这个窗口接缝（**必须置**，否则测试会弹真模态框而挂住）钉住落在改名后的远端名、改名停在屏上目录、问题看到被占的名字与剩余数量、一次回答覆盖其余、取消不排队。假 RC 的 `ListedNames`（默认恰好一个同名文件）决定「目录里已有什么」，`PlanAsync` / `Place` 是把窗口接缝接上的小工具；`RunSta` 的 pumping + 单 `Application` 所有权 + watchdog 是这类**异步窗口测试**的样板（见 §4 的 WPF 表） |
| `tests/FengSync.Tests/` | Core 层 + 需要真 rclone 的测试（门禁里的 Core 阶段） |
| `tests/FengSync.UiTests/` | in-process WPF 行为测试 + 桌面场景（`UiAcceptanceTests` + `Scripts/Invoke-UiScenario.ps1`） |
| `scripts/Test-All.ps1` | 门禁入口（`-Level Core|UiOffline|Online`，`-ShowOnScreen`） |
| `docs/handoff-cloud-drive-improvements.md` | **本文**：这条线唯一的权威文档（路线图 + 实证事实 + 约定） |
| `AGENTS.md` | 项目不变式清单（改行为前先读，改完补一条） |
