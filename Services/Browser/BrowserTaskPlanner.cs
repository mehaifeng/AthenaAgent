using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class BrowserTaskPlanner : IBrowserTaskPlanner
{
    private readonly IConfigService _configService;
    private readonly IModelEndpointResolver _endpointResolver;
    private readonly ILogger _logger;

    public BrowserTaskPlanner(IConfigService configService, ILogger logger, IModelEndpointResolver? endpointResolver = null)
    {
        _configService = configService;
        _endpointResolver = endpointResolver ?? CustomModelEndpointResolver.Instance;
        _logger = logger.ForContext<BrowserTaskPlanner>();
    }

    public async Task<BrowserTaskPlan> CreatePlanAsync(BrowserTaskRequest request, CancellationToken cancellationToken = default)
    {
        var fallbackPlan = CreateFallbackPlan(request);
        var config = _configService.Load();
        var effectiveConfig = BrowserAgentModelResolver.Resolve(config, _endpointResolver);
        if (string.IsNullOrWhiteSpace(effectiveConfig.ApiKey) || string.IsNullOrWhiteSpace(effectiveConfig.Model))
        {
            return fallbackPlan;
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(effectiveConfig.BaseUrl);
            }

            var client = new OpenAIClient(new ApiKeyCredential(effectiveConfig.ApiKey), clientOptions);
            var chatClient = client.GetChatClient(effectiveConfig.Model);
            var userPrompt = $"""
                Browser task instruction:
                {request.Instruction}

                Start URL:
                {request.StartUrl ?? "(none)"}
                """;
            var completion = await chatClient.CompleteChatAsync(
                new OpenAI.Chat.ChatMessage[]
                {
                    new SystemChatMessage(BrowserPrompts.TaskPlanningPrompt),
                    new UserChatMessage(userPrompt)
                },
                new ChatCompletionOptions
                {
                    Temperature = 0,
                    MaxOutputTokenCount = Math.Max(800, Math.Min(effectiveConfig.MaxTokens, 2000))
                },
                cancellationToken);

            var content = completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
            var plan = ParsePlan(content, request);
            if (plan.Goals.Count == 0)
            {
                return fallbackPlan;
            }

            EnsureNavigationGoal(plan, request);
            NormalizeGoalIndexes(plan);
            _logger.Information("Browser task plan created. Goals={GoalCount}, Summary={Summary}", plan.Goals.Count, plan.Summary ?? "(none)");
            return plan;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Browser task planning failed; using fallback plan");
            return fallbackPlan;
        }
    }

    private static BrowserTaskPlan ParsePlan(string content, BrowserTaskRequest request)
    {
        var json = ExtractJson(content);
        var dto = JsonSerializer.Deserialize<PlannerResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var plan = new BrowserTaskPlan { Summary = NullIfWhiteSpace(dto?.Summary) ?? "Browser task plan." };
        foreach (var goalDto in dto?.Goals ?? new List<PlannerGoal>())
        {
            var goalType = NormalizeGoalType(goalDto.Kind);
            if (goalType == null)
            {
                continue;
            }

            var goal = new BrowserTaskGoal
            {
                Type = goalType.Value,
                Label = NullIfWhiteSpace(goalDto.Label) ?? goalType.Value.ToString(),
                Value = NullIfWhiteSpace(goalDto.Value),
                Url = NormalizeUrl(goalDto.Url),
                Checked = goalDto.Checked,
                Optional = goalDto.Optional ?? false,
                MaxAttempts = 2
            };

            if (goal.Type == BrowserTaskGoalType.Navigate)
            {
                goal.Url ??= NormalizeUrl(request.StartUrl) ?? ExtractFirstUrl(request.Instruction);
                goal.Label = string.IsNullOrWhiteSpace(goal.Label) || string.Equals(goal.Label, "Navigate", StringComparison.OrdinalIgnoreCase)
                    ? goal.Url ?? "Open page"
                    : goal.Label;
            }

            plan.Goals.Add(goal);
        }

        return plan;
    }

    private static BrowserTaskPlan CreateFallbackPlan(BrowserTaskRequest request)
    {
        var plan = new BrowserTaskPlan { Summary = "Fallback browser task plan." };
        var initialUrl = NormalizeUrl(request.StartUrl) ?? ExtractFirstUrl(request.Instruction);
        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            plan.Goals.Add(new BrowserTaskGoal
            {
                Type = BrowserTaskGoalType.Navigate,
                Label = initialUrl,
                Url = initialUrl
            });
        }

        var instruction = request.Instruction;
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Text input", "Athena text test", "text input", "文本输入", "文本框");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Password", "AthenaPassword123!", "password", "密码");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Textarea", "Athena textarea test line", "textarea", "text area", "文本域", "多行文本");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Select, "Dropdown select", null, "dropdown select", "selectbox", "select box", "下拉框", "下拉列表");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Dropdown datalist", null, "datalist", "data list", "自动完成");

        var filePath = ExtractFilePath(instruction);
        if (!string.IsNullOrWhiteSpace(filePath) || ContainsAny(instruction, "file input", "upload", "上传", "文件选择"))
        {
            plan.Goals.Add(new BrowserTaskGoal
            {
                Type = BrowserTaskGoalType.Upload,
                Label = "File input",
                Value = filePath
            });
        }

        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.SetChecked, "Default checkbox", "true", "checkbox", "check box", "复选框", "勾选");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.SetChecked, "Default radio", "true", "radio", "单选");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Color picker", null, "color picker", "color", "颜色");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Date picker", null, "date picker", "date", "日期");
        AddGoalIfMentioned(plan, instruction, BrowserTaskGoalType.Fill, "Example range", null, "range", "slider", "滑块");

        if (ContainsAny(instruction, "submit", "提交"))
        {
            plan.Goals.Add(new BrowserTaskGoal { Type = BrowserTaskGoalType.Submit, Label = "Submit" });
        }

        if (plan.Goals.Count == 0 || ContainsAny(instruction, "extract", "summarize", "summary", "读取", "总结", "汇总", "测试"))
        {
            plan.Goals.Add(new BrowserTaskGoal { Type = BrowserTaskGoalType.Extract, Label = "Extract page evidence" });
        }

        EnsureNavigationGoal(plan, request);
        NormalizeGoalIndexes(plan);
        return plan;
    }

    private static void AddGoalIfMentioned(BrowserTaskPlan plan, string instruction, BrowserTaskGoalType type, string label, string? value, params string[] terms)
    {
        if (!ContainsAny(instruction, terms))
        {
            return;
        }

        plan.Goals.Add(new BrowserTaskGoal
        {
            Type = type,
            Label = label,
            Value = value,
            Checked = type == BrowserTaskGoalType.SetChecked ? true : null
        });
    }

    private static void EnsureNavigationGoal(BrowserTaskPlan plan, BrowserTaskRequest request)
    {
        if (plan.Goals.Any(g => g.Type == BrowserTaskGoalType.Navigate))
        {
            return;
        }

        var initialUrl = NormalizeUrl(request.StartUrl) ?? ExtractFirstUrl(request.Instruction);
        if (string.IsNullOrWhiteSpace(initialUrl))
        {
            return;
        }

        plan.Goals.Insert(0, new BrowserTaskGoal
        {
            Type = BrowserTaskGoalType.Navigate,
            Label = initialUrl,
            Url = initialUrl
        });
    }

    private static void NormalizeGoalIndexes(BrowserTaskPlan plan)
    {
        var index = 1;
        foreach (var goal in plan.Goals.Take(30))
        {
            goal.Index = index++;
            goal.GoalId = string.IsNullOrWhiteSpace(goal.GoalId) ? Guid.NewGuid().ToString("N") : goal.GoalId;
            goal.Label = string.IsNullOrWhiteSpace(goal.Label) ? goal.Type.ToString() : goal.Label.Trim();
            goal.Value = NullIfWhiteSpace(goal.Value);
            goal.Url = NormalizeUrl(goal.Url);
            goal.MaxAttempts = Math.Clamp(goal.MaxAttempts, 1, 3);
        }

        if (plan.Goals.Count > 30)
        {
            plan.Goals = plan.Goals.Take(30).ToList();
        }
    }

    private static BrowserTaskGoalType? NormalizeGoalType(string? kind)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "navigate" or "open" or "goto" => BrowserTaskGoalType.Navigate,
            "fill" or "type" or "input" or "set_value" => BrowserTaskGoalType.Fill,
            "select" or "dropdown" or "choose" => BrowserTaskGoalType.Select,
            "upload" or "file" or "file_input" => BrowserTaskGoalType.Upload,
            "set_checked" or "check" or "checkbox" or "radio" => BrowserTaskGoalType.SetChecked,
            "click" => BrowserTaskGoalType.Click,
            "submit" => BrowserTaskGoalType.Submit,
            "extract" or "extract_text" => BrowserTaskGoalType.Extract,
            "verify" or "inspect" or "summarize" => BrowserTaskGoalType.Verify,
            _ => null
        };
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractFirstUrl(string value)
    {
        var match = Regex.Match(value, @"https?://[^\s""'<>，。；、)）\]]+", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeUrl(match.Value) : null;
    }

    private static string? ExtractFilePath(string value)
    {
        var fenced = Regex.Match(value, @"`([^`]+)`");
        if (fenced.Success && LooksLikePath(fenced.Groups[1].Value))
        {
            return fenced.Groups[1].Value.Trim();
        }

        foreach (Match match in Regex.Matches(value, @"(?:/[^\s，。；、]+|[A-Za-z]:\\[^\s，。；、]+)"))
        {
            var candidate = match.Value.Trim();
            var previous = match.Index > 0 ? value[match.Index - 1] : ' ';
            if (candidate.StartsWith('/') && !char.IsWhiteSpace(previous) && previous != '`' && previous != '"' && previous != '\'')
            {
                continue;
            }

            if (LooksLikePath(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/') || value.Contains('\\');

    private static string? NormalizeUrl(string? url)
    {
        var trimmed = NullIfWhiteSpace(url);
        if (trimmed == null)
        {
            return null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        return null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        throw new JsonException("No JSON object found.");
    }

    private sealed class PlannerResponse
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("goals")]
        public List<PlannerGoal>? Goals { get; set; }
    }

    private sealed class PlannerGoal
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("checked")]
        public bool? Checked { get; set; }

        [JsonPropertyName("optional")]
        public bool? Optional { get; set; }
    }
}
