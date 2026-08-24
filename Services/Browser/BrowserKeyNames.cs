using System;
using System.Globalization;

namespace Athena.UI.Services.Browser;

/// <summary>
/// send_keys 的键名归一化。单独成类是为了能被无头测试直接断言：这段逻辑的价值全在
/// "模型会怎么写键名"这一堆字符串映射上，藏在浏览器服务里就只能靠跑真浏览器才能验证。
/// </summary>
public static class BrowserKeyNames
{
    /// <summary>
    /// 把模型写出的键名归一到 Playwright 认的写法。模型给的是 "ENTER"/"esc"/"ctrl+a" 这类
    /// 自然写法，原样透传只会换来 <c>Unknown key: "ENTER"</c> ——一次纯粹的拼写失败要烧掉一整步，
    /// 而这一步本可以是提交表单的那一步。认不出来的键名原样放行，交给 Playwright 报错。
    /// </summary>
    public static string Normalize(string? key)
    {
        var raw = (key ?? string.Empty).Trim();
        if (raw.Length == 0) return raw;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return raw;

        var normalized = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var isModifier = i < parts.Length - 1;
            normalized[i] = isModifier ? NormalizeModifier(parts[i]) : NormalizeKeyName(parts[i]);
        }

        return string.Join("+", normalized);
    }

    private static string NormalizeModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Control",
        "cmd" or "command" or "meta" or "win" or "super" => "Meta",
        "ctrlormeta" or "cmdorctrl" or "ctrlorcmd" or "mod" => "ControlOrMeta",
        "alt" or "opt" or "option" => "Alt",
        "shift" => "Shift",
        _ => part
    };

    private static string NormalizeKeyName(string part)
    {
        // 单个字符是字面量：Playwright 要的就是 "a"/"A"/"7"，大小写在这里有语义，不能动。
        if (part.Length == 1) return part;

        var lower = part.ToLowerInvariant();
        var known = lower switch
        {
            "enter" or "return" or "cr" => "Enter",
            "esc" or "escape" => "Escape",
            "tab" => "Tab",
            "space" or "spacebar" => "Space",
            "backspace" or "bksp" => "Backspace",
            "delete" or "del" => "Delete",
            "insert" or "ins" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pageup" or "pgup" or "page_up" => "PageUp",
            "pagedown" or "pgdn" or "page_down" => "PageDown",
            "up" or "arrowup" or "arrow_up" => "ArrowUp",
            "down" or "arrowdown" or "arrow_down" => "ArrowDown",
            "left" or "arrowleft" or "arrow_left" => "ArrowLeft",
            "right" or "arrowright" or "arrow_right" => "ArrowRight",
            _ => null
        };

        if (known != null) return known;

        // F1–F24。
        if (lower.Length is 2 or 3 && lower[0] == 'f' && int.TryParse(lower[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return "F" + fn.ToString(CultureInfo.InvariantCulture);
        }

        return part;
    }
}
