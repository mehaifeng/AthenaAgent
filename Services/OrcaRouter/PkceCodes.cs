using System;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.OrcaRouter;

/// <summary>
/// 一次授权流程的 PKCE 材料（RFC 7636）与 CSRF 令牌。
///
/// <see cref="State"/> 是唯一能把回调认成"我们发起的那一次"的东西：环回端口上谁都能敲，
/// 只有 state 相符的回调才配拿去换 key。<see cref="Verifier"/> 永远留在本进程内，
/// 只有它的哈希（<see cref="Challenge"/>）会离开机器。
/// </summary>
public sealed record PkceCodes(string Verifier, string Challenge, string State)
{
    /// <summary>只使用 S256；plain 方法等于没有 PKCE。</summary>
    public const string ChallengeMethod = "S256";

    /// <summary>随机材料的字节数。32 字节 → 43 字符 base64url，落在 RFC 7636 的 43–128 区间内。</summary>
    private const int EntropyBytes = 32;

    /// <summary>生成一组全新的 PKCE 材料。</summary>
    public static PkceCodes Create()
        => Create(RandomNumberGenerator.GetBytes(EntropyBytes), RandomNumberGenerator.GetBytes(EntropyBytes));

    /// <summary>用给定熵构造，便于断言确定性的推导关系。</summary>
    internal static PkceCodes Create(ReadOnlySpan<byte> verifierEntropy, ReadOnlySpan<byte> stateEntropy)
    {
        var verifier = Base64Url(verifierEntropy);
        return new PkceCodes(verifier, ComputeChallenge(verifier), Base64Url(stateEntropy));
    }

    /// <summary>challenge = base64url(SHA256(ASCII(verifier)))。</summary>
    public static string ComputeChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>base64url 且不带填充——这些值要进 URL query，'+' '/' '=' 都得走掉。</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
