# 子代理猫头鹰动画 V2 设计

状态：V2 已制作并接入项目。美术方向：保留现有灰褐色写实猫头鹰，精修素材和动作。

## V2 实际交付

- 14 组 / 124 张独立透明 PNG，每张 256 × 256，8 px 透明安全边距。
- 行走、振翅、落地各 12 帧；待机、眨眼、思考、阅读、工具操作、起飞、滑翔、完成、异常、取消、谢幕各 8 帧。
- 正式素材在 `Assets/SubAgents/V2`；原稿、提示词和锚点在 `Artwork/SubAgents`。制作、重建和验证方法见该目录 README。
- `OwlAnimationPlayer` 统一按单调时间采样帧与位置；小镇用挂载期间共享的 16ms DispatcherTimer 驱动。入口飞行共用同一素材缓存和振翅节奏。
- 跨区动作接起飞、飞行和落地；落地不再限于归巢区；飞行期间不允许随机游走；批次谢幕等待归巢飞行结束。
- 独立帧播放预览：`Docs/OwlAnimationPreview.html`，包含慢放、背景切换与逐帧检查；不再使用旧四帧素材。

下方保留最初的设计分析和长期目标。合成线程动画、系统减少动态效果设置、转身补间和按脚底深度排序仍是后续优化方向，未包含在当前实现中。

关键姿态设定稿：`Docs/images/owl-animation-v2-concept.png`（内置 imagegen 生成，使用现有 idle-01、walk-03、fly-02 三张精灵作为角色参考）。这张图用于评审形象与姿态，背景不透明，并非可直接运行的帧序列。

## 1. 当前问题与证据

- `Assets/SubAgents` 是 16 张独立 PNG，每种动作（idle / walk / fly / land）仅 4 帧。当前播放代码没有运行时图集裁剪，因此如果独立 PNG 内已有相邻帧残片，改采样模式不能清除它。抽看 `owl-walk-03.png` 可见右缘碎片；原始图集和导出工程未在本次检查中找到，不能据此断言最初的裁剪算法。
- `Views/OwlVillageView.axaml.cs` 每 120 ms 推进一次精灵，最多约 8.3 次更新/秒；`SubAgentViewModel.AdvanceSprite` 的落地帧间隔却是 90 ms，因此有些帧可能被跳过。
- 行走、飞行一律 700 ms，不考虑移动距离；短距离像滑行，长距离像冲刺。
- 待机四帧等速轮播，缺少“停留—短眨眼—停留”的节奏。
- 动作切换把图片盒从 58 改成 64 / 56；未统一脚底锚点时，会同时产生尺寸变化和位置跳动。
- `ShouldWanderNow` 只检查业务状态和下次游走时间，不检查飞行/落地状态；视图打开即调用 Wander，可能覆盖未结束的飞行。
- 飞行只在目的地是 Perch 时接落地，其余跨区移动直接回到 Idle。
- 全身 8% 同步呼吸同时缩放角色，容易呈现充气感；标签应独立于身体动作。
- 入口飞行与小镇分别加载飞行图片、分别维护帧节奏，后续增加帧容易漏改一处。

## 2. 动画气质

安静、专注、有生命感。保留原猫头鹰的面盘、耳羽、眼睛、灰褐色羽纹和真实翼形。

细腻来自预备动作、重心转移、停顿、缓冲与自然错峰。持续工作以小动作表达；跨区移动才使用完整飞行。避免持续跳动、频繁绕圈和持续粒子特效。

## 3. 动作库（制作目标，需按实机观感微调）

