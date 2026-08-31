using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.VirtualPet;

/// <summary>
/// 养成规则引擎。所有数值只在这里变化，规则常量全部来自
/// <see cref="VirtualPetProgressionRules"/>。
///
/// 两条设计约束值得记住：
/// 1. 心情/精力是<b>惰性推进</b>的——存的是数值加上次推进时刻，读的时候按真实经过的时间补算。
///    用计时器累加的话，应用关着的那八小时里宠物既不会饿也不会休息好，重开像时间没走过。
/// 2. 冷却是<b>只挡收益、不挡反馈</b>：冷却中的摸头照样有动画和台词，只是不再加数值。
///    直接禁用按钮会让人以为点坏了，而反复点击刷数值又会把养成变成无意义的连点。
/// </summary>
public sealed class VirtualPetProgressionService : IVirtualPetProgressionService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    private readonly IPetProfileStore _store;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, VirtualPetCompanionRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);

    private int _savePending;
    private bool _disposed;

    public VirtualPetProgressionService(IPetProfileStore store, ISystemClock clock, ILogger logger)
    {
        _store = store;
        _clock = clock;
        _logger = logger.ForContext<VirtualPetProgressionService>();

        foreach (var record in _store.Load().Pets)
        {
            if (!string.IsNullOrWhiteSpace(record.Slug)) _records[record.Slug] = record;
        }
    }

    public event EventHandler<VirtualPetSnapshot>? SnapshotChanged;

    public VirtualPetSnapshot GetSnapshot(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return VirtualPetSnapshot.Empty;
        lock (_gate) return Project(GetOrCreate(slug));
    }

    public VirtualPetSnapshot Advance(string slug, bool busy)
    {
        if (string.IsNullOrWhiteSpace(slug)) return VirtualPetSnapshot.Empty;

        VirtualPetSnapshot snapshot;
        bool changed;
        lock (_gate)
        {
            var record = GetOrCreate(slug);
            var now = _clock.UtcNow;
            if (now - record.LastTickAt < VirtualPetProgressionRules.MinTickInterval)
                return Project(record);

            var before = Fingerprint(record);
            Tick(record, now, busy);
            EvaluateNeed(record, now);
            snapshot = Project(record);
            changed = Fingerprint(record) != before;
        }

        if (changed)
        {
            // 时间推进本身是可重算的：即使这次没写盘，下次启动会从上次的时间戳继续补算。
            ScheduleSave(immediate: false);
            SnapshotChanged?.Invoke(this, snapshot);
        }
        return snapshot;
    }

    public PetInteractionResult Interact(string slug, PetInteractionKind kind)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return new PetInteractionResult(kind, false, TimeSpan.Zero, 0, false, 1, PetNeedKind.None, VirtualPetSnapshot.Empty);

        PetInteractionResult result;
        lock (_gate)
        {
            var record = GetOrCreate(slug);
            var now = _clock.UtcNow;
            Tick(record, now, busy: false);

            var cooldown = VirtualPetProgressionRules.CooldownOf(kind);
            var last = kind switch
            {
                PetInteractionKind.Feed => record.LastFedAt,
                PetInteractionKind.Play => record.LastPlayedAt,
                _ => record.LastPatAt
            };
            var remaining = last.HasValue ? cooldown - (now - last.Value) : TimeSpan.Zero;
            if (remaining > TimeSpan.Zero)
            {
                // 冷却中：不计数、不加经验，但仍然刷新"最近互动"，
                // 否则一个正在被反复逗弄的宠物还会喊寂寞。
                record.LastInteractionAt = now;
                EvaluateNeed(record, now);
                result = new PetInteractionResult(kind, false, remaining, 0, false, LevelOf(record), PetNeedKind.None, Project(record));
            }
            else
            {
                result = ApplyInteraction(record, kind, now);
            }
        }

        // 有收益的互动立刻落盘（升级、投喂这类事件稀疏且用户看得见）；
        // 被冷却挡下的只更新了"最近互动"，跟着去抖批次走就够了。
        ScheduleSave(immediate: result.Accepted);
        SnapshotChanged?.Invoke(this, result.Snapshot);
        return result;
    }

    public VirtualPetSnapshot RecordConversationCompleted(string slug, bool succeeded, int toolCalls)
    {
        if (string.IsNullOrWhiteSpace(slug)) return VirtualPetSnapshot.Empty;

        VirtualPetSnapshot snapshot;
        lock (_gate)
        {
            var record = GetOrCreate(slug);
            var now = _clock.UtcNow;
            Tick(record, now, busy: false);
            record.TotalConversations++;
            record.TotalToolCalls += Math.Max(0, toolCalls);
            if (succeeded)
            {
                AddExp(record, VirtualPetProgressionRules.ConversationExp);
                AddMood(record, VirtualPetProgressionRules.ConversationSuccessMood);
            }
            EvaluateNeed(record, now);
            snapshot = Project(record);
        }

        ScheduleSave(immediate: false);
        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    public VirtualPetSnapshot RecordToolFailure(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return VirtualPetSnapshot.Empty;

        VirtualPetSnapshot snapshot;
        lock (_gate)
        {
            var record = GetOrCreate(slug);
            var now = _clock.UtcNow;
            Tick(record, now, busy: true);
            AddMood(record, VirtualPetProgressionRules.ToolFailureMood);
            EvaluateNeed(record, now);
            snapshot = Project(record);
        }

        ScheduleSave(immediate: false);
        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    public async Task FlushAsync()
    {
        Interlocked.Exchange(ref _savePending, 0);
        await SaveNowAsync().ConfigureAwait(false);
    }

    // ===== 规则实现 =====

    private VirtualPetCompanionRecord GetOrCreate(string slug)
    {
        if (_records.TryGetValue(slug, out var existing)) return existing;
        var now = _clock.UtcNow;
        var created = new VirtualPetCompanionRecord
        {
            Slug = slug,
            FirstMetAt = now,
            LastTickAt = now
        };
        _records[slug] = created;
        return created;
    }

    /// <summary>把心情/精力推进到 <paramref name="now"/>。幂等：重复调用只是把间隔切得更细。</summary>
    private static void Tick(VirtualPetCompanionRecord record, DateTimeOffset now, bool busy)
    {
        if (record.LastTickAt == default) record.LastTickAt = now;
        var elapsedHours = (now - record.LastTickAt).TotalHours;
        record.LastTickAt = now;
        // 时钟被往回拨（时区/校时）时不做任何补算，避免负增长把数值打穿。
        if (elapsedHours <= 0) return;

        var energyRate = busy
            ? -VirtualPetProgressionRules.EnergyDrainPerHour
            : VirtualPetProgressionRules.EnergyRecoveryPerHour;
        record.Energy = Clamp(
            record.Energy + energyRate * elapsedHours,
            VirtualPetProgressionRules.EnergyMin,
            VirtualPetProgressionRules.EnergyMax);

        var distance = VirtualPetProgressionRules.MoodBaseline - record.Mood;
        if (Math.Abs(distance) > 0.001)
        {
            var step = VirtualPetProgressionRules.MoodRegressionPerHour * elapsedHours;
            record.Mood += Math.Sign(distance) * Math.Min(Math.Abs(distance), step);
        }
        record.Mood = Clamp(record.Mood, VirtualPetProgressionRules.MoodMin, VirtualPetProgressionRules.MoodMax);
    }

    private PetInteractionResult ApplyInteraction(
        VirtualPetCompanionRecord record,
        PetInteractionKind kind,
        DateTimeOffset now)
    {
        var levelBefore = LevelOf(record);
        var exp = 0;

        switch (kind)
        {
            case PetInteractionKind.Feed:
                record.LastFedAt = now;
                record.TotalFeeds++;
                exp += VirtualPetProgressionRules.FeedExp;
                AddMood(record, VirtualPetProgressionRules.FeedMood);
                record.Energy = Clamp(
                    record.Energy + VirtualPetProgressionRules.FeedEnergy,
                    VirtualPetProgressionRules.EnergyMin,
                    VirtualPetProgressionRules.EnergyMax);
                break;
            case PetInteractionKind.Play:
                record.LastPlayedAt = now;
                record.TotalPlays++;
                exp += VirtualPetProgressionRules.PlayExp;
                AddMood(record, VirtualPetProgressionRules.PlayMood);
                record.Energy = Clamp(
                    record.Energy + VirtualPetProgressionRules.PlayEnergy,
                    VirtualPetProgressionRules.EnergyMin,
                    VirtualPetProgressionRules.EnergyMax);
                break;
            default:
                record.LastPatAt = now;
                record.TotalPats++;
                exp += VirtualPetProgressionRules.PatExp;
                AddMood(record, VirtualPetProgressionRules.PatMood);
                break;
        }

        // 每日首次互动：按用户感知的本地日期算，不是 UTC 日。
        var today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(record.LastCompanionDate, today, StringComparison.Ordinal))
        {
            record.LastCompanionDate = today;
            record.CompanionDays++;
            exp += VirtualPetProgressionRules.DailyFirstInteractionExp;
        }

        var satisfied = PetNeedKind.None;
        if (record.ActiveNeed != PetNeedKind.None
            && VirtualPetProgressionRules.SatisfyingInteraction(record.ActiveNeed) == kind)
        {
            satisfied = record.ActiveNeed;
            record.TotalNeedsMet++;
            exp += VirtualPetProgressionRules.NeedMetBonusExp;
            AddMood(record, VirtualPetProgressionRules.NeedMetMood);
            record.ActiveNeed = PetNeedKind.None;
            record.NeedRaisedAt = null;
            record.NeedNeglectApplied = false;
            record.NeedCooldownUntil = now + VirtualPetProgressionRules.NeedCooldown;
        }

        record.LastInteractionAt = now;
        AddExp(record, exp);
        EvaluateNeed(record, now);

        var levelAfter = LevelOf(record);
        return new PetInteractionResult(
            kind,
            Accepted: true,
            CooldownRemaining: TimeSpan.Zero,
            ExpGained: exp,
            LeveledUp: levelAfter > levelBefore,
            Level: levelAfter,
            SatisfiedNeed: satisfied,
            Snapshot: Project(record));
    }

    /// <summary>
    /// 需求判定。优先级 饿 &gt; 无聊 &gt; 寂寞：精力见底是最具体的诉求，寂寞最泛。
    /// 满足过一次之后进入静默期，宠物不会连着要东西。
    /// </summary>
    private static void EvaluateNeed(VirtualPetCompanionRecord record, DateTimeOffset now)
    {
        if (record.ActiveNeed != PetNeedKind.None)
        {
            // 需求还挂着：条件已经不成立就自然消解，挂太久就扣一次心情。
            if (!NeedStillHolds(record, now))
            {
                record.ActiveNeed = PetNeedKind.None;
                record.NeedRaisedAt = null;
                record.NeedNeglectApplied = false;
                return;
            }
            if (!record.NeedNeglectApplied
                && record.NeedRaisedAt.HasValue
                && now - record.NeedRaisedAt.Value >= VirtualPetProgressionRules.NeedNeglectAfter)
            {
                record.NeedNeglectApplied = true;
                record.Mood = Clamp(
                    record.Mood + VirtualPetProgressionRules.NeedNeglectMood,
                    VirtualPetProgressionRules.MoodMin,
                    VirtualPetProgressionRules.MoodMax);
            }
            return;
        }

        if (record.NeedCooldownUntil.HasValue && now < record.NeedCooldownUntil.Value) return;

        var next = DetectNeed(record, now);
        if (next == PetNeedKind.None) return;
        record.ActiveNeed = next;
        record.NeedRaisedAt = now;
        record.NeedNeglectApplied = false;
    }

    private static bool NeedStillHolds(VirtualPetCompanionRecord record, DateTimeOffset now) => record.ActiveNeed switch
    {
        PetNeedKind.Hungry => record.Energy <= VirtualPetProgressionRules.HungryEnergyThreshold,
        PetNeedKind.Bored => record.Mood <= VirtualPetProgressionRules.BoredMoodThreshold,
        PetNeedKind.Lonely => IsLonely(record, now),
        _ => false
    };

    private static PetNeedKind DetectNeed(VirtualPetCompanionRecord record, DateTimeOffset now)
    {
        if (record.Energy <= VirtualPetProgressionRules.HungryEnergyThreshold) return PetNeedKind.Hungry;
        if (record.Mood <= VirtualPetProgressionRules.BoredMoodThreshold) return PetNeedKind.Bored;
        return IsLonely(record, now) ? PetNeedKind.Lonely : PetNeedKind.None;
    }

    private static bool IsLonely(VirtualPetCompanionRecord record, DateTimeOffset now)
        => now - (record.LastInteractionAt ?? record.FirstMetAt) >= VirtualPetProgressionRules.LonelyAfter;

    private static void AddExp(VirtualPetCompanionRecord record, int exp)
    {
        if (exp <= 0) return;
        var cap = VirtualPetProgressionRules.ExpForLevel(VirtualPetProgressionRules.MaxLevel);
        record.Exp = Math.Min(record.Exp + exp, cap);
    }

    private static void AddMood(VirtualPetCompanionRecord record, double delta)
        => record.Mood = Clamp(
            record.Mood + delta,
            VirtualPetProgressionRules.MoodMin,
            VirtualPetProgressionRules.MoodMax);

    private static int LevelOf(VirtualPetCompanionRecord record)
        => VirtualPetProgressionRules.LevelForExp(record.Exp);

    private static double Clamp(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static VirtualPetSnapshot Project(VirtualPetCompanionRecord record)
    {
        var level = LevelOf(record);
        var isMax = level >= VirtualPetProgressionRules.MaxLevel;
        var floor = VirtualPetProgressionRules.ExpForLevel(level);
        var ceiling = VirtualPetProgressionRules.ExpForLevel(level + 1);
        return new VirtualPetSnapshot(
            record.Slug,
            level,
            record.Exp,
            record.Exp - floor,
            isMax ? 0 : ceiling - floor,
            isMax,
            Math.Round(record.Mood, 2),
            Math.Round(record.Energy, 2),
            record.TotalPats + record.TotalFeeds + record.TotalPlays,
            record.CompanionDays,
            record.TotalPats,
            record.TotalFeeds,
            record.TotalPlays,
            record.TotalConversations,
            record.TotalToolCalls,
            record.TotalNeedsMet,
            record.ActiveNeed);
    }

    /// <summary>只包含"值得写盘"的字段，用来判断一次推进有没有真正改变什么。</summary>
    private static string Fingerprint(VirtualPetCompanionRecord record) => string.Create(
        CultureInfo.InvariantCulture,
        $"{record.Exp}|{record.Mood:F2}|{record.Energy:F2}|{record.ActiveNeed}|{record.NeedNeglectApplied}");

    // ===== 持久化 =====

    private void ScheduleSave(bool immediate)
    {
        if (_disposed) return;
        if (immediate)
        {
            Interlocked.Exchange(ref _savePending, 0);
            _ = SaveNowAsync();
            return;
        }
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = DebouncedSaveAsync();
    }

    private async Task DebouncedSaveAsync()
    {
        try
        {
            await Task.Delay(SaveDebounce).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _savePending, 0) == 0) return;
            await SaveNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Debounced pet profile save failed");
        }
    }

    private async Task SaveNowAsync()
    {
        VirtualPetProfileDocument document;
        lock (_gate)
        {
            document = new VirtualPetProfileDocument
            {
                SchemaVersion = VirtualPetProgressionRules.CurrentSchemaVersion,
                Pets = _records.Values.Select(record => record.Clone()).ToList()
            };
        }
        await _store.SaveAsync(document).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放路径。生产退出走的是 <see cref="DisposeAsync"/>（App 释放 DI 容器时用的就是它），
    /// 这里只做尽力而为的最后一次写入：宁可丢掉最近两秒的心情小数，也不在退出路径上阻塞等待 IO。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = SaveNowAsync();
    }
}
