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
    /// 玻璃模式下面板背景画笔的不透明度上限。
    /// 这一道夹取本身是必需的，不是审美选择：PanelTransparency 停在 0 时面板完全不透明，
    /// 不夹的话模糊底图会被面板自身彻底盖住，开关看起来失效。
    /// 0.62 这个具体数字是起始值——往上玻璃感变弱，往下正文对比度变差，真机上再校准。
    /// </summary>
    public const double GlassTintOpacity = 0.62;

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
    /// 面板背景画笔的实际不透明度：玻璃模式在用户滑块之上再夹一道上限。
    /// </summary>
    public static double ResolveTintOpacity(double shellPanelOpacity, bool glassEnabled) =>
        glassEnabled ? System.Math.Min(shellPanelOpacity, GlassTintOpacity) : shellPanelOpacity;
}
