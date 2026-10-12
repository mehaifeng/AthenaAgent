# 玩家角色（女性）参考图

给图生 3D 用的三视图，由 `generate.mjs` 生成（`node generate.mjs`，再用 `magick -background white X.svg X.png` 出 PNG）。

| 文件 | 用途 |
|---|---|
| `front.png` | **单图生 3D 只喂这一张** |
| `front.png` + `side.png` + `back.png` | 支持多视图输入的模型（同一比例、同一地平线，1 px = 1 mm） |
| `sheet.png` | 只给人看的对照表，带标注——不要喂给模型，文字和虚线会被当成几何 |

## 设定

- 年轻成年女性，身高 1.70 m，古希腊平民装束：
  - 及小腿中段的本色亚麻希顿（爱奥尼亚式扣袖，袖到肘上），腰间一根细绳带；
  - 深蓝灰兜帽披肩（游戏里 `playerCloth` 的 `#4f6690`），胸前一枚铜扣，背后一条长尾巴垂到小腿；
  - 深棕色中分长发，被兜帽包着；皮质系带凉鞋。
- 没有赤陶红（那只属于雅典娜），身上也没有光。

## 为什么要画成这样（bind.mjs 的硬性要求）

- **A-pose**，手臂离竖直方向 38°，**腋下以下和躯干之间留出空当**——绑骨时要靠这个空当找手臂；
- 披肩在腋下就收住，背后的尾巴**不比躯干宽**——否则从正面投影过去，空当会被填掉；
- 两脚分开，裙摆停在小腿中段，两条腿露在外面。

## 配合用的文字提示（模型同时接受图和文字时）

> Full-body 3D character, young adult woman from ancient Greece, standing in an A-pose with arms held clearly away from the body, feet apart.
> Knee-to-calf-length undyed cream linen chiton with short buttoned sleeves, thin brown cord belt. Slate-blue hooded shoulder cape with a bronze clasp, long narrow tail down the back.
> Dark brown centre-parted hair inside the hood, brown leather strap sandals. Realistic proportions, neutral expression, plain background, no props, no weapons.

## 导入游戏时

生成出来的模型面数和贴图都要先压一压（参考雅典娜那次：weld + simplify 到 ~60k 三角形、贴图缩到 2048），
再在 `AthenaData/Game/assets/manifest.json` 里用 `"rig": "procedural"` 挂到 `player` 上。
详见 `Docs/GameMode_AssetManifest_CN.md`。
