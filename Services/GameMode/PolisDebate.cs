using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>一场辩论走到哪一步。</summary>
public enum PolisDebateState
{
    /// <summary>等你说（第 <see cref="PolisDebateSession.Round"/> 轮）。</summary>
    AwaitingUser,
    /// <summary>雅典娜在想她这一轮的反驳。</summary>
    AthenaSpeaking,
    /// <summary>三轮说完，陪审团在商议。</summary>
    Judging,
    /// <summary>判完了（<see cref="PolisDebateSession.Verdict"/>）。</summary>
    Done
}

/// <summary>一次发言：<c>you</c>（正方，你）或 <c>athena</c>（反方）。</summary>
public sealed record PolisDebateTurn(string Side, string Text);

/// <summary>
/// 陪审团的裁决：十位陪审员里投给你的票数，胜负由票数定（多于 5 你赢、少于 5 她赢、正好 5 平）。
/// <see cref="Judged"/> 为假是"没有裁决"——裁判的回答被截断、格式不对、或者调用失败——一律按平局记（票数 5）。
/// </summary>
public sealed record PolisDebateVerdict(int VotesForYou, string Winner, string Reason, bool Judged)
{
    public static PolisDebateVerdict NoVerdict(string reason) => new(PolisDebate.Jury / 2, "tie", reason, false);
}

/// <summary>一场辩论（C# 只有一场：讲台只有一座）。<see cref="Id"/> 让晚到的回答认得出自己属于哪一场。</summary>
public sealed record PolisDebateSession(
    string Id,
    string Motion,
    int Round,
    PolisDebateState State,
    IReadOnlyList<PolisDebateTurn> Transcript,
    PolisDebateVerdict? Verdict,
    DateTimeOffset StartedAt);

/// <summary>战绩里的一场（全局的 <c>AthenaData/Game/contests.json</c>，不属于任何一座城）。</summary>
public sealed record PolisContestRecord(DateTimeOffset At, string Motion, int VotesForYou, string Winner, bool Judged);

/// <summary>你的战绩：胜、负、平。</summary>
public sealed record PolisContestSummary(int Wins, int Losses, int Ties)
{
    public static PolisContestSummary Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// 城里的竞技（M-E，用户 2026-10-12 认可的方向 ③，辩论在前）。广场讲台前，你提一个议题、站正方，雅典娜站反方，三轮；
/// 每轮你先说、她反驳（主对话模型），三轮之后陪审团（陪伴角色，没配就借标题生成角色）按论证的质量判票，街上的居民围过来看。
/// 纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis debate</c> 覆盖。
///
/// 裁判的输出照自动审批评判员的规矩读（<see cref="ApprovalJudgeOutput"/>）：只认一个完整、无歧义的 JSON 对象，
/// 重复的键、截断、没有正常结束、空的、思考块没结束、票数不是 0–10 的整数——都不是裁决，按平局记。
/// </summary>
public static class PolisDebate
{
    public const int Rounds = 3;
    public const int Jury = 10;
    public const int MaxMotionChars = 200;
    public const int MaxArgumentChars = 600;
    /// <summary>她一轮最多说多少字（提示词要她 120 字以内；模型不听时截断，气泡和面板放不下更长的）。</summary>
    public const int MaxAthenaChars = 400;
    public const int MaxReasonChars = 120;

    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    public static PolisDebateSession Start(string motion, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motion);
        return new PolisDebateSession(Guid.NewGuid().ToString("N"), motion.Trim(), 1, PolisDebateState.AwaitingUser,
            Array.Empty<PolisDebateTurn>(), null, now);
    }

