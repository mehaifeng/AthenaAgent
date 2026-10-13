// OpenAI SDK Experimental 面（OPENAI001）：裁判在 Responses 协议下读 ResponseResult。
#pragma warning disable OPENAI001

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

public enum PolisDebateCallStatus
{
    Ok,
    /// <summary>该用的角色没配模型（雅典娜：主对话；裁判：陪伴与标题生成都没配）。</summary>
    NoModel,
    /// <summary>超时、网络、供应商报错，或者答了个空。</summary>
    Failed
}

public sealed record PolisDebateReply(PolisDebateCallStatus Status, string? Text = null);

public sealed record PolisDebateJudgement(PolisDebateCallStatus Status, PolisDebateVerdict? Verdict = null);

/// <summary>辩论要的两种调用与战绩（M-E）。游戏的视图模型在线程池上调用，回 UI 线程推给页面。</summary>
public interface IPolisDebateService
{
    /// <summary>每小时最多开几场（<see cref="PolisDebateService.MaxDebatesPerHour"/>）：占到了才开。</summary>
    bool TryReserveDebate();

    Task<PolisDebateReply> AthenaRespondAsync(PolisDebateSession session, string language, CancellationToken cancellationToken = default);

    /// <summary><paramref name="noVerdictReason"/>：没有裁决（截断、格式不对）时记在平局上的那句话（本地化）。</summary>
    Task<PolisDebateJudgement> JudgeAsync(PolisDebateSession session, string language, string noVerdictReason, CancellationToken cancellationToken = default);

    Task<PolisContestSummary> LoadSummaryAsync(CancellationToken cancellationToken = default);

