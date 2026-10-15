# 游戏模式资产清单：把一件程序化资产换成外部模型

> 2026-10-11（M2）。城邦里画的每一件东西缺省都由代码生成（设计稿第 1 节"美术全部由代码生成，保留资产清单接口，
> 任何一件都能单独换成外部模型"）。这份说明讲怎样用一份清单把其中任何一件换成你自己的 glTF 模型，以及换不上时会发生什么。
> 2026-10-12：人物可以写 `"rig": "procedural"`，把一个静止站着的模型（文生 3D 出来的就是这样）蒙在这个人物自己的程序化骨架上，
> 动作全用程序化的那一套（第 6 节）。前一天夜里修了内嵌贴图被页面的内容安全策略拦掉、外部模型只剩白模的问题。
> 实现：页面 `Assets/Polis/src/art/assets.mjs`（资产表与资产库）、`asset-manifest.mjs`（回退规则，Node 单测）、`bind.mjs`（蒙到程序化骨架上）；
> C# `Services/GameMode/PolisAssetManifest.cs`（校验）、`PolisAssetStore.cs`（读文件）、`OfficePreviewHost` 的 `/polis-assets/` 路由。

## 1. 放在哪

```
AthenaData/Game/assets/
├── manifest.json      ← 清单
├── house.glb          ← 模型（.glb 推荐：一个文件自带网格、贴图、动画）
├── athena.gltf        ← 也可以是 .gltf，它引用的 .bin / 贴图放在同一个文件夹里
└── athena.bin
```

- 只认这一个文件夹，**不认子文件夹**：清单和它引用的每一个文件都必须是这个文件夹里的一段文件名。
- 文件夹不存在、没有清单：全部用程序化的资产（这是缺省状态）。
- 改完清单，**重新进入游戏模式**（或重启应用）生效：清单在页面就绪时读一次。

## 2. 格式

