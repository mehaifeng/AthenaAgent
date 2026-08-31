using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.VirtualPet;

/// <summary>
/// 宠物台词。本地台词库是主线，模型台词是可选增强。
///
/// 三条纪律：
/// 1. <b>永不阻塞表现</b>。气泡先用本地台词显示出来，模型那句到了才替换；请求超时/限流/未配置
///    都只是"没有替换"，用户看不到任何失败。
/// 2. <b>严格限流</b>。最短间隔 + 每小时上限 + 同时只允许一个在途请求。宠物是个装饰件，
///    绝不允许它在后台悄悄产生持续的 API 开销。<b>最短间隔只管后台台词</b>：用户右键点
///    "说句话"是一次明确动作，不该因为二十秒前有一句自动台词就静默失败——限流要挡的是
///    "无人看管的持续开销"，而每小时上限（对谁都生效）才是那道成本上限。
/// 3. <b>最小上下文</b>。只发宠物自己的状态和一小段话题，不发整段对话。
/// </summary>
public sealed class PetChatterService : IPetChatterService, IDisposable
{
    /// <summary>两次模型台词之间的最短间隔。</summary>
    public static readonly TimeSpan MinModelInterval = TimeSpan.FromSeconds(45);

    /// <summary>每小时最多生成多少句。</summary>
    public const int MaxModelCallsPerHour = 20;

    /// <summary>模型台词的硬超时。等更久还不如直接用本地台词。</summary>
    public static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(8);

    /// <summary>台词长度上限（字符）。超出部分截断，宠物气泡不是聊天窗。</summary>
    public const int MaxLineChars = 40;

    /// <summary>随附话题上下文的长度上限。</summary>
    public const int MaxTopicContextChars = 80;

    private readonly OpenAiModelRuntimeFactory? _modelFactory;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private readonly Func<bool> _isEnabled;
    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recentCalls = new();
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly ISystemClock _clock;
    private DateTimeOffset _lastModelCallAt = DateTimeOffset.MinValue;

    public PetChatterService(
        OpenAiModelRuntimeFactory modelFactory,
        ILocalizationService localizationService,
        ISystemClock clock,
        Func<bool> isEnabled,
        ILogger logger)
        : this(modelFactory, localizationService, clock, isEnabled, logger, modelBacked: true)
    {
    }

    private PetChatterService(
        OpenAiModelRuntimeFactory? modelFactory,
        ILocalizationService? localizationService,
        ISystemClock clock,
        Func<bool> isEnabled,
        ILogger logger,
        bool modelBacked)
    {
        _modelFactory = modelFactory;
        _localizationService = localizationService;
        _clock = clock;
        _isEnabled = isEnabled;
        _logger = logger.ForContext<PetChatterService>();
        if (!modelBacked)
        {
            _logger.Debug("Pet chatter constructed without a model factory; local lines only");
        }
    }

    /// <summary>
    /// 设计器 / 测试构造：没有模型工厂，只有本地台词库。
    /// 显式命名的工厂方法，而不是"依赖可空、悄悄不工作"（见 CLAUDE.md「Review Rules」第 1 条）。
    /// </summary>
    public static PetChatterService CreateLocalOnly(
        ILocalizationService? localizationService,
        ILogger logger,
        ISystemClock? clock = null)
        => new(null, localizationService, clock ?? new SystemClock(), () => false, logger, modelBacked: false);

    public bool IsModelChatterAvailable
    {
        get
        {
            if (_modelFactory == null || !_isEnabled()) return false;
            return TryResolveRole(out _, out _);
        }
    }

    public string GetLocalLine(PetChatterTopic topic, PetMoodBand band)
    {
        // 先找情绪档位专属台词，没有就用通用的；两者都缺时退回一个永远存在的兜底键。
        var line = PickVariant($"Pet.Line.{topic}.{band}")
                   ?? PickVariant($"Pet.Line.{topic}")
                   ?? PickVariant($"Pet.Line.{PetChatterTopic.Idle}")
                   ?? "…";
        return line;
    }

    public async Task<string?> TryGenerateAsync(
        PetChatterRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_modelFactory == null || !_isEnabled()) return null;
        if (!TryResolveRole(out var role, out var effective)) return null;
        if (!TryReserveSlot(request.UserRequested)) return null;
        if (!await _inFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTimeout);

