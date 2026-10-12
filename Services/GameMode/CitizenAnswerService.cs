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
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.GameMode;

/// <summary>问一位居民的结局。</summary>
public enum CitizenAnswerStatus
{
    /// <summary>他答了（<see cref="CitizenAnswer.Text"/>）。</summary>
    Ok,
    /// <summary>陪伴角色和标题生成角色都没配模型。</summary>
    NoModel,
    /// <summary>上一个问题还在路上（同一时刻只问一位）。</summary>
    Busy,
    /// <summary>这一小时问得太多了（<see cref="CitizenAnswerService.MaxCallsPerHour"/>）。</summary>
    Limited,
    /// <summary>超时、网络、供应商报错，或者答了个空——他想了想，没说出来。</summary>
    Failed
}

public sealed record CitizenAnswer(CitizenAnswerStatus Status, string? Text = null);

/// <summary>问话的材料：那位居民、他的名字、那场会话的消息（快照）、问题、界面语言。</summary>
public sealed record CitizenQuestion(
    PolisCitizen Citizen,
    string CitizenName,
    IReadOnlyList<Athena.UI.Models.ChatMessage> Messages,
    string Question,
    string Language);

/// <summary>问一位居民那场会话的事（M-C）。实现不碰 Avalonia；游戏的视图模型在线程池上调用、回 UI 线程推给页面。</summary>
public interface ICitizenAnswerService
{
    Task<CitizenAnswer> AskAsync(CitizenQuestion question, CancellationToken cancellationToken = default);
}

/// <summary>
/// 问话走陪伴角色（没配就借标题生成角色——和宠物台词同一条回退，两者都是一句话、低延迟、便宜的调用）。
///
/// 纪律：
/// 1. <b>永远没有看得见的失败</b>：没配模型、正忙、超限、超时、报错都只是一个状态，页面让居民说一句"想不起来"一类的话；
///    失败只记 Debug（没配模型这件事每个进程只记一次 Information）。
/// 2. <b>只在你问的时候发</b>：问话是你按 E、写下问题、回车的那一下才有的调用；闲聊永远是本地台词。
///    发出去的是那一场会话里挑出来的几段话（<see cref="PolisCitizenAnswers.SelectExcerpts"/>，≤ 6000 字）与你的问题。
/// 3. <b>限流</b>：同一时刻只问一位（单飞），每小时最多 <see cref="MaxCallsPerHour"/> 次——用户主动发起，所以不设最短间隔。
/// 4. 不发 temperature：辅助角色一律用供应商的缺省（根 CLAUDE.md 的 Unified Model Roles，推理模型会直接拒收这个参数）。
/// </summary>
public sealed class CitizenAnswerService : ICitizenAnswerService, IDisposable
{
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);
    public const int MaxCallsPerHour = 30;
    /// <summary>
    /// 一到三句话用不了这么多；留出来的是推理模型的思考——自动审批的评判员 256 的上限被推理吃光过（根 CLAUDE.md 的 Judge Output 一节），
    /// 一个被截断的回答在这里只会是半句话。
    /// </summary>
    public const int MaxOutputTokens = 1024;
    public const int MaxAnswerChars = 1200;

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;
    private readonly PipelineTransport? _transport;
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recentCalls = new();
    private int _noModelLogged;

    public CitizenAnswerService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger)
        : this(modelFactory, clock, logger, transport: null)
    {
    }

    private CitizenAnswerService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger, PipelineTransport? transport)
    {
        _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<CitizenAnswerService>();
        _transport = transport;
    }

    /// <summary>测试用：网络换成给定的传输（真实 SDK 的序列化与解析照走，只是不出本机）。</summary>
    internal static CitizenAnswerService CreateWithTransport(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, ILogger logger, PipelineTransport transport)
        => new(modelFactory, clock, logger, transport ?? throw new ArgumentNullException(nameof(transport)));

    public async Task<CitizenAnswer> AskAsync(CitizenQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!TryResolveRole(out var role, out var effective))
        {
            if (Interlocked.Exchange(ref _noModelLogged, 1) == 0)
                _logger.Information("Citizen answers need a model: neither the Companion nor the TitleGeneration role is configured");
            return new CitizenAnswer(CitizenAnswerStatus.NoModel);
        }
        if (!await _inFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new CitizenAnswer(CitizenAnswerStatus.Busy);
        try
        {
            if (!TryReserveSlot()) return new CitizenAnswer(CitizenAnswerStatus.Limited);
            var excerpts = PolisCitizenAnswers.SelectExcerpts(question.Messages, question.Question);
            var c = question.Citizen;
            var prompt = PolisCitizenAnswers.BuildPrompt(question.CitizenName, c.Title, c.StartedAt, c.Folders, excerpts, question.Question, question.Language);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AnswerTimeout);
            string? text;
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds, _transport);
                var options = ResponsesCallHelpers.CreateOptions(effective, prompt.System, (float?)effective.Temperature, MaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, [new UserChatMessage(prompt.User)]);
                var result = await responses.CreateResponseAsync(options, timeout.Token).ConfigureAwait(false);
                text = ResponsesCallHelpers.GetConcatenatedOutputText(result.Value);
            }
            else
            {
                // 与 OpenAiModelRuntimeFactory.CreateChatClient 同一套选项（重试、超时、流清洗），直接用上面解析好的 effective 构造
                // （不再读第二次配置，也就不会和那次解析读到不同的模型）——和自动审批的评判员同一个做法
                var clientOptions = OpenAiClientOptionsFactory.Create(effective.BaseUrl, _modelFactory.TimeoutSeconds);
                if (_transport != null) clientOptions.Transport = _transport;
                var client = new OpenAIClient(new ApiKeyCredential(effective.ApiKey), clientOptions).GetChatClient(effective.Model);
                var completion = await client.CompleteChatAsync(
                    [new SystemChatMessage(prompt.System), new UserChatMessage(prompt.User)],
                    new ChatCompletionOptions { Temperature = (float?)effective.Temperature, MaxOutputTokenCount = MaxOutputTokens },
                    timeout.Token).ConfigureAwait(false);
                text = string.Concat(completion.Value.Content.Select(p => p.Text));
            }
            var answer = PolisCitizenAnswers.Sanitize(ConversationTitleGenerator.StripReasoning(text), MaxAnswerChars);
            if (answer == null)
            {
                _logger.Debug("Citizen answer model returned nothing usable ({Role})", role);
                return new CitizenAnswer(CitizenAnswerStatus.Failed);
            }
            return new CitizenAnswer(CitizenAnswerStatus.Ok, answer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("Citizen answer timed out after {Timeout}", AnswerTimeout);
            return new CitizenAnswer(CitizenAnswerStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Citizen answer model call failed");
            return new CitizenAnswer(CitizenAnswerStatus.Failed);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>每小时上限（公开给测试：不真发请求也能断言这一条）。</summary>
    public bool TryReserveSlot()
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            while (_recentCalls.Count > 0 && now - _recentCalls.Peek() > TimeSpan.FromHours(1)) _recentCalls.Dequeue();
            if (_recentCalls.Count >= MaxCallsPerHour) return false;
            _recentCalls.Enqueue(now);
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
                _logger.Debug(ex, "Failed to resolve the {Role} model for citizen answers", candidate);
            }
        }
        role = AiModelRole.Companion;
        effective = default;
        return false;
    }

    public void Dispose() => _inFlight.Dispose();
}