```json
{
  "schema": 1,
  "assets": {
    "building.house": { "model": "house.glb", "scale": 1.0, "rotateY": 0, "offset": [0, 0, 0] },
    "prop.amphora":   { "model": "amphora.glb", "scale": 0.9 },
    "figure.scribe":  {
      "model": "scribe.glb",
      "animations": { "walk": "Walk", "idle": "Idle", "work": "Work", "deliver": "Give" }
    },
    "figure.athena":  { "model": "athena.glb", "scale": 1.62, "rig": "procedural" }
  }
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `schema` | 是 | 固定为 `1`。别的值整份不用。 |
| `assets` | 是 | 资产 Id → 条目。Id 见第 4 节。 |
| `model` | 是 | 资产文件夹里的一段文件名，扩展名 `.glb` 或 `.gltf`；1–64 个字母、数字、`.` `_` `-`，不以 `.` 开头。 |
| `scale` | 否 | 整体缩放，(0.01, 100]，缺省 1。 |
| `rotateY` | 否 | 绕竖轴转多少度，[−360, 360]，缺省 0。 |
| `offset` | 否 | `[x, y, z]` 米，每个分量在 ±50 以内，缺省 `[0, 0, 0]`。 |
| `animations` | 否 | 只对人物有用：动作名（`walk` / `run` / `idle` / `work` / `deliver` / `talk` / `wave` / `receive`）→ 模型里的动画片段名。M3 加了跑、说话、挥手、接过。 |
| `rig` | 否 | 只对人物、只能写 `"procedural"`：模型蒙在这个人物自己的程序化骨架上，动作全是程序化的（第 6 节）。和 `animations` 互斥——两样一起写整条拒绝。 |

整份清单不超过 64 KB、最多 256 条；单个文件不超过 64 MB。

## 3. 坐标约定（模型要按这个摆）

- 单位是米，y 向上。
- **原点在资产底面的中心**，地面是 y = 0。
- **正面朝 +z**（城里所有建筑正面朝南、朝镜头）。
- 建筑：放得进一块 12.5 × 12.5 米的地块（地块中心就是原点）。门口一侧朝 +z，雅典娜和你站在门前的街沿上。
- 人物：程序化的人是 1.75 米高，雅典娜 1.9 米；脚底在 y = 0，面朝 +z。
- 器物、植物：底面中心在原点。场馆底座的顶面在 y = 0.8（陈列的藏品会被放在那里）。

## 4. 资产 Id

| 种类 | Id |
|---|---|
| 文件夹的建筑（设计稿 5.1） | `building.stoaLibrary`（柱廊书库）、`building.house`（民居）、`building.workshop`（作坊）、`building.sculptureGarden`（雕塑园）、`building.treasury`（金库）、`building.warehouse`（仓库） |
| 公共建筑 | `civic.temple`、`civic.tholos`（圆形图书馆）、`civic.forge`、`civic.market`、`civic.gate`、`civic.agora`（广场：委托板、日晷、讲台）、`civic.harbor`、`civic.boat`、`civic.paintedStoa`、`civic.archive`、`civic.vault`、`civic.terrace`（港前石台）、`civic.groveMarker`；城北乡间（第四轮）：`civic.field-wheat` / `-greens` / `-vines`（田，不参与碰撞，人走在垄沟里）、`civic.pasture`（牧场，围栏挡路，朝城一侧留口） |
| 器物 | `prop.amphora`、`prop.hydria`、`prop.krater`、`prop.kylix`、`prop.oinochoe`、`prop.lekythos`、`prop.lamp`、`prop.brazier`、`prop.tripod`、`prop.stool`、`prop.chest`、`prop.scrollCase`、`prop.easel`、`prop.herm`、`prop.stele`、`prop.crate`、`prop.basket`、`prop.pedestal`、`prop.statue`；成果：`prop.item-scroll` / `-ledger` / `-painting` / `-answer`（待收下，地上），`prop.display-scroll` / `-ledger` / `-painting` / `-answer`（陈列在底座上）；工位（第三轮）：`prop.station-wheel` / `-trough` / `-block` / `-sawhorse` / `-loom`（居民门前干活的陶轮、揉面台、石料、锯木架，第四轮加织工的立式织机；不参与碰撞，按干活的人的身高缩放）；第四轮的杂物：`prop.sack`（麻袋）、`prop.netRack`（晾网架）、`prop.woodpile`（柴垛）、`prop.sheep`（羊，不参与碰撞） |
| 植物 | `plant.olive`、`plant.cypress`、`plant.laurel`、`plant.potted`、`plant.acanthus` |
| 人物 | `figure.athena`、`figure.player`，居民 `figure.surveyor`、`figure.scribe`、`figure.accountant`、`figure.smith`、`figure.sailor`、`figure.librarian`、`figure.maiden`、`figure.herald`、`figure.painter`、`figure.artisan`、`figure.priestess` |

资产表在页面里（`createAssetRegistry`），是 Id 的事实来源；清单里写了资产表没有的 Id，页面忽略它、记一笔。

## 5. 换上之后有什么不同

- **一种建筑一个模型**：程序化的建筑按体量（文件数）、状态（常用 / 冷清 / 爬藤 / 废墟）和子型变化；外部模型对这一类的每一座都一样，
  不随这些变化（脚手架、名牌照常）。
- **城里一律黑白**：外部模型和程序化的一样走黑白调色，颜色会去掉，只剩明暗——挑模型时看它的明暗关系。
  点缀色（赤陶红）只留给"轮到你了"的东西，外部模型上的红色不会被留下。
- **人物没有手里的小件**：程序化人物手里的卷轴、石板、锤子按动作出现；外部人物只播它自己的动画片段。
  "轮到你了"的两样东西——交付时的红卷轴、等审批时手中石板的红封印——会浮在外部人物的胸前，颜色的规则不因为换了模型而失效。
- **雅典娜的光**：换成外部模型的雅典娜照样被标进光的遮罩，周身的光晕和她脚下的那盏光都还在。
- **动作**：每个动作播 `animations` 里映射的片段，换片段时交叉淡入 0.22 秒；没映射的动作按 跑 → 走 → 停、说话 / 挥手 / 接过 / 做事 / 交付 → 停 退；
  连 `idle` 都没有就静止不动。挥手、接过只播一遍（`LoopOnce`）。
  人在城里的移动由页面驱动（雅典娜每秒走 3.2 米、你每秒 6 米、按住 Shift 跑 10.5 米），`walk` / `run` 片段按原地踏步做即可，不要带根运动。
- **外部人物没有弹簧骨**：程序化人物的衣摆、面纱、斗篷挂在弹簧骨上随走动和风飘（M3）；外部模型要这个效果，把布料动画烘进片段里。

## 6. 人物蒙在程序化骨架上（`"rig": "procedural"`）

文生 3D、雕刻、扫描出来的人物多半是**静止**的：一个网格，没有骨头，没有动画。按第 5 节直接换上，人在城里只会整体平移
（M3 里否掉的"滑行"）。写上 `"rig": "procedural"`，页面（`bind.mjs`）会：

1. 把模型按清单摆好（`scale` / `rotateY` / `offset`），每 1 厘米切一层，找出脖子、肩、腋下、肘、腕、指尖、腰、衣摆、髋、膝、踝；
2. 按部位算蒙皮权重：手臂分给上臂、前臂、手，肩头匀一部分给上臂，头跟头，腰以下的衣裙按左右分给两条大腿、
   一部分跟胯、一部分跟裙摆关节（走起来前后摆、跑起来往后飘），衣摆边上绕着腿的那一圈分一点给小腿，露出来的腿和脚跟腿；
3. 建这个人物的程序化版本，拆掉它的身体（手里的东西留着），关节挪到模型的关节上，模型蒙上去。

之后这个人物的一切照旧：走 / 跑 / 停（带小动作）/ 做事 / 交付 / 说话 / 挥手 / 接过、动作之间 220 毫秒的过渡、头看向对方、
步频跟着速度走（脚不打滑）、手里的卷轴石板锤子、交付时的红卷轴和等审批时的红封印。

**模型的要求：**
- **A-pose**：两臂离开身体，腋下以下和躯干之间有空当（找手臂靠这个空当）；手不碰身体、不拿东西，手指最好张开。
  T-pose（手臂水平）和手贴着身体都不行。
- 两脚分开、站直，面朝 +z，摆好之后脚底在 y = 0、按真人身高（雅典娜 1.9 米）。
- 衣服可以是长裙；短衣也行（露出来的大腿跟大腿）。

**找不出关节时**（没站成 A-pose、手贴着身体、模型太小……）这一件退回程序化的人物，`console.warn` 记一行原因
（`falls back to the procedural asset: the arms do not stand apart from the body; the model must be in an A-pose`）。

**和程序化人物不一样的地方：**
- 裙摆不再是布料模拟，跟着大腿和裙摆关节摆；面纱、辫子、斗篷这些弹簧骨也没有了——它们挂在程序化的衣料上。
- 手指不会握：手里的东西照样在手上，手指还是张开的。
- 手臂举过肩很高时（挥手、挠头），腋下会挤出一道褶（线性蒙皮的老问题）。
- 模型自己的手比程序化的手长，碰头、抱臂这种贴着身体的小动作会对不太准。

**审查：** 美术审查页可以带清单渲染外部模型——
`index.html?gallery=figure.athena&action=walk&frames=8&zoom=6&manifest={"figure.athena":{"model":"athena.glb","scale":1.62,"rig":"procedural"}}`
（模型从 `/polis-assets/` 取，测试服务器要把它挂到放模型的文件夹）；再加 `&weights=1` 按蒙皮权重上色，
每根骨头一种颜色，查权重分错了地方。

## 7. 换不上会怎样（缺省回退）

任何一层出问题，那一件都照常用程序化的，城照样画出来：

| 哪里 | 情况 | 结果 |
|---|---|---|
| C#（读清单时） | 不是 JSON、`schema` 不是 1、Id 写法不对、`model` 不是一段 `.glb`/`.gltf` 文件名、文件不在资产文件夹里（或是一个指到文件夹外面的软链）、数字越界、动作名不认识 | 那一条不推给页面；**每条一行 Warning**（日志里 `Polis asset manifest … : …`） |
| 页面（核对时） | Id 不在资产表里、条目形状不对 | 忽略，`console.warn` 一行 |
| 页面（加载时） | 模型 404、解析失败、文件里没有网格 | 退回程序化的那一件，`console.warn` 一行（`falls back to the procedural asset`） |
| 页面（`rig` 绑骨时） | 找不到手臂（不是 A-pose）、找不到脖子或肩、模型太小 | 同上，原因写在那一行里 |

外部模型到之前，城先用程序化的画出来；模型取到了再整城重建一次（地块、人物、时间线都不变）。

## 8. 怎样确认换上了

- 日志：`Polis asset manifest …: N asset(s) replaced by external models`；被拒的条目各有一行 Warning。
- 在城里看：换掉的那一类建筑 / 那件器物 / 那个人物变成了你的模型。
- 冒烟测试 `Tools/GameMode/web/test/smoke.mjs` 的 "live, accumulation" 一段就是这样测的：现写一个盒子形状的 GLB 换掉民居（`externalModelUsed`），
  再让双耳瓶指向一个不存在的文件（`missingModelFallsBack`），页面没有报错。盒子带一张内嵌在 GLB 里的贴图，
  检查它真的读进了材质（`externalTextureLoaded`）：页面的内容安全策略少了 `connect-src blob:`，GLTFLoader 读不到内嵌贴图，
  外部模型照样换上、只是一片白，只在控制台留一行错误——2026-10-11 之前就是这样，那时的盒子没有贴图，测不出来。
  雅典娜换成一个 A-pose 人台、蒙在程序化骨架上（`externalFigureOnProceduralRig`）。

## 9. 建议

- 三角面数：建筑 2 万以内、人物 1.5 万以内、器物和植物几千以内——城里同时画几十座建筑和十几个人。
- 贴图用 2 的幂尺寸（512、1024），能用 `.glb` 就用 `.glb`。
- 人物的动画片段按真实的步速做：程序化的人走一步约 0.65 秒，雅典娜一步 0.5 秒（步幅 1.6 米、3.2 米 / 秒）。
- 文生 3D 出来的模型通常很重（第一个雅典娜：50 万三角面、三张 4096 贴图、43.6 MB），先减面、缩贴图再放进来：
  `npx @gltf-transform/cli weld in.glb w.glb`、`npx @gltf-transform/cli simplify w.glb s.glb --ratio 0.12 --error 0.002`、
  `npx @gltf-transform/cli resize s.glb out.glb --width 2048 --height 2048`（得到 6 万面、13.3 MB，游戏视距下看不出差别）。
  别用 `optimize`：它缺省加 meshopt 压缩，页面的 GLTFLoader 没配解码器，读不出来。
