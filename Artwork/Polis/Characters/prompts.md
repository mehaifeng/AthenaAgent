# 人物文生 3D 提示词

每个人物的提示词由三段拼成：**公共风格段 + 人物段 + 姿势段**。公共风格段和姿势段每个人物都一字不改地照抄，只换人物段，这样一批人物的风格才统一。
负面提示词也是所有人物共用一份。

姿势段是给 `Assets/Polis/src/art/bind.mjs` 准备的，不能省：生成的模型要站成 A-pose，手臂和身体之间留出空隙，两脚分开，否则绑不上骨架，游戏会退回程序化的人物。

## 公共风格段

```
Realistic stylized 3D game character, ancient Greek city-state setting around 450 BC. Grounded, natural human anatomy with believable proportions. Hand-painted PBR textures with soft natural colours: undyed linen, wool, leather and bronze, muted and slightly desaturated. Clean readable silhouette, matte fabric with real drape and gravity-driven folds. Calm, dignified, quiet mood.
```

## 姿势段

```
Full body, standing in a strict A-pose: arms straight and held about 40 degrees away from the body with a clear gap between each arm and the torso from the armpit down, palms facing the thighs, fingers straight and separated, thumbs apart. Feet shoulder-width apart, flat on the ground, facing forward. Head level, looking straight ahead. Clothing hangs freely and does not touch or bridge the arms. Single character, no props, no base, no background.
```

## 负面提示词（模型支持时）

```
T-pose, arms down, hands touching body, fused fingers, mitten hands, extra fingers, cape or sleeves connecting arm to torso, crossed legs, feet together, floor-length robe covering the feet, cartoon, chibi, anime, oversized head, exaggerated muscles, armor, weapon, helmet, glowing, red clothing, logo, text, multiple characters, base plate, background scenery
```

## 人物段

### 玩家（你）——女性

```
A young woman in her mid-twenties, about 1.70 m tall, slender but healthy build, gentle natural feminine curves. Oval face with soft features, warm olive Mediterranean skin, calm attentive dark brown eyes with focused gaze, straight nose, natural lips, neutral relaxed expression. Long dark brown hair parted in the centre, loosely tied at the nape, a few strands framing the face. She wears a calf-length undyed cream linen chiton with short sleeves fastened by small bronze buttons along the upper arm, gathered at the waist by a thin brown cord belt. Over it, a slate-blue wool hooded mantle (colour #4f6690): the hood is up but pushed back from the forehead so the face and hairline are fully visible, the mantle covers both shoulders and ends just above the elbows, closed at the chest with a small round bronze clasp, and a narrow tail of the same wool hangs straight down the middle of the back to the calves. Brown leather strap sandals with laces around the ankles. Simple traveller, not noble, not a priestess.
```

**字数受限时的精简版**（约 450 字符，三段合一）：

```
Realistic ancient Greek young woman, mid-20s, slender, olive skin, calm dark brown eyes, long dark brown centre-parted hair. Calf-length cream linen chiton with short buttoned sleeves and thin brown cord belt; slate-blue wool hooded shoulder cape (hood up, face visible) with bronze clasp; brown leather sandals. Strict A-pose, arms 40° from body with clear armpit gap, fingers separated, feet apart. Natural anatomy, soft PBR textures, no props, plain background.
```

## 加新人物

只写人物段，照上面的写法交代：年龄、身高、体型 → 脸和发型 → 从里到外的衣服（颜色写名字，能对上游戏材质表的就带十六进制色值）→ 鞋 → 一句身份定位。
设定要和 `Assets/Polis/src/art/characters.mjs` 的 `CHARACTER_SPECS` 一致；赤陶红只给雅典娜，"发光"也只给雅典娜。
