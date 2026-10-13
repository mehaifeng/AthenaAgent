using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.GameMode;

/// <summary>城里一次说话是谁发起的：各自一个池子，互不挤占。</summary>
public enum PolisTalkPurpose
{
    /// <summary>你开的口：和一位居民聊天、在街上说话有人接（你按回车的那一下）。</summary>
    Player,
    /// <summary>城里的人自己聊（两位居民碰上了）。用户定的上限：每小时 60 次（2026-10-13）。</summary>
    Ambient,
    /// <summary>辩论时围观的人起哄、喝彩、嘘。</summary>
    Crowd
}

/// <summary>一次说话的结局。</summary>
public enum PolisTalkStatus
{
    /// <summary>说了（<see cref="PolisTalkReply.Text"/>）。</summary>
    Ok,
    /// <summary>陪伴角色和标题生成角色都没配模型。</summary>
    NoModel,
    /// <summary>这个池子上一句还在路上（同一时刻每个池子只说一句）。</summary>
    Busy,
    /// <summary>这个池子这一小时说得太多了。</summary>
    Limited,
    /// <summary>超时、网络、供应商报错，或者说了个空。</summary>
    Failed
}

public sealed record PolisTalkReply(PolisTalkStatus Status, string? Text = null);

/// <summary>城里的人说话（2026-10-13）。实现不碰 Avalonia；游戏的视图模型在线程池上调用，看得见的字边收边回调，回 UI 线程推给页面。</summary>
public interface IPolisTalkService
{
    /// <param name="onVisible">流式输出到此刻为止看得见的字（<see cref="PolisTalk.VisibleSoFar"/>，变了才叫）；可能在线程池上叫。</param>
    Task<PolisTalkReply> TalkAsync(PolisTalkPurpose purpose, PolisTalkPrompt prompt, Action<string>? onVisible = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 城里的人说话都走这里：陪伴角色（没配就借标题生成角色——和宠物台词、原来的问话同一条回退），两种协议都流式。
///
/// 纪律：
/// 1. <b>永远没有看得见的失败</b>：没配模型、正忙、超限、超时、报错都只是一个状态，页面让说话的人说一句"想不起来"一类的话，
///    或者干脆不说（闲聊）；失败只记 Debug（没配模型这件事每个进程只记一次 Information）。
/// 2. <b>按用途分池限流</b>：每个池子同一时刻只说一句（单飞），每小时有上限（<see cref="MaxCallsPerHour"/>）。
///    你开的口不设最短间隔；城里的闲聊由页面挑人、挑时机（一对一对来，见 citizens.mjs），这里只守总数。
/// 3. 不发 temperature：辅助角色一律用供应商的缺省（根 CLAUDE.md 的 Unified Model Roles）。
/// 4. 输出上限 <see cref="MaxOutputTokens"/>：一句话用不了这么多，留出来的是推理模型的思考（自动审批的评判员 256 被推理吃光过）。
/// </summary>
public sealed class PolisTalkService : IPolisTalkService, IDisposable
{
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(25);
    public const int MaxOutputTokens = 1024;

    /// <summary>每个池子每小时最多几次。</summary>
    public static int MaxCallsPerHour(PolisTalkPurpose purpose) => purpose switch
    {
        PolisTalkPurpose.Player => 60,
        PolisTalkPurpose.Ambient => 60,
        _ => 30,
    };

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;
    private readonly PipelineTransport? _transport;
    private readonly Dictionary<PolisTalkPurpose, SemaphoreSlim> _inFlight = Enum.GetValues<PolisTalkPurpose>().ToDictionary(p => p, _ => new SemaphoreSlim(1, 1));
    private readonly Dictionary<PolisTalkPurpose, Queue<DateTimeOffset>> _recentCalls = Enum.GetValues<PolisTalkPurpose>().ToDictionary(p => p, _ => new Queue<DateTimeOffset>());
    private readonly object _gate = new();
    private int _noModelLogged;

    public PolisTalkService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger)
        : this(modelFactory, clock, logger, transport: null)
    {
    }

    private PolisTalkService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger, PipelineTransport? transport)
    {
        _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisTalkService>();
        _transport = transport;
    }

    /// <summary>测试用：网络换成给定的传输（真实 SDK 的序列化与解析照走，只是不出本机）。</summary>
    internal static PolisTalkService CreateWithTransport(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger, PipelineTransport transport)
        => new(modelFactory, clock, logger, transport ?? throw new ArgumentNullException(nameof(transport)));