            var system = BuildSystemPrompt(request);
            var user = BuildUserPrompt(request);
            string? text;

            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
                var options = ResponsesCallHelpers.CreateOptions(effective, system, (float)effective.Temperature, MaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, [new UserChatMessage(user)]);
                var result = await responses.CreateResponseAsync(options, timeout.Token).ConfigureAwait(false);
                text = ResponsesCallHelpers.GetFirstOutputText(result.Value);
            }
            else
            {
                var client = _modelFactory.CreateChatClient(role);
                var completion = await client.CompleteChatAsync(
                    [new SystemChatMessage(system), new UserChatMessage(user)],
                    new ChatCompletionOptions
                    {
                        Temperature = (float)effective.Temperature,
                        MaxOutputTokenCount = MaxOutputTokens
                    },
                    timeout.Token).ConfigureAwait(false);
                text = completion.Value.Content.FirstOrDefault()?.Text;
            }

            var line = Sanitize(text);
            if (line == null) _logger.Debug("Pet chatter model returned nothing usable");
            return line;
        }
        catch (OperationCanceledException)
        {
            // 超时或调用方取消都不是故障：本地台词已经在气泡里了。
            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pet chatter model call failed; keeping the local line");
            return null;
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>台词很短，给足余量即可；推理型模型的思考预算也走这个上限。</summary>
    private const int MaxOutputTokens = 256;

    /// <summary>
    /// 角色解析：优先用专属的 Companion 角色；没配就借用同样是"小模型场景"的标题生成角色。
    /// 两个都没配就当模型台词不可用。
    /// </summary>
    private bool TryResolveRole(out AiModelRole role, out EffectiveOpenAiModel effective)
    {
        foreach (var candidate in new[] { AiModelRole.Companion, AiModelRole.TitleGeneration })
        {
            try
            {
                var resolved = _modelFactory!.Resolve(candidate);
                resolved.ValidateChatRole(candidate);
                role = candidate;
                effective = resolved;
                return true;
            }
            catch (InvalidOperationException)
            {
                // 该角色未配置，试下一个候选。
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to resolve the {Role} model for pet chatter", candidate);
            }
        }
        role = AiModelRole.Companion;
        effective = default;
        return false;
    }

    /// <summary>
    /// 限流。占用成功才真的发请求；失败时不留下任何痕迹。
    /// <paramref name="userRequested"/> 只豁免最短间隔，不豁免每小时上限——那道才是成本上限。
    /// 公开是为了让测试直接断言这套语义，而不必真的发一次请求（发出去就分不清"被限流"和"调用失败"）。
    /// </summary>
    public bool TryReserveSlot(bool userRequested)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (!userRequested && now - _lastModelCallAt < MinModelInterval) return false;
            while (_recentCalls.Count > 0 && now - _recentCalls.Peek() > TimeSpan.FromHours(1))
                _recentCalls.Dequeue();
            if (_recentCalls.Count >= MaxModelCallsPerHour) return false;
            _recentCalls.Enqueue(now);
            _lastModelCallAt = now;
            return true;
        }
    }

    private string BuildSystemPrompt(PetChatterRequest request)
    {
        var chinese = request.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return chinese
            ? "你是桌面 AI 助手 Athena 界面角落里的一只像素小宠物。用第一人称说一句话，"
              + "不超过 20 个汉字，口语、俏皮、不谄媚。只输出这一句话本身："
              + "不要引号、不要 Markdown、不要表情符号堆砌、不要解释。"
            : "You are a pixel pet living in the corner of the Athena desktop assistant. "
              + "Say exactly one short first-person line of at most 12 words: playful, plain, never fawning. "
              + "Output only that line - no quotes, no Markdown, no emoji spam, no explanation.";
    }

    private static string BuildUserPrompt(PetChatterRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"scene={request.Topic}");
        builder.Append(CultureInfo.InvariantCulture, $"; name={request.PetName}");
        builder.Append(CultureInfo.InvariantCulture, $"; level={request.Level}");
        builder.Append(CultureInfo.InvariantCulture, $"; mood={request.Mood:F0}/100");
        builder.Append(CultureInfo.InvariantCulture, $"; energy={request.Energy:F0}/100");
        if (request.ActiveNeed != PetNeedKind.None)
            builder.Append(CultureInfo.InvariantCulture, $"; want={request.ActiveNeed}");
        if (!string.IsNullOrWhiteSpace(request.RecentToolName))
            builder.Append(CultureInfo.InvariantCulture, $"; last_tool={request.RecentToolName}");
        if (!string.IsNullOrWhiteSpace(request.RecentUserText))
            builder.Append(CultureInfo.InvariantCulture, $"; topic={Shorten(request.RecentUserText, MaxTopicContextChars)}");
        return builder.ToString();
    }

    private string? PickVariant(string key)
    {
        // 台词库用 '|' 分隔同一场景的多个变体，这样一个场景只占一条本地化资源。
        var raw = _localizationService?.GetString(key, string.Empty);
        if (string.IsNullOrWhiteSpace(raw) || raw == key) return null;
        var variants = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (variants.Length == 0) return null;
        lock (_gate) return variants[_random.Next(variants.Length)];
    }

    /// <summary>
    /// 把模型返回的东西压成一句能进气泡的话：取第一行、剥掉引号与 Markdown、截断。
    /// 模型偶尔会加解释、加引号、加粗，这些直接显示出来就不像宠物在说话了。
    /// </summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var firstLine = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return null;
        var cleaned = firstLine.Trim().Trim('"', '\'', '「', '」', '“', '”', '*', '`', ' ');
        cleaned = cleaned.Replace("**", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cleaned) ? null : Shorten(cleaned, MaxLineChars);
    }

    private static string Shorten(string text, int maxChars)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars) return trimmed;
        var cut = maxChars;
        if (char.IsHighSurrogate(trimmed[cut - 1])) cut--;
        return trimmed[..cut] + "…";
    }

    public void Dispose() => _inFlight.Dispose();
}
