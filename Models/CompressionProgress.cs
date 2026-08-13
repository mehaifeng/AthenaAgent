namespace Athena.UI.Models;

/// <summary>
/// 自动压缩里对用户可见的阶段。规划与校验都是本地瞬时操作，故意不在此列举：
/// 把它们也报给界面只会闪一下，反而像故障。只报真正会让人干等的那两个阶段。
/// </summary>
public enum CompressionProgressPhase
{
    /// <summary>Map：每一段材料对应一次完整的压缩模型调用，是等待的主要来源。</summary>
    Mapping,

    /// <summary>Reduce：把多份 map 摘要合并，最多三层。</summary>
    Reducing,

    /// <summary>压缩已提交，上下文确实变小了。</summary>
    Committed,

    /// <summary>压缩未成功，本轮带原上下文继续。</summary>
    Failed,

    /// <summary>用户主动跳过，本轮带原上下文继续。</summary>
    Skipped
}

/// <summary>
/// 压缩进度快照。分段数在第一次模型调用之前就由 Pack 决定，所以 Index/Total 是真实的——
/// 界面上不需要、也不应该出现假进度。
/// </summary>
public sealed record CompressionProgress(
    CompressionProgressPhase Phase,
    int Index = 0,
    int Total = 0,
    int Depth = 0,
    int MessageCount = 0,
    long TokensBefore = 0,
    long TokensAfter = 0)
{
    public static CompressionProgress Mapping(int index, int total)
        => new(CompressionProgressPhase.Mapping, index, total);

    public static CompressionProgress Reducing(int depth)
        => new(CompressionProgressPhase.Reducing, Depth: depth);

    public static CompressionProgress Committed(int messageCount, long tokensBefore, long tokensAfter)
        => new(
            CompressionProgressPhase.Committed,
            MessageCount: messageCount,
            TokensBefore: tokensBefore,
            TokensAfter: tokensAfter);

    public static CompressionProgress Failed() => new(CompressionProgressPhase.Failed);

    public static CompressionProgress Skipped() => new(CompressionProgressPhase.Skipped);
}
