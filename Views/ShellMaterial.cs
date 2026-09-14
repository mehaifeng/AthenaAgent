namespace Athena.UI.Views;

/// <summary>
/// Shell 面板材质（实心 / 半透明 / 毛玻璃）的全部常量与派生规则。
/// 数值集中在这里而不是散落在 XAML 与 code-behind 里，是因为无头断言要拿同一份数字校验
/// （见 Program.cs 里 "毛玻璃材质" 那一段），注释里的理由才不会和实现走散。
///
/// 关于这些数字的诚实说明：**它们是一组起始值，不是实测结论。**
/// 唯一有结构性依据的是 ResolveTintOpacity 的夹取（不夹，开关就等于没开）和
/// GlassBackdropDecodeWidth 选择"升采样而非 BlurEffect"的理由。其余几个透明度
/// 只是第一版的取值，需要在真机上对着深/浅两套主题和 5 套配色方案逐一看过再定。
/// 调这些值不需要先推翻什么——它们还没有任何测量撑着。
/// </summary>
internal static class ShellMaterial
{
    /// <summary>
    /// 深色主题下面板背景画笔的不透明度上限。
    /// 这一道夹取本身是必需的，不是审美选择：PanelTransparency 停在 0 时面板完全不透明，
    /// 不夹的话模糊底图会被面板自身彻底盖住，开关看起来失效。
    /// </summary>
    public const double GlassTintOpacityDark = 0.62;

    /// <summary>
    /// 浅色主题下面板背景画笔的不透明度上限，明显高于深色。
    /// 这一条不是取值而是看图定的：0.62 在浅色下会让左侧会话树的文字掉到读不动
    /// （深色下同样的 0.62 完全没问题）。原因是浅色正文是深色字压浅底，
    /// 底图透上来抬高的是"底"的亮度方差，直接吃掉字的对比度；
    /// 深色正文是浅色字压深底，同样的方差反而被字的亮度盖住。
    /// 也就是说这个不对称是必然的，不是随手调的数字——改浅色这一档前先截图看文字。
    /// </summary>
    public const double GlassTintOpacityLight = 0.80;

    /// <summary>
    /// 玻璃模式下模糊底图层的不透明度。比清晰底图（0.24）高，是因为模糊会把对比度压掉，
    /// 保持同样的透出强度需要更高的不透明度。0.55 是起始值。
    /// </summary>
    public const double GlassBackdropOpacity = 0.55;

    /// <summary>
    /// 玻璃模式下清晰底图层压到的不透明度：留一点纹理，但不要和模糊层叠成双影。
    /// 0.08 是起始值；真正的约束只是"必须明显低于 BaseImageOpacity"。
    /// </summary>
    public const double GlassBaseImageOpacity = 0.08;

    /// <summary>非玻璃模式下清晰底图层的不透明度。与改动前的 XAML 字面值一致，不要改。</summary>
    public const double BaseImageOpacity = 0.24;

    /// <summary>
    /// 模糊底图的解码宽度。刻意不用 Effect="blur(n)"：那是每帧一次全窗口离屏渲染，
    /// 而底图是静态的，没有理由付这个钟。解码到窄位图再让 UniformToFill 升采样铺满窗口
    /// （HighQuality 插值 = mipmap + 三次卷积）得到的就是一层平滑模糊，运行期成本为零。
    /// 96px 对 ~1440px 宽的窗口是 15 倍放大；这个倍率是模糊强度的唯一旋钮——
    /// 数字越大越锐，越小越糊，小到一定程度会开始看出色块。具体阈值没量过。
    /// </summary>
    public const int GlassBackdropDecodeWidth = 96;

    /// <summary>玻璃描边：深色主题下的白色高光透明度（起始值）。</summary>
    public const double GlassBorderAlphaDark = 0.16;

    /// <summary>
    /// 玻璃描边：浅色主题下的白色高光透明度（起始值）。
    /// 比深色主题高得多，因为白高光压在浅背景上本就难分辨。
    /// </summary>
    public const double GlassBorderAlphaLight = 0.62;

    /// <summary>
    /// 会话行选中动效跑完所需的时间，也就是「幕布升起」与「换绑」之间要空出的那一段。
    ///
    /// 这一条不是手感调优，是一个必然：Avalonia 的 Transition 由 **UI 线程**的动画时钟驱动，
    /// 而换绑那一拍 UI 线程要冻住 2~3 秒重排整棵气泡树。两件事挨在一起，选中行的
    /// 0.38s 生长动效（Border.row-accent 的 scaleY）和 0.18s 背景淡入就会停在第一帧上，
    /// 直到会话加载完才「啪」地补完——看起来正是「要等对话加载完才算选中」。
    /// 让出这一段，选中态先自己跑完，再去付布局的账。
    ///
    /// 数值必须与 MainWindow.axaml 里 `Border.row-accent` 的 TransformOperationsTransition
    /// 时长保持一致（两者取较长的那条动效）；headless 用例直接读实体行上的 Transition 比对，
    /// 改了一边而没改另一边会当场失败。
    ///
    /// 附带的好处不是附带：连切时这个计时器会被重置，所以按住方向键在列表里走，
    /// 一次气泡树重排都不会发生，直到手停下来。
    /// </summary>
    public static readonly System.TimeSpan RowSelectionSettle = System.TimeSpan.FromMilliseconds(380);

    /// <summary>
    /// 会话切换幕布是**不透明**的，用的是面板自己的底色但不跟随透明度滑块。
    ///
    /// 这一条量过：按滑块走（面板 0.5、幕布叠加合成 0.75）以及退一步取 0.97，
    /// 旧会话的正文都还认得出字——一块让被切走的会话透出来的幕布，正好取消了它唯一的用途。
    /// 滑块管的是**稳态**的面板观感；幕布是一块只活 2~3 秒的过渡面，两者不是一回事。
    /// 色相仍取自 ResolveShellPanelBackgroundColor，所以它读起来是"这块面板暂时实心了"，
    /// 而不是凭空压上来一块外来色板。
    /// </summary>
    public const double VeilTintOpacity = 1.0;

    /// <summary>
    /// 面板背景画笔的实际不透明度：玻璃模式在用户滑块之上再夹一道上限，上限随主题。
    /// </summary>
    public static double ResolveTintOpacity(double shellPanelOpacity, bool glassEnabled, bool isLightTheme) =>
        glassEnabled
            ? System.Math.Min(shellPanelOpacity, isLightTheme ? GlassTintOpacityLight : GlassTintOpacityDark)
            : shellPanelOpacity;
}