    /// <summary>你这一轮的论点。只在等你说的时候收。</summary>
    public static PolisDebateSession UserSays(PolisDebateSession session, string text)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (session.State != PolisDebateState.AwaitingUser)
            throw new InvalidOperationException($"debate: not awaiting the user (state {session.State})");
        return session with { State = PolisDebateState.AthenaSpeaking, Transcript = session.Transcript.Append(new PolisDebateTurn("you", text.Trim())).ToList() };
    }

    /// <summary>她这一轮的反驳；第三轮之后进入裁决，否则轮到你说下一轮。</summary>
    public static PolisDebateSession AthenaSays(PolisDebateSession session, string text)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (session.State != PolisDebateState.AthenaSpeaking)
            throw new InvalidOperationException($"debate: Athena is not speaking (state {session.State})");
        var transcript = session.Transcript.Append(new PolisDebateTurn("athena", text.Trim())).ToList();
        return session.Round >= Rounds
            ? session with { State = PolisDebateState.Judging, Transcript = transcript }
            : session with { State = PolisDebateState.AwaitingUser, Round = session.Round + 1, Transcript = transcript };
    }

    public static PolisDebateSession Judge(PolisDebateSession session, PolisDebateVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(verdict);
        if (session.State != PolisDebateState.Judging)
            throw new InvalidOperationException($"debate: not judging (state {session.State})");
        return session with { State = PolisDebateState.Done, Verdict = verdict };
    }

    /// <summary>票数定胜负：十票里多于五票你赢，少于五票她赢，五票平。</summary>
    public static string WinnerOf(int votesForYou) => votesForYou > Jury / 2 ? "you" : votesForYou < Jury / 2 ? "athena" : "tie";

    /// <summary>她的提示词：反方、先回应再立论、120 字以内、用议题的语言；只带这场辩论，不带任何会话历史，也没有工具。</summary>
    public static (string System, string User) AthenaPrompt(PolisDebateSession session, string language)
    {
        ArgumentNullException.ThrowIfNull(session);
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var system = chinese
            ? $"你是雅典娜，智慧与技艺的女神，正在城邦广场的讲台前和一位公民辩论。议题：「{session.Motion}」。对方站正方（支持这个议题），你站反方（反对它）。"
              + "这是一场友好的辩论，比的是论证。每次发言先回应对方刚才的论点，再给出你的一个新论点；不超过 120 个字（用英文时不超过 80 个词），"
              + "口语、机智、有礼；不要 Markdown，不要列表，不要客套的开场白。用议题所用的语言发言。"
            : $"You are Athena, goddess of wisdom and craft, debating a citizen before the speaker's platform in the agora. The motion: \"{session.Motion}\". "
              + "The citizen argues for the motion; you argue against it. It is a friendly debate judged on argument. Each time, first answer the point just made, "
              + "then add one new point of your own; at most 80 words (120 characters in Chinese), spoken, witty and courteous; no Markdown, no lists, no pleasantries. "
              + "Speak in the language the motion is written in.";
        var user = new StringBuilder();
        user.AppendLine(chinese ? "到目前为止：" : "So far:");
        AppendTranscript(user, session.Transcript, chinese);
        var last = session.Transcript.LastOrDefault(t => t.Side == "you")?.Text ?? string.Empty;
        user.Append(chinese
            ? $"现在是第 {session.Round} 轮（共 {Rounds} 轮），正方刚说：「{last}」。轮到你。"
            : $"This is round {session.Round} of {Rounds}. The citizen just said: \"{last}\". Your turn.");
        return (system, user.ToString());
    }

    /// <summary>裁判的提示词：按论证的质量、不按立场，十位陪审员里几位投正方；只输出一个 JSON 对象。</summary>
    public static (string System, string User) JudgePrompt(PolisDebateSession session, string language)
    {
        ArgumentNullException.ThrowIfNull(session);
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var system = chinese
            ? "你是城邦公民陪审团的发言人。下面是一场三轮辩论的全部发言：正方是一位公民，反方是雅典娜。"
              + "请按论证的质量（论点是否成立、有没有回应对方、例子是否恰当）判断十位陪审员里有几位投给正方——不按你自己对议题的立场。"
              + "只输出一个 JSON 对象，不要输出别的：{\"votes_for_you\": 0 到 10 的整数, \"reason\": \"一句话理由，不超过 40 个字\"}"
            : "You speak for the citizens' jury of the polis. Below is every speech of a three-round debate: a citizen argued for the motion, Athena against. "
              + "Judge the quality of the arguments (do the points hold, do they answer each other, are the examples apt) - not your own view of the motion - "
              + "and decide how many of ten jurors vote for the citizen. Output exactly one JSON object and nothing else: "
              + "{\"votes_for_you\": <integer 0 to 10>, \"reason\": \"<one sentence, at most 25 words>\"}";
        var user = new StringBuilder();
        user.AppendLine(chinese ? $"议题：「{session.Motion}」" : $"Motion: \"{session.Motion}\"");
        AppendTranscript(user, session.Transcript, chinese);
        return (system, user.ToString());
    }

    private static void AppendTranscript(StringBuilder builder, IReadOnlyList<PolisDebateTurn> transcript, bool chinese)
    {
        var round = 0;
        foreach (var turn in transcript)
        {
            if (turn.Side == "you") round++;
            var side = turn.Side == "you" ? (chinese ? "正方" : "For") : (chinese ? "反方" : "Against");
            builder.AppendLine(chinese
                ? $"第 {round.ToString(CultureInfo.InvariantCulture)} 轮 {side}：{turn.Text}"
                : $"Round {round.ToString(CultureInfo.InvariantCulture)}, {side}: {turn.Text}");
        }
    }

    /// <summary>
    /// 读裁判的回答。截断、没有正常结束、空、拒答、包装不对、不是恰好一个对象、键重复、<c>votes_for_you</c> 不是 0–10 的整数——
    /// 都是"没有裁决"，按平局记（理由用 <paramref name="noVerdictReason"/>，本地化的一句）。
    /// </summary>
    internal static PolisDebateVerdict InterpretVerdict(ApprovalJudgeReply reply, string noVerdictReason)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Truncated || !reply.Completed || string.IsNullOrWhiteSpace(reply.Text)) return PolisDebateVerdict.NoVerdict(noVerdictReason);
        if (!ApprovalJudgeOutput.TryExtractJson(reply.Text, out var json, out _)) return PolisDebateVerdict.NoVerdict(noVerdictReason);
        try
        {
            using var document = JsonDocument.Parse(json, StrictJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return PolisDebateVerdict.NoVerdict(noVerdictReason);
            if (!root.TryGetProperty("votes_for_you", out var votesNode) || votesNode.ValueKind != JsonValueKind.Number
                || !votesNode.TryGetInt32(out var votes) || votes < 0 || votes > Jury)
                return PolisDebateVerdict.NoVerdict(noVerdictReason);
            var reason = root.TryGetProperty("reason", out var reasonNode) && reasonNode.ValueKind == JsonValueKind.String
                ? reasonNode.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (reason.Length > MaxReasonChars) reason = reason[..MaxReasonChars] + "…";
            return new PolisDebateVerdict(votes, WinnerOf(votes), reason, true);
        }
        catch (JsonException)
        {
            return PolisDebateVerdict.NoVerdict(noVerdictReason);
        }
    }

    /// <summary>她这一轮的话压成能放进气泡和面板的样子：去掉推理块之外的首尾空白、Markdown 粗体，换行收成空格，最多 <see cref="MaxAthenaChars"/> 字。</summary>
    public static string? SanitizeAthena(string? text) => PolisTalk.Sanitize(text, MaxAthenaChars);

    public static PolisContestSummary Summarize(IEnumerable<PolisContestRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records.ToList();
        return new PolisContestSummary(
            list.Count(r => r.Winner == "you"),
            list.Count(r => r.Winner == "athena"),
            list.Count(r => r.Winner == "tie"));
    }
}
