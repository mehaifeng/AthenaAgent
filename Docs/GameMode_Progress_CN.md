# 游戏模式进度

> 配合 `Docs/GameMode_Design_CN.md`（设计稿 v2）与 `Docs/GameMode_Goals_CN.md`（三段目标）使用。上下文被压缩后先读这里再继续。
> 分支：`feature/game-mode-m0`（从 `docs/game-mode-design` 拉出，main 上还没有设计稿）。

## M0：城邦原型 + 真实会话回放

### 清单

| # | 项 | 状态 | 证据 |
|---|---|---|---|
| 1 | logs.db 节奏统计（只出聚合数字） | ✅ | 见「节奏统计」 |
| 2 | C# 纯函数：扫描、建筑汇总、布局账本、回放转换、工具类别表；Archive.Tests 断言 | ✅ | 见「C# 纯函数」 |
| 3 | 导出控制台 `Tools/GameMode/PolisExport`；合成夹具提交，真实夹具进忽略目录 | 🔄 合成夹具已提交，真实夹具待导出 | |
| 4 | three.js 离线打包 + 引擎选择理由 | ⬜ | |
| 5 | 网页原型：城邦（柱廊书库 / 民居 / 作坊等）、雅典娜（周身有光）、玩家（点哪走哪）、旁白模板、回放 | ⬜ | |
| 6 | 编排器：6.2 的六条节奏规则 + Node 单测（每条至少一例） | ⬜ | |
| 7 | Playwright 冒烟：Chromium、WebKit 都就绪、无控制台错误、画面非空 | ⬜ | |
| 8 | 运动自检：屏幕空间效果与当帧真实投影的最大偏差 ≤ 0.5 px | ⬜ | |
| 9 | 真实夹具回放，5 张不同时刻的截图（忽略目录），逐张审查 | ⬜ | |
| 10 | M0 演示说明 | ⬜ | |
| 11 | 本阶段改动全部提交；最后一轮重跑 a–d | ⬜ | |

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

读法：

- **设计稿 6.2 的"一秒内并行跑完 4 个只读调用"是少数**：九成以上的突发只有 1 个调用，4 个的只有 1%–3%。真实节奏是"一次一个，中间等模型约 5 秒"。
- **工具本身几乎不花时间**（中位 50 毫秒），长尾来自浏览器任务、子代理和终端命令（p99 两分半）。动画绝不能等工具，也不能等工具"结束"再开演。
- **等模型才是主旋律**：后续轮中位 4.7–4.8 秒，p90 约 18 秒，最长两分钟。沉思状态必须能撑几十秒不显得卡住。
- 一半的调用在上一次开始后 3 秒内到来：一次"走过去 + 做事"超过 3 秒就会排队，所以 3 秒的瞬移阈值会在同一轮的连续调用里触发——同地点合并要先吃掉其中的大部分（同一轮里多是同一座建筑）。

### C# 纯函数

M0 不接入应用，所以源码放在 `Tools/GameMode/Polis/`（命名空间已用 `Athena.UI.Services.GameMode`，M1 只需整体挪进 `Services/`）。`Athena.UI.csproj` 加了 `<Compile Remove="Tools/**/*.cs" />`（只影响构建，不影响运行时），`Athena.Archive.Tests` 和导出工程各自逐文件链接。

| 文件 | 内容 |
|---|---|
| `PolisScanner.cs` | 按层遍历、条目上限、深度上限；跳过 `GeneratedDirectories` 与隐藏项；读不了的目录与预算砍掉的部分记成"未完全测绘"；软链接目录不进入 |
| `PolisBuildings.cs` | 扩展名 → 文件类别；过半者定建筑类型（不过半或空 = 民居）；体量取对数分 4 级；状态按最近修改分 5 档 |
| `PolisLayoutLedger.cs` | 棋盘格、广场居中、海在南；生长次序一圈一圈顺时针（整数运算）；已有建筑永不挪位、删掉的留空地、空地不回收、超过上限进市集摊位、损坏账本去重 |
| `PolisToolCategories.cs` | 51 个注册工具 → 7 个动作类别；地点参数的优先级 |
| `PolisReplayConverter.cs` | 归档消息投影 → turn / think / tool / deliver 事件；并发批共享开始时间；剪掉回合间空闲；城外路径不带路径；无正文 |
| `PolisFixture.cs`、`PolisSyntheticFixture.cs` | 组合成网页夹具（JSON）；合成夹具走同一条管线 |

前置小重构（设计稿 14 节列出的那条）：`FileSystemService.GeneratedDirectoryNames` 提成 `Services/GeneratedDirectories.cs`，内容与比较器原样，目录搜索的行为不变（既有用例 `filesystem: directory search groups by file, prunes build output and honors caps` 照常通过）。

Archive.Tests 新增 6 个用例（`polis scan` / `polis buildings` / `polis layout ledger` / `polis replay` / `polis tool categories` / `polis fixture`）。第一次运行就抓到账本的一个真错误：损坏账本里抢了别人地块的条目被丢弃前，它的名字已经登记进了"已有文件夹"，于是这个文件夹之后永远分不到地；已修。

```
dotnet build Athena.Archive.Tests -p:UseAppHost=false
dotnet Athena.Archive.Tests/bin/Debug/net10.0/Athena.Archive.Tests.dll
→ 186 个 [PASS]，0 个 [FAIL]，退出码 0
```

### 决定

1. **回放的时间来自会话归档，节奏统计来自 logs.db。** 归档里每条消息都有 `Timestamp`（精确到微秒）：隐藏的工具调用载体在一轮模型输出结束时生成，工具结果在执行完、回填时生成，可见气泡在回合开始时生成、结束时记 `DurationMs`。这足以还原"等模型 / 工具 / 收尾"的真实时间轴，而且归档就是"会话"本身（9.1：一个会话 = 一份委托）。老气泡没有 `DurationMs` 时，收尾按 logs.db 的后续轮中位数 4.8 秒估，并在事件上标 `estimated`。
2. **回放里没有正文**：工具结果只读 `success`，推理只读长度，终端命令、查询词、URL 一概不读；工作区外的路径只说"城外"。断言逐字检查。
3. **相对路径一律算城外**：文件工具把相对路径解析到知识库 / AthenaData（`FileSystemService.ExpandPath`），终端按进程目录，都不是工作区。
4. **隐藏项（以 `.` 开头）不进城邦**，在 `GeneratedDirectories` 之外额外跳过。
5. **表格 → 金库**；"市集"留给顶层文件夹太多时的摊位（5.1 写的是"金库或市集"）。独立建筑上限先取 30。
6. **海在南边**（z ≥ 2），城市只向北、东、西生长；港口在广场正南的海岸上。默认镜头从东南看过来，前景是港口和海。
7. **Chromium 一侧用本机 Google Chrome（154）**，不下载；WebKit 用 Playwright 自带的 WebKit 26.5（v2311），2026-10-11 经用户同意从 Playwright 官方 CDN 下载安装（约 81 MB + ffmpeg 1.1 MB，在 `~/Library/Caches/ms-playwright/`）。
8. 回放事件的类别与猫头鹰村的 `SubAgentZones` 暂时各管各的；类别表逐个核对 `FunctionRegistry` 的注册（断言），新工具忘了登记会被抓住。合并成一张表是 M1 的事。

### 阻塞

（无）
