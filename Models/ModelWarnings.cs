using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 模型元数据与上下文策略解析产出的诊断码，以及它们唯一的展示映射。
/// 码本身是给日志、断言和跨语言比对用的稳定标识，永远不要直接显示——
/// 界面一律经 <see cref="Describe"/> 落到 Locale，供应商模型页与上下文检查器共用同一套键，
/// 同一条诊断因此不会出现两种译法，新增码也只需要翻一次。
/// </summary>
public static class ModelWarnings
{
    /// <summary>诊断码对应的 Locale 键前缀（<c>ModelWarning.&lt;码&gt;</c>）。</summary>
    public const string LocaleKeyPrefix = "ModelWarning.";

    /// <summary>没有可靠的元数据来源，上下文窗口按应用默认值假设。</summary>
    public const string UnknownModelAssumption = "UnknownModelAssumption";

    /// <summary>匹配到了 OpenRouter 记录，但记录里缺少该字段。</summary>
    public const string OpenRouterFieldMissing = "OpenRouterFieldMissing";

    /// <summary>人工绑定的 OpenRouter 模型已不在当前目录中。</summary>
    public const string PinnedOpenRouterModelMissing = "PinnedOpenRouterModelMissing";

    /// <summary>OpenRouter 元数据目录已过期（超出 TTL）。</summary>
    public const string OpenRouterCatalogStale = "OpenRouterCatalogStale";

    /// <summary>匹配到的 OpenRouter 模型已被标记下线。</summary>
    public const string OpenRouterModelExpired = "OpenRouterModelExpired";

    /// <summary>配置的上下文上限低于最小可用值，已忽略并回落到模型元数据。</summary>
    public const string InvalidContextCapIgnored = "InvalidContextCapIgnored";

    /// <summary>配置的压缩阈值非正，已忽略并回落到默认阈值。</summary>
    public const string InvalidCompressionThresholdIgnored = "InvalidCompressionThresholdIgnored";

    /// <summary>配置的压缩阈值高于可用输入预算，已按预算截断。</summary>
    public const string CompressionThresholdClamped = "CompressionThresholdClamped";

    /// <summary>配置的上下文上限高于模型自身窗口，已按模型窗口截断（该设定不生效）。</summary>
    public const string ContextCapClampedToModel = "ContextCapClampedToModel";

    /// <summary>全部已登记的诊断码；新增常量必须同步登记，测试会比对两者一致。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UnknownModelAssumption,
        OpenRouterFieldMissing,
        PinnedOpenRouterModelMissing,
        OpenRouterCatalogStale,
        OpenRouterModelExpired,
        InvalidContextCapIgnored,
        InvalidCompressionThresholdIgnored,
        CompressionThresholdClamped,
        ContextCapClampedToModel
    ];

    /// <summary>
    /// 把诊断码翻成界面文案。未登记的码原样返回：宁可露出一个码，也不要把一条警告吞掉。
    /// </summary>
    /// <param name="code">解析器产出的诊断码。</param>
    /// <param name="getString">本地化查表函数（键, 缺省值）。</param>
    public static string Describe(string code, Func<string, string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        return string.IsNullOrWhiteSpace(code) ? string.Empty : getString(LocaleKeyPrefix + code, code);
    }
}
