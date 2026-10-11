# 游戏模式进度

> 配合 `Docs/GameMode_Design_CN.md`（设计稿 v3）与 `Docs/GameMode_Goals_CN.md`（三段目标）使用。上下文被压缩后先读这里再继续。
> 分支：M0 在 `feature/game-mode-m0`（从 `docs/game-mode-design` 拉出）；M1 在 `feature/game-mode-m1`（M0 未合入 main，从 `feature/game-mode-m0` 拉出，并合入了 origin/main 的 PR #28 以复用工作台监听的 Error 处理）。

## M1：接入应用

### 清单

状态：⬜ 未开始 · 🔶 进行中 · ✅ 完成（证据见右列或对应小节）。

| # | 项 | 状态 | 证据 |
|---|---|---|---|
| 1 | 前置①：`GeneratedDirectories` 已在 M0 提出共用（deeacb4）——核实并复用 | ✅ | `Services/GeneratedDirectories.cs`；`PolisScanner.IsSkippedDirectory` 与目录搜索共用它 |
| 2 | 前置②：M0 的 C# 纯函数挪进 `Services/GameMode/`；`SubAgentZones` 与类别表合成一张 | ✅ | 8a9ff18；Archive.Tests「polis tool categories」逐个核对 `FunctionRegistry` 注册 |
| 3 | 前置③：工作区文件监听提成服务（`WorkspaceWatcherService`），工作台与游戏共用；Error → 整城重扫；建不起来 → 降级并提示；改写 `TestWorkspaceWatcherErrors` | ✅ | 1a5ea0a；`TestWorkspaceWatcherErrors`（第二个订阅者每批只收一次）；`TestGameModeCityLifecycle`（注入溢出 → 整城重扫） |
| 4 | 前置④：「重新定位工作区文件夹」（只改 `DirectoryPath`，`Id` 不变）：服务 + 会话树入口 + 游戏雾中入口；断言 | ✅ | 1a5ea0a；Archive「workspace relocation」；`TestGameModeCityLifecycle`（雾 → 重新定位 → 同一本账本） |
| 5 | 模式切换：`MainLayoutSettings.GameMode` 持久化；对话标题栏按钮；游戏视图替换消息列表那一行、输入框保留；进入游戏收起宠物（并停掉它的计时器） | ✅ | a05843c；`TestGameModeSwitchAndFailures` |
| 6 | 网页承载：前端挪进 `Assets/Polis/` 离线打包；`OfficePreviewHost` 新增 `/polis/` 路由（路径白名单 + CSP 响应头）；`NativeWebView` 惰性创建 | ✅ | a61347e、a05843c；真实 WKWebView 探针（「真实 WebView」） |
| 7 | 城邦：后台、有界、可取消的扫描 → 汇总 → 账本；新的切换取消旧的扫描 | ✅ | `GameModeViewModel.LoadCityAsync`（`Task.Run` + 每次换城一个新的 `CancellationTokenSource`）；Archive「polis city」 |
| 8 | 9.2 切换：会话树是唯一选择来源；同工作区 / 跨工作区（航海过场）/ 全局对话（神殿）；两拍；过场 = 加载遮罩（最短观感、上限） | ✅ | `IPolisShell`；`TestGameModeSwitchAndFailures`（游戏里切会话不建气泡树、不踢回对话）；冒烟「voyageLandsWhenReady / voyageWaitsForPartial」 |
| 9 | 存档：`AthenaData/Workspaces/<id>/game/{polis,index}.json`、`AthenaData/Game/sanctuary.json`；原子写、按记录隔离损坏、保留上一份完好副本、schema 版本与迁移 | ✅ | ba3bd85；Archive「polis save」 |
| 10 | 重启：先按快照显示，再后台扫描核对差异；未处理的成果仍带颜色；被打断的委托显示停工 | ✅ | `TestGameModeCityLifecycle`（第一座城来自快照、报告、待处理成果仍在）；页面 `focus.interrupted` → 停工脚手架 |
| 11 | 外部改动：增量更新受影响建筑、区分雅典娜与外部改动、批量合并（超阈值整城重扫）、运行时改名换牌匾 | ✅ | Archive「polis attribution」；`TestGameModeCityLifecycle`（外部新建 = external + 公告板、她写的 = athena 无公告、其他建筑不挪） |
| 12 | 离线差异报告与改名找回（文件夹按子项名集合相似度配对；藏品按大小 + 哈希找回；找不到 = 遗失，不自动删） | ✅ | Archive「polis offline diff」「polis city」；`TestGameModeCityLifecycle`（离线改名原地换牌匾、删除留空地、报告） |
| 13 | 文件夹丢失：雾 + 「重新定位」 | ✅ | `TestGameModeCityLifecycle`；冒烟「relocateSent」；截图 05-fog |
| 14 | 事件推导：从渲染模型（`Segments` / `ToolCallEntry.Status` / 审批 / 子代理）推导，不给 `IChatService` 加回调；`InvokeScript` 约 100 ms 攒批 | ✅ | Archive「polis events」；`GameModeViewModel.Flush`；真实探针 liveEvents = 4 |
| 15 | 动作与旁白：旁白搬进 locale 文件（`Polis.Line.*`，`\|` 分隔变体），中英两套 | ✅ | `PolisLocale`；`TestGameModeSwitchAndFailures` 核对中英两份都有全部 `Polis.*` / `GameMode.*` 词条 |
| 16 | 卷轴阅读器：Markdown 只建 DOM、不解释任何 HTML；页面 CSP | ✅ | Node「reader」×2 + 源码扫描（无 innerHTML 等）；冒烟「readerIsText」；截图 06-reader |
| 17 | 成果交付与收下 / 退回（退回 = 同一会话追加修改要求：预填输入框并聚焦，由用户发出） | ✅ | `TestGameModeCityLifecycle`（收下 / 退回落盘、退回只预填不发送、只回答的回合交出一卷回答）；冒烟「acceptSent」 |
| 18 | 审批状态镜像（封印变红，雅典娜停在门槛前）；网页没有「批准」意图 | ✅ | Node「approval」×3；冒烟「awaitsApproval / neverApproves」；`TestGameModeCityLifecycle`（审批开始 / 结束都镜像） |
| 19 | 安全边界：意图封闭集合，C# 逐条校验（越界路径、未知类型、「批准」都拒绝）；C# 只发事件类型与工作区相对路径；文字画在 canvas 上 | ✅ | Archive「polis intents」；`TestGameModeSwitchAndFailures`（approve 与越界路径各一条 Warning）；Node 源码扫描 |
| 20 | 失败处理：创建 / 挂载失败、导航失败、WebGL 不可用、就绪超时，四处都在游戏区显示原因 + 技术细节 + 「回到对话」，并记 Warning；Linux 同一条路 | ✅ | `TestGameModeSwitchAndFailures`（四处）；冒烟「WebGL disabled → failed(webgl)」；`NativeWebViewPolisPage.HandleAttachFailure` |
| 21 | 性能：不可见（对话模式 / 窗口最小化）时暂停渲染；空闲降到 30 帧；切回对话后 WebView 保留一段时间再释放 | ✅ | Node「frame policy」；冒烟「pausesWhenHidden / resumesWhenShown」；`TestGameModeSwitchAndFailures`（保留期内不释放、过期释放） |
| 22 | 断言（Archive.Tests）：账本稳定、存档损坏隔离与迁移、离线差异与改名找回、归属、意图校验 | ✅ | 193 个全过（「最后一轮」） |
| 23 | 断言（无头）：模式切换与失败提示（不实例化 WebView） | ✅ | `TestGameModeSwitchAndFailures`、`TestGameModeCityLifecycle`；全套 143 个 `[PASS]`、exit 0（「最后一轮」） |
| 24 | Node 单测 + Playwright 冒烟（Chromium、WebKit）+ 运动自检，覆盖实时模式 | ✅ | 「最后一轮」 |
| 25 | 手动验收清单 `Docs/GameMode_M1_Acceptance_CN.md` | ✅ | 该文件 |
| 26 | 文档即规范：`CLAUDE.md` / `AGENTS.md` 写进实际行为；设计稿被推翻的部分改掉 | ✅ | 两份文档的 "Game Mode" 一节、目录地图与命令表；设计稿 v3（各节"M1："标注） |
| 27 | 本阶段改动全部提交；推送分支；开以 main 为目标的 PR（不合并） | ✅ | 改动已全部提交（工作区只剩两个不属于本阶段的未跟踪文件，没动）；`feature/game-mode-m1` 已推送；PR [mehaifeng/AthenaAgent#29](https://github.com/mehaifeng/AthenaAgent/pull/29)（以 main 为目标，没有合并；它同时带着设计稿与 M0，因为两者都还没进 main） |
| 28 | 最后一轮重跑 a–c，贴结果，打印清单 | ✅ | 「最后一轮」 |

### 待定问题的默认处理（设计稿第 15 节与正文里的"待定"）

| 问题 | M1 的处理 |
|---|---|
| 15.1 点缀色 | 赤陶红 `#b9572f`（设计稿建议，M0 已用） |
| 15.2 玩家外观能否自定义 | 不做（M1 范围外） |
| 15.3 雅典娜配语音 | 不配（走 TTS 有费用；设计稿把它列为待定，默认不花钱） |
| 15.4 猫头鹰 | 不加（美术项，留给 M2） |
| 15.5 启动时恢复上次选中的会话 | **按用户要求不做，留给用户决定**；启动仍选中最近更新的会话 |
| 15.6 2D 半身像风格 | 白底陶瓶线描（设计稿默认）；M1 不画半身像 |
| 9.2 过场时长 | 最短 1.6 秒、上限 6 秒；同城与装城失败都要靠岸 |
| 11.2 一大批改动 | 一批超过 200 处或涉及 8 座以上建筑：整城重扫，公告板"城里起了大变化" |
| 11.3 离线改名配对 | 子项名字集合 Jaccard ≥ 0.5、一对一；没有子项的不配 |
| 12.5 就绪超时 | 从创建页面（开始导航）起 20 秒：导航本身卡住也算超时 |
| 12.6 WebView 保留多久 | 切回对话后 3 分钟 |

### 决定

1. **工作区监视器由工作台驱动**：共享的 `WorkspaceWatcherService` 由 `WorkspaceWorkbenchViewModel.SetWorkspaceAsync` 切换（外壳"当前作用域"的唯一入口，也保住了"先加载、后监听"的次序），游戏按事件里的工作区 Id 认领。没用 `IWorkspaceService.ActiveWorkspaceChanged`：它在每个会话 `AssignWorkspace` 时都会触发（启动恢复全部会话、定时任务在后台开会话），不等于"当前选中"。
2. **`SetWorkspaceAsync` 同时比较 Id 与根目录**：工作区是共享的活对象，重新定位后 Id 不变，只比 Id 会把新位置当成"同一个工作区"什么都不做。
3. **游戏模式下不建气泡树**（设计稿 9.2 的"需要实测"）：`ATHENA_SWAP_PERF=1` 的探针（`ProbeHiddenSurfaceSwap`，20 条助手消息、240 个工具行的会话）：可见换绑 648 ms，**隐藏换绑 843 ms**，都是 10 770 个视觉元素；隐藏换绑之后再显示 0 ms——隐藏的列表照样实体化整棵树。所以不是"藏起来"，而是消息列表不给数据源；`ItemsSource` 改在代码里给（XAML 绑定在 DataContext 换绑时会重新产出值，盖掉 null，测试抓到过）。切回对话：升幕布、等 60 ms（够提交一帧幕布）、再给数据源。
4. **页面放进 `Assets/Polis/`**，随应用作为 Avalonia 资源发布；测试、夹具、导出工具留在 `Tools/GameMode/`。开发用的静态服务按应用的样子挂载 `/polis/`、`/fixtures/`、`/.local/`。
5. **C# → 页面的数据编码**：整批消息序列化成 JSON，再把这段 JSON 文本序列化成一个 JS 字符串字面量（默认编码器转义 `<` `>` `&` `'` 与 U+2028/2029），页面 `JSON.parse`。数据从不拼进脚本。
6. **退回只预填，不发送**：网页的文字有一部分来自模型；"退回"把"关于《…》，请修改："预填进同一会话的输入框并聚焦，由人写完发出。发消息仍只走输入框一条路径。
7. **审批的到达顺序**：渲染模型里工具调用先出现（Running），审批随后弹出。编排器里她若已经站在那儿开工，收到 approval-start 就停手改为在门槛前等，点头后接着做这一处；正在走路时不打断（规则 4），走到了才停。
8. **成果**：一回合一件。写进工作区的文档 / 表格 / 图片（取最后写的那个）是卷轴 / 账册 / 彩绘板；生成了图片是彩绘板；都没有就是"回答"本身（标题取回答第一行）。读回答在页面的卷轴阅读器里（那段 Markdown 按需发给页面，是 12.4 唯一的例外）；读文件在工作台里打开，文件内容不进页面。
9. **后台核对与新近决定的合并**（`MergeReconciled`）：账本、路径、指纹、遗失标记取核对结果，处理状态和核对期间新交来的成果取当前存档——否则扫描期间的"收下"会被悄悄撤销。
10. **手动验收的失败注入**：`ATHENA_POLIS_FAILURE=create|navigation|webgl|timeout`，设了就在启动时写一条 Warning。没有它，一台正常的 Mac 上看不到四处失败提示各自的样子。
11. **雅典娜的"碰过"**：工具参数里的目标路径（读写类）；终端命令只有 `workingDirectory` 落在工作区里才算（命令本身不读）；派发子代理期间整座城都算她的（侍女替她跑腿）。之外一律算外部改动。
12. **页面不可见就停**：`visibility` 消息（模式切换、窗口最小化）加上 `document.hidden`；有东西在动 60 帧，待命 / 沉思 / 等审批 30 帧。
13. **同一时刻只跑一次测绘**（最后一轮之前、为 CI 排查平台差异时读代码发现的两个竞争，都已用 `SurveyScanned` 测试缝确定地复现）：
    装城、整城重扫、增量补丁不重叠；装城抢先（取消在跑的、丢掉上一座城攒下的改动），其余在测绘期间只记下，落地后由 `ContinueAfterSurvey` 补上。
    ① 回来时的核对还没落地，应用外来了一个改动：旧代码起一次整城重扫、把核对取消，**"你离开期间"的报告丢了**；
    ② 增量补丁还在算，用户换了城：旧代码把上一座城的账本合进下一座城的存档并落盘——复现时另一座城的账本变成了 `[账本, 照片, 合同, 新项目, 草稿]`，**建筑全挪了地方**。
    修法：await 之后先看换城取消，写快照用开始时的槽位；补丁的藏品指纹（每件最多 64 MB 的哈希）和扫描一起挪到线程池，原来在 UI 线程上。
    两处各做了一次去掉修复的对照，`TestGameModeCityLifecycle` 分别报「上一座城的补丁不能写进这一座城的存档」与「回来时必须有一份"你离开期间"的报告」。
14. **就绪超时从创建页面起算**（不是导航完成后）：导航本身卡住、`NavigationCompleted` 永远不来时也能超时；设计稿与 `CLAUDE.md` 原先写的"导航完成后"已改正。

### 真实 WebView（一次性探针，不进仓库）

`/tmp/polis-real`：引用 `Athena.UI.csproj` 的小程序，真实 macOS 平台 + `NativeWebView`（WKWebView）+ 回环服务 `/polis/` + `GameModeViewModel`，假外壳与临时目录（不碰开发版 AthenaData）。结果：

```
[INF] Polis page ready                         （窗口显示后约 1 秒）
page status: {"ready":true,"mode":"live","city":{"buildings":4,...},"items":[{"state":"pending",...}],"errors":[],"liveEvents":4,"sent":["ready"]}
save written: True
```

城邦装进了真实的 WKWebView，四个实时事件（开场、工具开始 / 结束、交付）被页面吃下，回答成了一件待收下的成果，存档写盘，页面没有报错。探针窗口是命令行进程开的，WebKit 判定 `document.hidden = true`、暂停了 `requestAnimationFrame`——这正是"不可见就停"的行为；可见时出帧由 Playwright WebKit 冒烟证明。

### 截图审查（被忽略的 `Tools/GameMode/.local/m1-shots/`）

| 画面 | 看到的问题 | 处理 |
|---|---|---|
| 01 航海过场 | 无 | — |
| 02 城邦 + "你离开期间" + 公告板 | 无 | — |
| 03 成果卡片 | 第一版卡片的两行字被按钮压住 | 面板加按钮行的高度（`panel` 的 footer） |
| 04 等审批 | 第一版气泡还是"我在誊写《摘要》"（刚开工就停下等审批，那句不成立了） | 刚开工就被审批打断的那一句撤掉，只说"这一步需要你点头" |
| 05 雾 | 无 | — |
| 06 卷轴阅读器 | 列表项间距偏大 | `li > p` 收紧 |

### 阻塞

（无。设计稿 15.5「启动时恢复上次选中的会话」按要求没做，留给用户决定——它不是阻塞，M1 不依赖它。）

### 最后一轮（2026-10-11，提交 42023d6 之上；工作区只有两个不属于本阶段的未跟踪文件）

f2fe631 上先跑过一遍，全过（47aaf3f 记的就是那一遍）。之后为 CI（ubuntu、Release）排查平台差异时读代码，发现决定 13 的两个竞争，修完（42023d6）在同一台机器上从头重跑，下面是重跑的结果。两次的数字只有用时与 WebKit 回放那一帧的灰阶统计不同。

a. 解决方案构建 + Archive.Tests + 无头套件：

```
dotnet build Athena.UI.sln -p:UseAppHost=false
→ exit=0，0 个错误；唯一的警告是 main 上既有的 MainConversationViewModel.cs(3026) CS1734（fc198fc6），与本阶段无关
dotnet Athena.Archive.Tests/bin/Debug/net10.0/Athena.Archive.Tests.dll
→ [PASS] 193 个，[FAIL] 0 个，exit=0（含 workspace relocation 与 polis 的 ledger / save / offline diff / attribution / intents / events 各用例）
Scripts/run-headless-tests.sh
→ [PASS] 143 行，[ALL HEADLESS TESTS PASSED]，exit=0，用时 55 秒
  其中 game mode（模式切换、游戏行替换消息行且不建气泡树、宠物收起、四处失败各有原因与「回到对话」和 Warning，不实例化 WebView）
  与 game mode city lifecycle（外部改动、重启快照与离线报告、雾与重新定位、监视器溢出整城重扫、审批镜像、收下 / 退回落盘，
  以及决定 13 的两个竞争：核对期间来的改动等它落地、换城丢弃上一座城的补丁）都 PASS
```

b. 新增断言对应（完成标准 b）：账本稳定（删一个文件夹，其他建筑不挪）= Archive「polis layout ledger」「polis city」；存档损坏隔离与迁移 = 「polis save」；离线差异与改名找回 = 「polis offline diff」；
雅典娜与外部归属 = 「polis attribution」；意图校验（越界路径、「approve」被拒）= 「polis intents」；模式切换与失败提示（无头、不实例化 WebView）= `TestGameModeSwitchAndFailures`。

c. 网页：

```
node --test Tools/GameMode/web/test/*.test.mjs        （Node v22.22.3；live / orchestrator / replay 三个文件）
→ tests 39, pass 39, fail 0, exit=0
bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs        （Node v24.16.0）
→ chromium 154.0.8037.98 回放：ready / noPageErrors / noConsoleErrors / nonEmptyFrame（std 30.7、31 灰阶）/ animating / syntheticFixture 全 true
→ chromium 实时：readySent / cityLoaded / awaitsApproval / nonEmptyFrame / acceptSent / relocateSent / neverApproves / pausesWhenHidden /
  resumesWhenShown / readerIsText / voyageLandsWhenReady / voyageWaitsForPartial / noProblems 全 true；页面发回 ready、accept-delivery、relocate
→ chromium 关掉 WebGL：页面回报 failed（reason=webgl，带技术细节），没有发 ready
→ webkit 26.5 回放：同上全 true（std 31.6、30 灰阶）；实时：同上 13 项全 true
→ SMOKE PASSED，exit=0
bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/motion.mjs
→ chromium 154.0.8037.98 与 webkit 26.5：各 118 帧（窗口 6000–9873 ms、42850–43670 ms），光晕 / 旁白气泡 / 建筑名牌最大偏差 0.000 / 0.000 / 0.000 px
→ 最大偏差 0.000 px，验收线 0.5 px：PASSED，exit=0
（对照：--nosync 时 638.749 / 638.791 / 291.323 px——检查抓得住漂移）
```

## M0：城邦原型 + 真实会话回放

### 清单

| # | 项 | 状态 | 证据 |
|---|---|---|---|
| 1 | logs.db 节奏统计（只出聚合数字） | ✅ | 「节奏统计」 |
| 2 | C# 纯函数：扫描、建筑汇总、布局账本、回放转换（+ 工具类别表、夹具组合）；Archive.Tests 断言 | ✅ | 「C# 纯函数」 |
| 3 | 导出控制台 `Tools/GameMode/PolisExport`；合成夹具提交，真实夹具进忽略目录 | ✅ | 「夹具」 |
| 4 | three.js 离线打包 + 引擎选择理由 | ✅ | 「引擎」 |
| 5 | 网页原型：柱廊书库 / 民居 / 作坊等六类程序化建筑、公共建筑、雅典娜（周身有光）、玩家（点哪走哪）、旁白模板、回放 | ✅ | 「网页原型」 |
| 6 | 编排器：6.2 的六条节奏规则 + Node 单测（每条至少一例） | ✅ | 「编排器」 |
| 7 | Playwright 冒烟：Chromium、WebKit 都就绪、无控制台错误、画面非空 | ✅ | 「冒烟测试」 |
| 8 | 运动自检：屏幕空间效果与当帧真实投影的最大偏差 ≤ 0.5 px | ✅ | 「运动自检」 |
| 9 | 真实夹具回放，5 张不同时刻的截图（忽略目录），逐张审查，记下问题与处理 | ✅ | 「真实夹具审查」 |
| 10 | M0 演示说明 | ✅ | `Docs/GameMode_M0_Demo_CN.md` |
| 11 | 本阶段改动全部提交；最后一轮重跑 a–d | ✅ | 「最后一轮」 |

### 节奏统计（logs.db，2026-10-11）

命令：`python3 -I Tools/GameMode/rhythm/rhythm.py`（只读打开两个库：开发版 `bin/Debug/net10.0/AthenaData/Logs/logs.db` 与安装版 `/Applications/Athena.app/Contents/MacOS/AthenaData/Logs/logs.db`；只输出聚合数字）。

锚点（都在 `Services/OpenAIChatService.cs`）：回合开始 `Starting message processing`；一轮模型输出结束 `Usage … (iteration N)`；本轮要求的调用数 `Detected N tool call(s)`；工具开始 `Executing tool`；工具结束 `Function … execution status`（`FunctionRegistry`）；结果回填 `Tool … execution completed`。并发批处理 2026-08-17 才上线（cd90c85），突发大小只统计 08-18 之后。

| 指标 | 开发版（07-23..10-10） | 安装版（08-12..09-14） |
|---|---|---|
| 回合数 | 188 | 43 |
| 每回合工具调用 p50 / p75 / p90 / max | 1 / 6 / 16 / 117 | 2 / 10 / 31 / 43 |
| 不调用工具的回合 | 77（41%） | 10（23%） |
| 每回合模型轮数 p50 / p90 / max | 2 / 13 / 73 | 3 / 23 / 40 |
| 每个工具轮要求的调用数 p50 / p90 / p99 | 1 / 2 / 5 | 1 / 2 / 5 |
| 并发突发大小分布（1/2/3/4 个） | 593 / 32 / 8 / 7（n=640） | 230 / 27 / 3 / 7（n=267） |
| 等模型·首轮（提问 → 首轮流结束）p50 / p90 / p99 | 6.9 / 23.3 / 132 s | 7.1 / 29.5 / 57.8 s |
| 等模型·后续轮（上轮结果回填 → 本轮流结束）p50 / p90 / p99 | 4.8 / 18.0 / 53.9 s | 4.7 / 18.7 / 50.6 s |
| 工具执行时长 p50 / p90 / p99 / max | 0.05 / 3.85 / 149 / 430 s | 0.05 / 0.35 / 23.3 / 120 s |
| 同轮相邻两次调用的开始间隔 p50 / p90 | 0.02 / 2.1 s | 0.00 / 0.14 s |
| 跨轮相邻两次调用的开始间隔 p50 / p90 / p99 | 5.5 / 26.6 / 176 s | 5.0 / 21.0 / 64.3 s |
| 下一次调用在上一次开始后 1 秒内 / 3 秒内到来 | 30.3% / 50.0% | 27.3% / 48.0% |
| 回合总长 p50 / p90 / max | 19 / 232 / 1589 s | 28 / 237 / 362 s |
| Detected 合计与实际开始的调用数对不上的回合（交错 / 中断） | 1 | 0 |

读法与它定下的参数：

- **设计稿 6.2 的"一秒内并行跑完 4 个只读调用"是少数**：九成以上的突发只有 1 个调用，4 个的只有 1%–3%。真实节奏是"一次一个，中间等模型约 5 秒"。
- **工具本身几乎不花时间**（中位 50 毫秒），长尾来自浏览器任务、子代理和终端命令（p99 两分半）。动画绝不能等工具，也不能等工具"结束"再开演；长工具的动作演满一段后要让位（编排器规则 2 的后一半）。
- **等模型才是主旋律**：后续轮中位 4.7–4.8 秒，p90 约 18 秒，最长两分钟。沉思状态必须能撑几十秒不显得卡住（光随推理长度一明一暗）。
- 一次"走过去 + 做事"定在约 5 秒：滑行 12 米 / 秒、步行上限 5 秒（约 60 米），动作 1.1 秒——和等模型的中位数相当，多数时候跟得上；跟不上时由"落后 3 秒就瞬移"兜底。没有估算数据的老气泡，收尾按 4.8 秒估（后续轮中位数）。

### C# 纯函数

M0 不接入应用，所以源码放在 `Tools/GameMode/Polis/`（命名空间已用 `Athena.UI.Services.GameMode`，M1 只需整体挪进 `Services/`）。`Athena.UI.csproj` 加了 `<Compile Remove="Tools/**/*.cs" />`（只影响构建，不影响运行时），`Athena.Archive.Tests` 和导出工程各自逐文件链接。

| 文件 | 内容 |
|---|---|
| `PolisScanner.cs` | 按层遍历、条目上限、深度上限；跳过 `GeneratedDirectories` 与隐藏项；读不了的目录与预算砍掉的部分记成"未完全测绘"；软链接目录不进入 |
| `PolisBuildings.cs` | 扩展名 → 文件类别；过半者定建筑类型（不过半或空 = 民居）；体量取对数分 4 级；状态按最近修改分 5 档 |
| `PolisLayoutLedger.cs` | 棋盘格、广场居中、海在南；生长次序一圈一圈顺时针（整数运算）；已有建筑永不挪位、删掉的留空地、空地不回收、超过上限进市集摊位、损坏账本去重 |
| `PolisToolCategories.cs` | `FunctionRegistry` 注册的 51 个工具 → 7 个动作类别；地点参数的优先级 |
| `PolisReplayConverter.cs` | 归档消息投影 → turn / think / tool / deliver 事件；并发批共享开始时间；剪掉回合间空闲；城外路径不带路径；无正文 |
| `PolisFixture.cs`、`PolisSyntheticFixture.cs` | 组合成网页夹具（JSON）；合成夹具走同一条管线 |

前置小重构（设计稿 14 节列出的那条）：`FileSystemService.GeneratedDirectoryNames` 提成 `Services/GeneratedDirectories.cs`，内容与比较器原样，目录搜索的行为不变（既有用例 `filesystem: directory search groups by file, prunes build output and honors caps` 照常通过）。

Archive.Tests 新增 6 个用例（`polis scan` / `polis buildings` / `polis layout ledger` / `polis replay` / `polis tool categories` / `polis fixture`）。第一次运行就抓到账本的一个真错误：损坏账本里抢了别人地块的条目被丢弃前，它的名字已经登记进了"已有文件夹"，于是这个文件夹之后永远分不到地；已修。其中两条防漂移的断言：类别表与 `FunctionRegistry.cs` 的注册逐个对账（新工具忘了登记会红）；提交进仓库的合成夹具必须与管线输出逐字节一致（改了 C# 忘了重新生成会红）。

### 夹具

- **合成夹具** `Tools/GameMode/web/fixtures/synthetic.json`（提交）：内存目录树 + 编出来的三回合会话，走真实管线生成（`PolisExport synthetic`）。8 座建筑覆盖全部六类；12 次工具调用覆盖读、写、终端、港口、记忆、侍女、城外失败、并发一批、估算收尾、剪掉的空闲。测试、冒烟和演示默认都用它。
- **真实夹具** `Tools/GameMode/.local/real-fixture.json`（被 `Tools/GameMode/.gitignore` 忽略，`git check-ignore` 已确认）：本仓库扫成城邦 + 会话 `64c4ba81`（2026-10-09，5 回合、33 次调用，5 个气泡都记了总时长）。导出输出：

```
城邦：24 座建筑（Workshop 14、StoaLibrary 2、House 7、SculptureGarden 1），市集摊位 0，扫描 4665 项；
回放：5 回合、33 次工具调用、334.4 秒，剪掉空闲 4 处
```

回放事件只有类别、工具名、工作区相对路径（或"城外"）、时间、成功与否、推理长度；工具结果只读 `success`，命令、查询词、URL 不读（断言逐字检查）。

### 引擎

**保留 three.js（0.185.1）作基线，不换 Babylon.js。** 理由，都来自 M0 实际做下来的情况：

1. 打样的整套画风在 three.js 里已经跑通，M0 直接沿用：正交相机、GTAO、PCF 软阴影、按遮罩去色并只给标记物体留色、只绕在雅典娜轮廓外的光晕。换引擎要把这些和全部程序化几何重写一遍，而它们没有遇到 three.js 做不到的地方。
2. v1 倾向 Babylon.js 的理由之一是导航网格插件；城邦是希波达摩斯棋盘格，走路就是街道图上的最短路（约 60 个交叉口的 Dijkstra，`layout.mjs`），用不到导航网格。
3. 屏幕空间效果的漂移问题（12.2）是"渲染前先对齐相机矩阵"的顺序问题，与引擎无关；运动自检证明对齐后偏差为 0。
4. 体积与性能：three 加用到的附加模块打成一个 796 KB 的离线 ESM（`npm run vendor:polis`）。无头 + GPU、1600×1000：Chromium 154 合成城邦 2.7 秒就绪、33 fps，真实城邦（24 座建筑）3.0 秒就绪、28.8 fps；WebKit 26.5 合成城邦 2.7 秒就绪、31.8 fps（`/tmp` 下的一次性测量脚本，按每 5 秒的动画帧数计）。

离线打包沿用 `Tools/PreviewVendor`：新增 `entry-polis-three.mjs` 和 `npm run vendor:polis`，产出 `Tools/GameMode/web/vendor/three.bundle.mjs`，MIT 许可证同目录。页面带 CSP（脚本只来自本站），不引用任何 CDN。

### 网页原型

`Tools/GameMode/web/`：`index.html` + `src/`（纯逻辑：`orchestrator.mjs`、`replay.mjs`、`narration.mjs`、`layout.mjs`；画面：`art/`、`world.mjs`、`render.mjs`、`overlay.mjs`、`main.mjs`）+ `test/`。

- **建筑（全部代码生成）**：柱廊书库（又宽又矮的长廊、一排密柱、单坡屋顶，后墙是卷轴格架）、民居（白灰墙、瓦顶、侧院墙，大的两层）、作坊（木棚、工作台、窑和烟囱）、雕塑园、金库（门廊双柱的小神殿）、仓库；公共建筑：多立克神庙（打样）、圆形图书馆（记忆）、锻炉（终端）、市集、港口（栈桥、船、海神像）、城门（城外）、广场（委托板）。体量等级决定大小与层数；常用的门口有陶罐盆栽，久未动的爬常春藤，一年以上是废墟（塌顶、断柱）；未完全测绘的插一根测量杆。
- **雅典娜**：打样的佩普洛斯真身，周身有光（屏幕空间光晕 + 点光源）；沿街道滑行；按类别做事（卷轴、石板、锤子、船出海、黄金侍女、脚手架）；等模型时原地沉思，光随推理长度一明一暗；失败时光暗下去；落后时化作一道光瞬移；交付时把赤陶红的卷轴放在你脚下。
- **玩家**：深色斗篷的人，点地面就沿街走过去，点建筑就走到它门前；点红色卷轴即收下（颜色褪掉）。
- **旁白**：按类别套模板、`|` 分隔变体，分"出发"和"到场"两套，文件用显示名（《租赁合同》），不出现路径；台词排队、每句有最短显示时间。
- **安全**：名牌、旁白、状态行全部画在 canvas 上，不进 innerHTML；页面 CSP 只允许本站脚本。

### 编排器

`src/orchestrator.mjs` 是纯逻辑（不碰 DOM 和 three.js），按虚拟时钟推进，同样输入得到同样输出（逐帧推进与一次跳到底结果相同，有断言）。六条规则与对应用例：

| 6.2 规则 | 实现 | 用例（`test/orchestrator.test.mjs`） |
|---|---|---|
| 工具执行不等动画 | `ingest` 只记账、立即返回；回放按时间喂，从不看画面 | rule 1（30 个调用边走边喂，引擎时间一个不差）；`replay.test.mjs` 的"喂出去的时间就是夹具时间" |
| 收尾有上限 | 一处的呈现最多"一段路 + 一段动作"；有人排队时动作不超过"结束 + 2.5 秒"、来晚只演一拍；长工具演满一段后让位 | rule 2 × 4（含 5/20/60/200 个地点的突发，收尾都在上限内） |
| 同地点合并 | 队尾同地点、或正在去 / 正在做的同一处，并进同一次拜访 | rule 3 × 4 |
| 移动必须完成 | 走、瞬移、闲逛都有终点，只有开放式步骤可被打断；目的地一经开始不变 | rule 4 × 3 |
| 落后超过阈值就瞬移 | 最旧的待演动作落后 > 3 秒：丢掉中间站点（交付永不丢），瞬移去最新一处 | rule 5 × 4 |
| 等模型时沉思 | 手上没事、回合未交付就沉思，带推理长度 | rule 6 |

```
node --test Tools/GameMode/web/test/*.test.mjs
→ tests 28, pass 28, fail 0
```

变异检查（逐条破坏规则，确认它自己的用例会红；原文件先备份到磁盘、跑完还原）：

| 变异 | 失败的用例 |
|---|---|
| R1 事件时间按积压量平移 | rule 1 |
| R2 去掉收尾上限 | rule 2、rule 4 |
| R2 长工具永不让位 | rule 2 |
| R3 不合并队尾 | rule 3 |
| R4 走到一半可被打断 | rule 1–6 共 11 个 |
| R5 落后也不瞬移 | rule 2、rule 5 |
| R6 等模型时待命而不沉思 | rule 2、rule 6 |

第一版变异检查里 R1、R3 两个变异活了下来（用例"先全部喂完再推进"、三次调用都并进了正在去的那一处），补强用例后才全部被抓住。

### 冒烟测试

`test/smoke.mjs`：自带的静态服务（只绑 127.0.0.1）+ 应用自带的 Playwright 驱动；Chromium 一侧用本机 Chrome（不下载），WebKit 用 Playwright 的 WebKit 26.5。检查：页面报告就绪、无页面异常、无控制台错误（含资源 404）、画面非空（标准差 > 8 且灰阶数 ≥ 16——画面是黑白的，按 5 位量化最多 32 种灰阶）、动画在走、读的是合成夹具。结果见「最后一轮」。

### 运动自检

`?selftest=motion`（`test/motion.mjs` 驱动）：先把合成回放过一遍，挑出一段"走"和一次"瞬移"，在这两个时间窗里逐帧推进（每帧 40 ms，共 128 帧）；每帧镜头跟着雅典娜的同时按脚本绕转（每帧 1.3°）、推拉（缩放 ±35%）、平移（±2.5 m），镜头朝向交给 `lookAt`（与 OrbitControls 相同的陷阱）。比较光晕中心、旁白气泡锚点、全部建筑名牌这一帧用的位置与渲染之后的真实投影，按设备像素计。

- 对齐矩阵（正常代码）：Chromium、WebKit 均为 **0.000 px**（光晕、气泡、名牌三项）。
- `--nosync`（故意跳过"渲染前对齐矩阵"）：最终代码上光晕 882.8 px、气泡 882.8 px、名牌 284.4 px（调速之前的时间窗里是 308 / 308 / 287 px）——检查确实抓得住打样里那种漂移。

### 真实夹具审查

`node Tools/GameMode/web/test/shoot.mjs --fixture ../.local/real-fixture.json --out Tools/GameMode/.local/real-shots`：脚本按回放事件自动挑五个不同时刻（走路、在建筑里做事、在公共建筑做事、沉思、交付），截图存进被忽略的 `Tools/GameMode/.local/`。最终版在 `Tools/GameMode/.local/real-final/`。逐张看过：

| 时刻（最终版） | 画面 | 看到的问题 | 处理 |
|---|---|---|---|
| 0:01.5 沉思 | 广场、神庙、圆形图书馆、市集、港口；雅典娜带光，"好，我来办" | 无 | — |
| 0:04.3 走路 | 她从广场走向锻炉 | 第一版刚出发气泡就说"炉火点上了"——那是到了才成立的话 | 旁白分"出发 / 到场"两套，出发说"去锻炉敲打一下"（361318e） |
| 0:05.6 锻炉 | 站在锻炉前 | ① 在锻炉"干活"本身看不出来，"她在哪干活，哪里就亮"没兑现（9 cd 的点光源在 3 米外只有日光的三分之一）；② 到场后气泡仍是出发那句；③ 到场变体里有一句"再敲打一下"，第一次到也说"再" | ① 做事时她的光移到身前、28 cd、照程 14 米，棚下明显被照亮（904878b），远看仍偏弱，记为 M2 美术项；②③ 锻炉、港口到场时补一句"到场"的话，变体改为"炉火点上了 / 炉火正旺"（本次提交） |
| 0:15.9 交付 | 她把红卷轴放在你脚下，"做好了，你看看" | 第一版里下一回合的开场白"这就去办"盖掉了这句（回合间空闲剪成 3 秒，下一回合在她递东西时就开始了） | 台词排队、每句最短显示时间，交付那句 2.6 秒（904878b） |
| 0:40.0 ViewModels | 第二回合在作坊门口读文件；左边缘红三角指向画面外的第一份成果 | 第一版 14 座作坊一模一样，分不出谁是谁 | 体量等级决定宽度和层数，名字哈希定窑的位置、烟囱、木棚、屋瓦墙面深浅（904878b） |

最终版五张在 `Tools/GameMode/.local/real-final/`（被忽略）；第一版五张与中间几版在 `Tools/GameMode/.local/real-shots/`。

用合成夹具开发时还看出并改掉了：广场以南的柱廊书库从默认镜头只看得到后墙（改为所有建筑正面朝南、朝镜头）；废墟上的常春藤是几块规整的黑椭圆（改成成串的小叶团）；港口地块是一块空的深色方块（铺地、货堆、海神像）；海面太暗压住画面、船沉在水下；小号柱廊书库像一座小神殿、和金库混淆（改成宽、矮、密柱、单坡屋顶）；交付的红卷轴太小（放大并加脉动白环，兼顾色弱）；浏览器自动请求 favicon 产生 404（加空图标）；时间线里一半移动因超过步行上限而瞬移（提速到 12 米 / 秒、上限 5 秒）；工具早已结束时到场只演一闪（只在有人排队时压缩动作）。

### 决定

1. **回放的时间来自会话归档，节奏统计来自 logs.db。** 归档里每条消息都有 `Timestamp`（精确到微秒）：隐藏的工具调用载体在一轮模型输出结束时生成，工具结果在执行完、回填时生成，可见气泡在回合开始时生成、结束时记 `DurationMs`。这足以还原"等模型 / 工具 / 收尾"的真实时间轴，而且归档就是"会话"本身（9.1：一个会话 = 一份委托）。
2. **回放里没有正文**：工具结果只读 `success`，推理只读长度，终端命令、查询词、URL 一概不读；工作区外的路径只说"城外"。
3. **相对路径一律算城外**：文件工具把相对路径解析到知识库 / AthenaData（`FileSystemService.ExpandPath`），终端按进程目录，都不是工作区。
4. **隐藏项（以 `.` 开头）不进城邦**，在 `GeneratedDirectories` 之外额外跳过。
5. **表格 → 金库**；"市集"留给顶层文件夹太多时的摊位（5.1 写的是"金库或市集"）。独立建筑上限先取 30。
6. **海在南边**（z ≥ 2），城市只向北、东、西生长；港口在广场正南的海岸上。默认镜头从东南看过来，前景是港口和海。
7. **所有建筑正面朝南（朝镜头）**，不朝向广场：朝向广场时以南的建筑从默认镜头只剩后墙。等距视角的游戏都这样做，可读性优先。
8. **Chromium 一侧用本机 Google Chrome（154）**，不下载；WebKit 用 Playwright 自带的 WebKit 26.5（v2311），2026-10-11 经用户同意从 Playwright 官方 CDN 下载安装（约 81 MB + ffmpeg 1.1 MB，在 `~/Library/Caches/ms-playwright/`）。
9. 回放事件的类别与猫头鹰村的 `SubAgentZones` 暂时各管各的；类别表逐个核对 `FunctionRegistry` 的注册（断言），合并成一张表是 M1 的事。
10. 编排器参数（都来自节奏统计，见上）：瞬移阈值 3 秒、收尾上限 2.5 秒、动作 1.1 秒、步行上限 5 秒、滑行 12 米 / 秒、回合间空闲剪到 3 秒。

### 留给 M1 / M2 的

- 雅典娜"干活时更亮"远看仍偏弱，人物手里的道具在游戏视距下看不清（M2 美术）。
- 代码仓库里大半是作坊，远看仍相近；名牌是主要的区分（M2 建筑套件可以按子类型细分）。
- 类别表与 `SubAgentZones` 合并、旁白搬进 locale 文件、`Tools/GameMode/Polis` 挪进 `Services/`（M1）。
- 节奏统计里"等模型"包含了首字之前的排队与流式输出，没有拆开（日志里没有首字时间）。

### 阻塞

（无）

### 最后一轮（2026-10-11，提交 edfa464 之上，工作区干净）

a. 解决方案构建 + Archive.Tests：

```
dotnet build Athena.UI.sln -p:UseAppHost=false
→ exit=0（唯一的警告是既有的 MainConversationViewModel.cs(3002) CS1734，与本阶段无关），已用时间 00:05:50
dotnet Athena.Archive.Tests/bin/Debug/net10.0/Athena.Archive.Tests.dll
→ [PASS] 186 个，[FAIL] 0 个，exit=0；新增的 6 个 polis 用例全部 PASS
```

b. 编排器 Node 单测：

```
node --test Tools/GameMode/web/test/*.test.mjs        （Node v22.22.3）
→ tests 28, pass 28, fail 0, exit=0（rule 1–6 各至少一例，见「编排器」）
```

c. Playwright 冒烟：

```
bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/smoke.mjs
→ chromium 154.0.8037.98：ready / noPageErrors / noConsoleErrors / nonEmptyFrame（std 33.9、32 灰阶）/ animating / syntheticFixture 全为 true
→ webkit 26.5：同上全为 true（std 31.7、31 灰阶）
→ SMOKE PASSED，exit=0
```

d. 运动自检：

```
bin/Debug/net10.0/.playwright/node/darwin-arm64/node Tools/GameMode/web/test/motion.mjs
→ chromium 154.0.8037.98：118 帧（窗口 6000–9873 ms 步行、42850–43670 ms 瞬移），最大偏差 光晕 0.000 px · 旁白气泡 0.000 px · 建筑名牌 0.000 px
→ webkit 26.5：同上，0.000 / 0.000 / 0.000 px
→ 最大偏差 0.000 px，验收线 0.5 px：PASSED，exit=0
（对照：--nosync 时 882.8 / 882.8 / 284.4 px）
```