    public async Task<PolisTalkReply> TalkAsync(PolisTalkPurpose purpose, PolisTalkPrompt prompt, Action<string>? onVisible = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!TryResolveRole(out var role, out var effective))
        {
            if (Interlocked.Exchange(ref _noModelLogged, 1) == 0)
                _logger.Information("Polis talk needs a model: neither the Companion nor the TitleGeneration role is configured");
            return new PolisTalkReply(PolisTalkStatus.NoModel);
        }
        var gate = _inFlight[purpose];
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new PolisTalkReply(PolisTalkStatus.Busy);
        try
        {
            if (!TryReserveSlot(purpose)) return new PolisTalkReply(PolisTalkStatus.Limited);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReplyTimeout);
            var raw = new StringBuilder();
            var shown = string.Empty;
            void Append(string? delta)
            {
                if (string.IsNullOrEmpty(delta)) return;
                raw.Append(delta);
                if (onVisible == null) return;
                var visible = PolisTalk.VisibleSoFar(raw.ToString());
                if (visible.Length > PolisTalk.MaxBubbleChars) visible = visible[..PolisTalk.MaxBubbleChars];
                if (visible == shown) return;
                shown = visible;
                onVisible(visible);
            }

            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds, _transport);
                var options = ResponsesCallHelpers.CreateOptions(effective, prompt.System, (float?)effective.Temperature, MaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, prompt.Messages.Select(ToChatMessage).ToList());
                var (text, _) = await ResponsesCallHelpers.StreamOutputTextAsync(responses, options, timeout.Token, Append).ConfigureAwait(false);
                // 有的端点只在终局事件里给全文，不发增量
                if (raw.Length == 0) Append(text);
            }
            else
            {
                // 与 OpenAiModelRuntimeFactory.CreateChatClient 同一套选项（重试、超时、流清洗），直接用上面解析好的 effective 构造
                // （不读第二次配置，也就不会和那次解析读到不同的模型）——和自动审批的评判员、原来的问话同一个做法
                var clientOptions = OpenAiClientOptionsFactory.Create(effective.BaseUrl, _modelFactory.TimeoutSeconds);
                if (_transport != null) clientOptions.Transport = _transport;
                var client = new OpenAIClient(new ApiKeyCredential(effective.ApiKey), clientOptions).GetChatClient(effective.Model);
                var messages = new List<OpenAI.Chat.ChatMessage> { new SystemChatMessage(prompt.System) };
                messages.AddRange(prompt.Messages.Select(ToChatMessage));
                var stream = client.CompleteChatStreamingAsync(
                    messages,
                    new ChatCompletionOptions { Temperature = (float?)effective.Temperature, MaxOutputTokenCount = MaxOutputTokens },
                    timeout.Token);
                await foreach (var update in stream.ConfigureAwait(false))
                {
                    foreach (var part in update.ContentUpdate) Append(part.Text);
                }
            }

            var answer = PolisCitizenAnswers.Sanitize(ConversationTitleGenerator.StripReasoning(raw.ToString()), PolisTalk.MaxBubbleChars);
            if (answer == null)
            {
                _logger.Debug("Polis talk model returned nothing usable ({Role}, {Purpose})", role, purpose);
                return new PolisTalkReply(PolisTalkStatus.Failed);
            }
            return new PolisTalkReply(PolisTalkStatus.Ok, answer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("Polis talk timed out after {Timeout} ({Purpose})", ReplyTimeout, purpose);
            return new PolisTalkReply(PolisTalkStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Polis talk model call failed ({Purpose})", purpose);
            return new PolisTalkReply(PolisTalkStatus.Failed);
        }
        finally
        {
            gate.Release();
        }
    }

    private static OpenAI.Chat.ChatMessage ToChatMessage((string Role, string Text) m)
        => m.Role == "assistant" ? new AssistantChatMessage(m.Text) : new UserChatMessage(m.Text);

    /// <summary>每小时上限（公开给测试：不真发请求也能断言这一条）。</summary>
    public bool TryReserveSlot(PolisTalkPurpose purpose)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            var recent = _recentCalls[purpose];
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromHours(1)) recent.Dequeue();
            if (recent.Count >= MaxCallsPerHour(purpose)) return false;
            recent.Enqueue(now);
            return true;
        }
    }

    private bool TryResolveRole(out AiModelRole role, out EffectiveOpenAiModel effective)
    {
        foreach (var candidate in new[] { AiModelRole.Companion, AiModelRole.TitleGeneration })
        {
            try
            {
                var resolved = _modelFactory.Resolve(candidate);
                resolved.ValidateChatRole(candidate);
                role = candidate;
                effective = resolved;
                return true;
            }
            catch (InvalidOperationException)
            {
                // 这个角色没配，试下一个
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to resolve the {Role} model for polis talk", candidate);
            }
        }
        role = AiModelRole.Companion;
        effective = default;
        return false;
    }

    public void Dispose()
    {
        foreach (var gate in _inFlight.Values) gate.Dispose();
    }
}
