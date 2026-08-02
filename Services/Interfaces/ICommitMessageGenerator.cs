using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>基于已暂存差异生成 Git 提交信息。</summary>
public interface ICommitMessageGenerator
{
    /// <summary>生成提交信息；失败或空结果返回 null，由调用方兜底。</summary>
    Task<string?> GenerateAsync(
        string? branchName,
        string diffStat,
        string diffContent,
        CancellationToken cancellationToken = default);
}
