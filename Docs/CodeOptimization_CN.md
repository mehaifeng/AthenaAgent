# AthenaAgent 代码优化交接

本轮以 `origin/main` 的 `dfc946d` 为基线，该提交已包含定时任务状态修复及三个未用 NuGet 引用的移除。从应用的启动、会话树、设置、工具注册及 Polis 页面入口回查实际调用方，再核对 XAML、资源路径、反射发现的工具和已落盘的数据。

## 本轮改动

| 功能 | 改动与保留依据 |
| --- | --- |
| 会话与工作区 | 移除旧对话页工作区选择器、旧设置字段及切换前归档提示。会话树集中订阅归档事件。启动恢复、外部归档入树、新建、分叉和定时任务复用会话树分组内的工作区。启动时集中读取一次工作区列表，定时任务编辑器打开时从树刷新选项并保留已选项。外壳按选中会话发布活动工作区，后台定时任务使用它自己所在的分组。会话落盘、旧待归档队列恢复、工作区重新定位及分叉来源由各自服务处理。 |
| 设置持久化 | 配置加载与保存共用当前 schema 版本和规范化入口，浏览器旧默认值只在读取 v8 以前的配置时迁移。设置保存后重启，用户指定的旧默认值组合（12 步、80 个标注、不保留登录态）现在可以原样读回。工作区压缩策略共用一种快照复制方式。 |
| 知识库 | 删除零调用方的通用 CRUD、旧补丁匹配实现及仅供这些入口使用的向量接口。文件工作台和文件工具走现有文件服务，文本补丁由 `DiffApplier` 处理。知识库保留专用创建、全文及向量检索、快照和后台整理。Markdown 监听将一批文件事件合并成一次哈希扫描，扫描期间新到的失效通知交给下一次刷新处理。保留现有 SQLite 表、FTS、向量模型指纹及增量更新。 |
| 模型与工具调用 | 删除旧模型选择加载器、角色分配 ViewModel、重复的整理与子代理模型解析器。共用角色设置读取及 Responses 请求参数构造，保留主对话的流式参数和辅助角色原有的采样、推理参数发送规则。删除零调用的 MCP 重连入口，设置页的连接应用和失败重试仍走注册中的生命周期服务。 |
| 视图与打包 | 删除 7 个无引用转换器、两种语言各 40 个无引用键、2 张旧猫头鹰图片及 16 帧旧版猫头鹰动画。运行时主题会按名称加载两张 Logo，资源回归覆盖其打包路径。V2 保留 124 帧。Polis 的维护说明留在源码，`AthenaSoul.md` 保留独立嵌入资源和首次初始化用途，从 Avalonia 资源包中去掉其重复副本。实时语言切换使用 Avalonia 绑定，三个功能窗口关闭时释放标题绑定。 |
| Polis | 删除废弃的脚手架、旧金色侍女分支、永不启用的瞬移分支及实时城市里每帧计算的个人光晕。合并路线通行判断和几何预处理。保留昼夜灯火、人物配色以及美术审查页独立的参考点光。 |
| 注释与说明 | 按当前实现校正工作区所有权、知识库索引时机、压缩用量及检查点持久化、运行时语言切换、TTS 播放、构建前置步骤、旧版猫头鹰动画和 Polis 行为。同步更新根目录两份维护指南以及隐私说明中的更新检查和后台请求。 |

会话入口见 [MainWindowViewModel.cs](../ViewModels/MainWindowViewModel.cs) 和 [ConversationSessionItemViewModel.cs](../ViewModels/ConversationSessionItemViewModel.cs)。配置及索引入口见 [ConfigService.cs](../Services/ConfigService.cs) 和 [KnowledgeBaseService.cs](../Services/KnowledgeBaseService.cs)。Polis 的现行契约见 [Assets/Polis/CLAUDE.md](../Assets/Polis/CLAUDE.md)。

相对基线，应用 C#、AXAML、JavaScript 和项目文件共净减 1,912 行，测试、文档和开发工具单独计。删除的 18 张图片合计 184,767 字节。Debug 程序集中的 Avalonia 资源块由 33,280,376 字节减至 32,944,453 字节，减少 335,923 字节。独立的 `AthenaSoul.md` 资源为 6,497 字节。

## 回归与复跑

在仓库根目录准备 .NET 10、Node.js。首次构建先生成仓库使用的 PTC 本地包源，随后还原解决方案。浏览器自检使用应用输出中的 Playwright 驱动及对应浏览器。

```bash
bash Libraries/Ptc/scripts/build-local-feed.sh
dotnet restore Athena.UI.sln
dotnet build Athena.UI.sln --no-restore
dotnet run --project Athena.Archive.Tests --no-build
dotnet run --project Athena.Presentation.Tests --no-build
bash Scripts/run-headless-tests.sh
dotnet test Libraries/Ptc/Ptc.slnx -c Release
node --test Tools/GameMode/web/test/*.test.mjs
node Tools/GameMode/web/test/smoke.mjs
node Tools/GameMode/web/test/motion.mjs
```

Archive 和 Presentation 是可执行测试程序，无头 UI 由仓库脚本执行。新增回归覆盖配置保存后重启、恢复会话与后台任务的工作区隔离、知识库扫描中途失效及监听更新、已关闭窗口的语言绑定、编译后资源路由。

Linux 上解决方案构建通过，0 警告、0 错误。Archive 212 项、完整无头 UI 套件（162 条通过记录）、Presentation 测试及 PTC 96 项组件测试通过。

Node 的 156 项 Polis 测试通过。Chromium 夹具冒烟通过。Intel 硬件渲染下，完整控制脚本在基线和本轮均等待同一个隐藏的对话结束按钮超时。用同一环境单独重跑了步行、方向键、跑步、对话镜头开合和昼夜灯光，两边均通过。运动自检覆盖 327 帧，人物名牌和气泡最大投影误差均为 0.000 像素。故意关闭矩阵同步时自检检测到 346.289 像素误差。

本轮实测环境为 Linux 和 Chromium。Windows、macOS 及 WebKit 浏览器冒烟待对应环境复验。

## 后续删减的交接

1. 从用户入口追到持有状态的实现。C# 调用之外，检查 DI、XAML 编译绑定、按名称发现的工具、动态资源查询、脚本和发布清单。静态扫描结果可以定位候选，再按实际入口和数据契约确认。
2. 对兼容入口用旧数据跑一遍。配置 schema、归档及待处理文件、SQLite 索引、工具协议和工作区迁移都可能由旧版本写入。删实现前先确认谁读取、何时清理、能否重试。
3. 共用代码时比较线上的参数形状和调用时序。模型的默认值、字段省略、请求身份及后台任务与活动界面的隔离都需要在正常入口覆盖。
4. 图像和脚本按最终产物验证。对动态路径做正向加载，在应用程序集验证移除的路由。修改 Polis 渲染后跑 Node 测试和实际 Chromium，记录渲染器、基线和发生超时的具体断言。
5. 从目标分支建独立工作树，按行为分批提交。记录资源数量与体积、测试命令及失败复现条件，随后更新对应的维护文档。

2026-11-27 的桌面专项覆盖冷启动、长会话滚动、两侧实时拖栏和供应商模型选择。负载、复现命令及模块核查记录见[桌面性能报告](DesktopPerformance_CN.md)。
