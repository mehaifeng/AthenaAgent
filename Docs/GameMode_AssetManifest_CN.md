# 游戏模式资产清单：把一件程序化资产换成外部模型

> 2026-10-11（M2）。城邦里画的每一件东西缺省都由代码生成（设计稿第 1 节"美术全部由代码生成，保留资产清单接口，
> 任何一件都能单独换成外部模型"）。这份说明讲怎样用一份清单把其中任何一件换成你自己的 glTF 模型，以及换不上时会发生什么。
> 实现：页面 `Assets/Polis/src/art/assets.mjs`（资产表与资产库）、`asset-manifest.mjs`（回退规则，Node 单测）；
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
    "figure.athena":  {
      "model": "athena.glb",
      "animations": { "walk": "Walk", "idle": "Idle", "work": "Work", "deliver": "Give" }
    }
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
| 公共建筑 | `civic.temple`、`civic.tholos`（圆形图书馆）、`civic.forge`、`civic.market`、`civic.gate`、`civic.agora`（广场：委托板、日晷、讲台）、`civic.harbor`、`civic.boat`、`civic.paintedStoa`、`civic.archive`、`civic.vault`、`civic.terrace`（港前石台）、`civic.groveMarker` |
| 器物 | `prop.amphora`、`prop.hydria`、`prop.krater`、`prop.kylix`、`prop.oinochoe`、`prop.lekythos`、`prop.lamp`、`prop.brazier`、`prop.tripod`、`prop.stool`、`prop.chest`、`prop.scrollCase`、`prop.easel`、`prop.herm`、`prop.stele`、`prop.crate`、`prop.basket`、`prop.pedestal`、`prop.statue`；成果：`prop.item-scroll` / `-ledger` / `-painting` / `-answer`（待收下，地上），`prop.display-scroll` / `-ledger` / `-painting` / `-answer`（陈列在底座上） |
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

## 6. 换不上会怎样（缺省回退）

任何一层出问题，那一件都照常用程序化的，城照样画出来：

| 哪里 | 情况 | 结果 |
|---|---|---|
| C#（读清单时） | 不是 JSON、`schema` 不是 1、Id 写法不对、`model` 不是一段 `.glb`/`.gltf` 文件名、文件不在资产文件夹里（或是一个指到文件夹外面的软链）、数字越界、动作名不认识 | 那一条不推给页面；**每条一行 Warning**（日志里 `Polis asset manifest … : …`） |
| 页面（核对时） | Id 不在资产表里、条目形状不对 | 忽略，`console.warn` 一行 |
| 页面（加载时） | 模型 404、解析失败、文件里没有网格 | 退回程序化的那一件，`console.warn` 一行（`falls back to the procedural asset`） |

外部模型到之前，城先用程序化的画出来；模型取到了再整城重建一次（地块、人物、时间线都不变）。

## 7. 怎样确认换上了

- 日志：`Polis asset manifest …: N asset(s) replaced by external models`；被拒的条目各有一行 Warning。
- 在城里看：换掉的那一类建筑 / 那件器物 / 那个人物变成了你的模型。
- 冒烟测试 `Tools/GameMode/web/test/smoke.mjs` 的 "live, accumulation" 一段就是这样测的：现写一个盒子形状的 GLB 换掉民居（`externalModelUsed`），
  再让双耳瓶指向一个不存在的文件（`missingModelFallsBack`），页面没有报错。

## 8. 建议

- 三角面数：建筑 2 万以内、人物 1.5 万以内、器物和植物几千以内——城里同时画几十座建筑和十几个人。
- 贴图用 2 的幂尺寸（512、1024），能用 `.glb` 就用 `.glb`。
- 人物的动画片段按真实的步速做：程序化的人走一步约 0.65 秒，雅典娜一步 0.5 秒（步幅 1.6 米、3.2 米 / 秒）。
