using System;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 生成暴露给主模型的 MCP 工具名。
/// - 规则：<c>mcp__{server}__{tool}</c>，非字母数字下划线替换为 <c>_</c>。
/// - OpenAI 函数名限制 64 字符：超长时截断中段，尾部附 8 位稳定哈希，避免不同工具撞名。
/// </summary>
public static class McpToolNameEncoder
{
    public const int MaxLength = 64;
    public const string Prefix = "mcp__";

    public static string Encode(string server, string tool)
    {
        var safeServer = Slugify(server);
        var safeTool = Slugify(tool);
        var candidate = $"{Prefix}{safeServer}__{safeTool}";

        if (candidate.Length <= MaxLength)
        {
            return candidate;
        }

        // 8 位哈希兜底：基于原始（未截断）候选生成，保证不同工具即使前缀相同也稳定不撞名。
        var hash = ShortHash(candidate, 8);
        var reserved = Prefix.Length + 1 + hash.Length; // prefix + '_' + hash
        var available = MaxLength - reserved;
        if (available < 4)
        {
            // 极端场景：名字太长直接返回 prefix+hash
            return $"{Prefix}{hash}";
        }

        var body = $"{safeServer}__{safeTool}";
        var truncated = body.Substring(0, Math.Min(body.Length, available));
        return $"{Prefix}{truncated}_{hash}";
    }

    private static string Slugify(string input)
    {
        if (string.IsNullOrEmpty(input)) return "x";
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        // OpenAI 函数名必须以字母/下划线开头
        if (sb.Length == 0) return "x";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string ShortHash(string input, int hexChars)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hexChars);
        for (int i = 0; i < hexChars / 2 && i < bytes.Length; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