| 动作 | 独立绘制帧目标 | 节奏 | 播放规则 |
| --- | ---: | --- | --- |
| 安静待机 | 6 | 2.8–4.2 s 呼吸周期，躯干约 1–2% | 循环；眼睛不跟着呼吸缩放 |
| 眨眼 | 5 | 合眼 60 ms、闭眼 70 ms、睁眼 100 ms | 间隔 3–7 s；每只独立偏移 |
| 思考 | 8 | 侧头 → 停 0.6–1.2 s → 回正 | 冥想区低频触发，约 6–12 s 一次 |
| 阅读 / 检索 | 10 | 视线扫描 → 点头；偶尔翻页 | 书房 / 文件区；可中断的小片段 |
| 操作工具 | 10 | 低头观察 → 翼尖轻点 → 停顿 | 电脑 / 工坊；不模拟每次真实按键 |
| 行走 | 10 | 左接触 → 下沉 → 经过 → 抬起 → 右接触 | 步频与位移绑定，脚落地时减少滑动 |
| 起飞 | 8 | 140 ms 蓄力 → 100 ms 推地 → 展翼 | 单次，约 350–450 ms |
| 振翅 | 12 | 下拍更快，上抬略慢 | 单周期约 600–750 ms |
| 滑翔 | 6 | 身体微倾，翼尖轻调 | 长路径可插入 250–500 ms |
| 落地 | 10 | 伸脚 → 接触 → 压低 → 回弹 → 收翼 | 每次飞行到达都播放，约 450–600 ms |
| 完成 | 8 | 收翼 → 轻点头 → 安静停驻 | 只播一次，随后休息 |
| 异常 / 取消 | 6 / 6 | 停下 → 轻侧头 / 收翼 | 各播一次；不用摇晃或高频闪烁 |
| 谢幕 | 8 | 看向出口 → 轻抬翼 → 淡出 | 整批结束一次；与编排器移除时刻一致 |

这些是独立姿态数，不是复制图片或对相邻帧叠化得出的“帧数”。羽翼大幅运动不使用叠化补帧，否则会出现双翅重影。

## 4. 行为衔接

```text
等待 → 待机 / 眨眼
执行 → 按区域选择思考 / 阅读 / 操作
同区小幅换位 → 行走 → 站稳 → 当前工作动作
跨区 → 起飞 → 振翅 / 滑翔 → 落地 → 当前工作动作
成功 → 飞向归巢 → 落地 → 完成点头 → 休息
异常 / 取消 → 就近安全收势 → 异常 / 取消姿态
整批结束 → 谢幕 → 移除
```

- 业务状态立即显示；视觉动作只允许有限的收势延迟（建议不超过 200 ms）。不能让用户误以为已失败的任务仍在运行。
- 终态 / 谢幕优先级最高，其次跨区移动，其次工具动作，最后待机和游走。
- 飞行与落地期间不发起随机游走。快速连续区域请求只保留最新目标，延续当前位置和速度，不能从上一个目标位置重新起飞。
- 保留现有区域最小停留机制；移动中的目标变更与工作区业务状态分开处理。
- 将无目的游走间隔从 1.2–3.5 s 调整为约 5–9 s，工作期间进一步减少。首次打开面板先恢复当前姿态，再决定是否换位。
- 地面左右切换用 100–160 ms 的转头 / 转身过渡；避免行走中瞬间水平翻转。
- 飞行时影子保留在地面，随离地高度变小、变淡；落地时恢复。标签保持稳定，不随身体呼吸、摇摆。
- 场景里重叠时按脚底 Y 坐标排序；避让目标找不到时保留原地，不能在最后一次失败尝试后仍强行采用重叠目标。

## 5. 素材规范：在导出阶段消除串帧

1. 每帧从独立图层 / 画板导出透明 PNG，建议统一 256 × 256；不再从自由排版的生成图中等分切格。
2. 每个姿态使用同一身体比例与地面脚底锚点；空中另记录身体中心锚点。展翼只扩大轮廓，不缩小整只猫头鹰以塞进同一外接框。
3. 保留至少 8 px 透明安全边距，翼尖、脚趾不能碰边。若动作不够放，统一扩大全套画布，而非单帧压缩。
4. 元数据记录动作名、帧文件、每帧持续时间、锚点、循环 / 单次、接触时刻、退出标记。不要靠文件名推断所有时序。
5. 透明区必须是真实 alpha。清理前先检查像素连通区域；自动工具仅标记可疑小碎片，人工确认后去除，避免删掉合法脚趾和细羽。
6. 输出后分别在浅色、深色、棋盘格和场景背景上检查，排查黑边、白边、半透明脏点。
7. 第一版继续使用独立 PNG，整套一次解码共享。只有实际测量证明有收益才引入图集；届时必须记录整数像素源矩形、帧间隔和采样隔离策略。

本次生成的动作设定图只是关键姿态和精修方向参考，不能直接切成最终帧。最终连续帧仍需逐帧校准角色比例、身体锚点和轮廓。

## 6. 播放结构

