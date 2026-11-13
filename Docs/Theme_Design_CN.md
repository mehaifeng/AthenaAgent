# 主题配色与可读性

九套主题分别提供浅色、深色版本。保留原方案名称与配置兼容性，新增 Nord（北境蓝灰）、Forest（森林绿）、Rose（玫瑰紫）、Sandstone（暖砂色）。明暗模式与配色独立选择，左右换位、工作台展开和侧栏折叠沿用当前布局行为。

## 资源职责

- `Models/AppPalettes.cs`：每种明暗版本的画布、普通容器、浮起容器、正文、次要文字、辅助文字、强调色。它也是设置列表和配置校验的唯一方案来源。
- `Styles/ThemeResources.cs`：将调色板转成完整资源字典，集中维护 Semi 控件、图标、选中/悬停/按下、Markdown、待处理卡片、终端、编辑器和差异视图的映射。每套字典键集、类型一致，不再复制整本 XAML。
- `App.GetColorSchemeDictionaries`：缓存每套明暗字典；`SetColorScheme` 保持幂等并通知已显示的窗口。Default 在应用初始化时同样由工厂创建。
- `Styles/Overlays.axaml`：保留只依赖明暗的交互覆盖层；不要把同色零透明度的过渡起点换成 `Transparent`。
- `MainWindow.ApplyShellPanelMaterial`：继续独占面板材质及气泡透明度。配色字典的背景是原始不透明颜色，窗口生成实际材质。

正文使用 `App.TextPrimary`，描述使用 `App.TextSecondary`，时间戳和帮助文字使用 `App.TextMuted`。辅助文字不再额外设置控件透明度，否则最终颜色会随底色变化。强调色文字与实心强调按钮上的反色文字要分别使用 `App.EmphasisForeground` 和 `App.InverseForeground`。状态提示同时使用配套背景与前景。

终端组件会缓存颜色表，因此在实际配色或明暗变化时重建控件并重新绑定同一个会话模型；普通日志/终端标签切换不重建控件。Markdown 的 `Color` 类型资源与控件的画笔资源分别提供，避免资源类型不匹配。代码高亮通过 `Markdown.CodeTheme` 跟随明暗选择 LightPlus / DarkPlus，代码块使用编辑器底色；链接及已访问链接也使用主题前景色。代码样本的实际高亮文本同样参与对比度断言。编辑区按容器铺满并从顶部排版。

## 验证

定向运行：

```bash
ATHENA_HEADLESS_ONLY=themes bash Scripts/run-headless-tests.sh /tmp/athena-themes/main.png
```

完整 UI 回归：

```bash
bash Scripts/run-headless-tests.sh /tmp/athena-headless/main.png
```

`ThemeContrastTests` 对九套主题的明暗字典检查完整性，以及正文、辅助文字、强调文字、选中态、按钮、待处理状态、代码、终端和差异区等配色对。普通文字按 [WCAG 2.2 对比度规则](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) 的 4.5:1 阈值计算，不对临界值四舍五入后放行。另验证已显示控件的实时换色、终端、主题预览与正常/换位/折叠布局，并输出四套新增主题的窗口截图。

数值断言针对指定的不透明颜色组合，不等于整款应用的无障碍认证。背景图片、玻璃透明度、位图内容和第三方 Office/网页预览仍需按最终合成效果检查；原有原生窗口及玻璃过渡机制保留。无头截图不能替代各操作系统原生窗口的实际使用验收。