    Task<PolisContestSummary> RecordAsync(PolisContestRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// 雅典娜用主对话模型（用户 2026-10-12 定的：辩论用主模型），温度沿用主角色；裁判用陪伴角色，没配就借标题生成角色，不发温度
/// （辅助角色的规矩）。不带任何会话历史、没有工具。每小时最多 <see cref="MaxDebatesPerHour"/> 场——一场是三次主模型调用加一次裁判。
/// 失败都是状态，不抛异常；没配模型每个进程只记一次 Information，其余失败记 Debug（游戏不弹错误框，页面让人物说一句话）。
/// </summary>
public sealed class PolisDebateService : IPolisDebateService, IDisposable
{
    public const int MaxDebatesPerHour = 10;
    /// <summary>她一轮的输出上限：提示词要 120 字，留出来的是推理模型的思考（见 <see cref="CitizenAnswerService.MaxOutputTokens"/> 的理由）。</summary>
    public const int AthenaMaxOutputTokens = 1024;
    public const int JudgeMaxOutputTokens = 1024;
    public static readonly TimeSpan AthenaTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan JudgeTimeout = TimeSpan.FromSeconds(30);

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ISystemClock _clock;
    private readonly PolisContestStore _store;
    private readonly ILogger _logger;
    private readonly PipelineTransport? _transport;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recentDebates = new();
    private int _noAthenaLogged;
    private int _noJudgeLogged;

    public PolisDebateService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, PolisContestStore store, ILogger logger)
        : this(modelFactory, clock, store, logger, transport: null)
    {
    }

    private PolisDebateService(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, PolisContestStore store, ILogger logger, PipelineTransport? transport)
    {
        _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PolisDebateService>();
        _transport = transport;
    }

    /// <summary>测试用：网络换成给定的传输（真实 SDK 的序列化与解析照走）。</summary>
    internal static PolisDebateService CreateWithTransport(OpenAiModelRuntimeFactory modelFactory, ISystemClock clock, PolisContestStore store, ILogger logger, PipelineTransport transport)
        => new(modelFactory, clock, store, logger, transport ?? throw new ArgumentNullException(nameof(transport)));

    public bool TryReserveDebate()
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            while (_recentDebates.Count > 0 && now - _recentDebates.Peek() > TimeSpan.FromHours(1)) _recentDebates.Dequeue();
            if (_recentDebates.Count >= MaxDebatesPerHour) return false;
            _recentDebates.Enqueue(now);
            return true;
        }
    }

    public async Task<PolisDebateReply> AthenaRespondAsync(PolisDebateSession session, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        EffectiveOpenAiModel effective;
        try
        {
            effective = _modelFactory.Resolve(AiModelRole.MainConversation);
            effective.ValidateChatRole(AiModelRole.MainConversation);
        }
        catch (InvalidOperationException)
        {
            if (Interlocked.Exchange(ref _noAthenaLogged, 1) == 0)
                _logger.Information("Debating needs the main conversation model, which is not configured");
            return new PolisDebateReply(PolisDebateCallStatus.NoModel);
        }
        var (system, user) = PolisDebate.AthenaPrompt(session, language);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AthenaTimeout);
            string? text;
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds, _transport);
                var options = ResponsesCallHelpers.CreateOptions(effective, system, (float?)effective.Temperature, AthenaMaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, [new UserChatMessage(user)]);
                var result = await responses.CreateResponseAsync(options, timeout.Token).ConfigureAwait(false);
                text = ResponsesCallHelpers.GetConcatenatedOutputText(result.Value);
            }
            else
            {
                var completion = await ChatClient(effective).CompleteChatAsync(
                    [new SystemChatMessage(system), new UserChatMessage(user)],
                    new ChatCompletionOptions { Temperature = (float?)effective.Temperature, MaxOutputTokenCount = AthenaMaxOutputTokens },
                    timeout.Token).ConfigureAwait(false);
                text = string.Concat(completion.Value.Content.Select(p => p.Text));
            }
            var line = PolisDebate.SanitizeAthena(ConversationTitleGenerator.StripReasoning(text));
            if (line == null)
            {
                _logger.Debug("Athena's debate reply was empty");
                return new PolisDebateReply(PolisDebateCallStatus.Failed);
            }
            return new PolisDebateReply(PolisDebateCallStatus.Ok, line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("Athena's debate reply timed out after {Timeout}", AthenaTimeout);
            return new PolisDebateReply(PolisDebateCallStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Athena's debate reply failed");
            return new PolisDebateReply(PolisDebateCallStatus.Failed);
        }
    }

    public async Task<PolisDebateJudgement> JudgeAsync(PolisDebateSession session, string language, string noVerdictReason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryResolveJudge(out var effective))
        {
            if (Interlocked.Exchange(ref _noJudgeLogged, 1) == 0)
                _logger.Information("Judging a debate needs a model for the Companion or TitleGeneration role; neither is configured");
            return new PolisDebateJudgement(PolisDebateCallStatus.NoModel);
        }
        var (system, user) = PolisDebate.JudgePrompt(session, language);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(JudgeTimeout);
            ApprovalJudgeReply reply;
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds, _transport);
                var options = ResponsesCallHelpers.CreateOptions(effective, system, (float?)effective.Temperature, JudgeMaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, [new UserChatMessage(user)]);
                var result = await responses.CreateResponseAsync(options, timeout.Token).ConfigureAwait(false);
                reply = ApprovalJudgeOutput.FromResponse(result.Value);
            }
            else
            {
                var completion = await ChatClient(effective).CompleteChatAsync(
                    [new SystemChatMessage(system), new UserChatMessage(user)],
                    new ChatCompletionOptions { Temperature = (float?)effective.Temperature, MaxOutputTokenCount = JudgeMaxOutputTokens },
                    timeout.Token).ConfigureAwait(false);
                reply = ApprovalJudgeOutput.FromChatCompletion(completion.Value);
            }
            var verdict = PolisDebate.InterpretVerdict(reply, noVerdictReason);
            if (!verdict.Judged)
                _logger.Debug("The debate judge gave no verdict (status {Status}, truncated {Truncated}); counting a tie", reply.Status, reply.Truncated);
            return new PolisDebateJudgement(PolisDebateCallStatus.Ok, verdict);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("The debate judge timed out after {Timeout}", JudgeTimeout);
            return new PolisDebateJudgement(PolisDebateCallStatus.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "The debate judge call failed");
            return new PolisDebateJudgement(PolisDebateCallStatus.Failed);
        }
    }

    public async Task<PolisContestSummary> LoadSummaryAsync(CancellationToken cancellationToken = default)
        => PolisDebate.Summarize(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<PolisContestSummary> RecordAsync(PolisContestRecord record, CancellationToken cancellationToken = default)
        => PolisDebate.Summarize(await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false));

    public void Dispose() => _store.Dispose();

    /// <summary>和 <see cref="OpenAiModelRuntimeFactory.CreateChatClient(AiModelRole)"/> 同一套选项，直接用解析好的 effective 构造（不读第二次配置）。</summary>
    private ChatClient ChatClient(EffectiveOpenAiModel effective)
    {
        var options = OpenAiClientOptionsFactory.Create(effective.BaseUrl, _modelFactory.TimeoutSeconds);
        if (_transport != null) options.Transport = _transport;
        return new OpenAIClient(new ApiKeyCredential(effective.ApiKey), options).GetChatClient(effective.Model);
    }

    private bool TryResolveJudge(out EffectiveOpenAiModel effective)
    {
        foreach (var candidate in new[] { AiModelRole.Companion, AiModelRole.TitleGeneration })
        {
            try
            {
                var resolved = _modelFactory.Resolve(candidate);
                resolved.ValidateChatRole(candidate);
                effective = resolved;
                return true;
            }
            catch (InvalidOperationException)
            {
                // 这个角色没配，试下一个
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to resolve the {Role} model for the debate judge", candidate);
            }
        }
        effective = default;
        return false;
    }
}