- `OwlAnimationCatalog`：共享素材和帧时长，供小镇与入口飞行共用。资源缺失在验证时明确失败。
- `OwlAnimationPlayer`：纯时间轴，输入单调递增时间，输出动作、帧索引、动作进度；与业务 ViewModel 分离。
- `OwlMotionController`：处理移动、朝向、优先级、退出点和最新目的地。
- ViewModel 保留业务状态和目标区域；View / 专用控件负责动画显示与生命周期，避免每帧构造 transform 字符串。
- 帧切换目标 16–24 fps；位移与细微缩放优先由 Avalonia 合成动画驱动，按显示刷新率插值。更多精灵帧不能单独保证流畅。
- 使用累计帧时长查找当前帧；单次动作到末帧后停住，不取模回到第一帧。时钟停顿后直接计算当前状态，不补播过期事件。
- 暂停、关闭面板和最小化时停止无意义更新；恢复时重新采样当前业务状态。多个角色共享调度，独立相位。
- 不把 UI DispatcherTimer 当作稳定渲染时钟。正式接入须验证合成动画与帧动画在 UI 繁忙时的表现，不能承诺卡顿彻底消失。
- 提供减少动态效果模式：停用游走、弹跳和飞行路径，保留静态角色、文字状态与短淡入淡出。

## 7. 验收与接入顺序

### P0：素材与时间轴

- 全帧尺寸、文件完整性、alpha 安全边距、锚点范围检查。
- 每段动画在首帧、边界前后、结束时刻、长时间跳跃后的帧索引测试。
- 先替换最常见的待机、行走、飞行、落地；共享入口与小镇素材加载。

### P1：衔接与工作动作

- 飞行期间游走不能覆盖它；所有区域飞行均接落地；取消 / 异常能及时收势。
- 终态不再游走；快速区域切换不瞬移；反复打开面板不会重新触发旧的完成动画。
- 检验动画期间标签不缩放、角色脚底不跳动、飞翼不被裁剪。

### P2：实际观感与性能

- 按真实显示大小检查：当前小镇角色约 46–51 逻辑像素（图片盒再乘 0.8），入口仅 26 像素；精细羽纹不能代替清楚轮廓。
- 在 100% / 150% / 200% DPI、两种主题和六种区域背景下录制慢放，逐帧检查边缘与翼形。
- 在项目配置允许的最大并发数下测量 CPU、内存与帧间隔；面板关闭后验证没有持续精灵更新。
- UI 改动经 `Scripts/run-headless-tests.ps1` 验证；无头截图只能证明布局和状态，连续动作还需真实渲染录屏检查。

## 初期设计交付记录

第一轮仅提供现状诊断、动作规范、状态衔接、素材生产规范、旧素材节奏原型和 AI 关键姿态设定稿。用户确认要求完整制作后，补齐了顶部列出的 V2 连续帧与项目接入；原先 4×3 设定图仍仅作为美术参考。

### 设定稿生成提示词

使用内置 imagegen，原始提示词如下：

> Use case: stylized-concept. Asset type: animation art direction and key-pose concept sheet, NOT a production sprite atlas. References 1-3 show the exact existing Athena desktop subagent owl character, idle, walking and flight. Preserve this character's realistic grey-brown feather texture, tufted ear silhouette, mature alert eyes, compact body, beak, proportions and restrained natural palette; do not turn it into a cartoon, logo or plush. Produce one refined 4 column x 3 row key-pose sheet on a clean warm pale grey background. Same owl identity and consistent body scale in every pose, entire wings and feet visible, extremely generous empty separation between poses, no stray dots, no neighboring limbs intruding, no ground props behind owl, no text or labels. Left to right top row: neutral attentive standing; blink nearly closed eyes; subtly tilted head thinking; looking down studying a small open book. Second row: delicate forward walking contact pose; slightly crouched takeoff anticipation; full upward extended wings takeoff; full downward swept wings mid flight. Third row: graceful gliding with spread wings; landing feet contact with body compressed and wings partly open; composed proud success pose with one wing lightly raised; quiet resting pose head slightly tucked. Clean carefully antialiased silhouettes, preserve feather detail without dirty edge fragments. This is an art concept reference for later hand-aligned frame production, not an automatically sliceable atlas. No scenery, no typography, no borders, no sparkles, no multiple owl characters per cell.
