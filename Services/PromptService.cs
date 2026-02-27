using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// Prompt 服务实现
/// </summary>
public class PromptService : IPromptService
{
    private readonly string _promptsFilePath;
    private Dictionary<PromptType, string> _customPrompts = new();

    /// <summary>
    /// 事件：Prompt 被更新
    /// </summary>
    public event EventHandler<PromptType>? PromptUpdated;

    public PromptService()
    {
        _promptsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Athena",
            "prompts.json"
        );
        _ = LoadCustomPromptsAsync();
        Log.Information("Prompt 服务初始化");
    }

    /// <summary>
    /// 获取指定类型的 Prompt
    /// </summary>
    public string GetPrompt(PromptType type)
    {
        // 优先返回自定义 Prompt，否则返回默认值
        if (_customPrompts.TryGetValue(type, out var customPrompt) &&
            !string.IsNullOrWhiteSpace(customPrompt))
        {
            return customPrompt;
        }
        return PromptTemplates.GetPrompt(type);
    }

    /// <summary>
    /// 获取格式化的主动消息 Prompt
    /// </summary>
    public string GetProactiveMessagePrompt(string intent, DateTime currentTime)
    {
        return PromptTemplates.GetProactiveMessagePrompt(intent, currentTime);
    }

    /// <summary>
    /// 重新加载 Prompt
    /// </summary>
    public async Task ReloadAsync()
    {
        _customPrompts.Clear();
        await LoadCustomPromptsAsync();
        foreach (var type in Enum.GetValues<PromptType>())
        {
            PromptUpdated?.Invoke(this, type);
        }
        Log.Information("Prompt 已重新加载");
    }

    /// <summary>
    /// 加载自定义 Prompts
    /// </summary>
    private async Task LoadCustomPromptsAsync()
    {
        if (!File.Exists(_promptsFilePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_promptsFilePath);
            var prompts = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (prompts != null)
            {
                foreach (var kvp in prompts)
                {
                    if (Enum.TryParse<PromptType>(kvp.Key, out var type))
                    {
                        _customPrompts[type] = kvp.Value;
                    }
                }
            }
            Log.Information("加载了 {Count} 个自定义 Prompt", _customPrompts.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载自定义 Prompts 失败");
        }
    }
}
