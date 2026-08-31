using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>用户主动发起的一次互动。每种互动有自己的冷却与数值去向。</summary>
public enum PetInteractionKind
{
    Pat,
    Feed,
    Play
}

/// <summary>
/// 宠物主动提出的需求。这是"宠物→用户"方向的唯一发起点：
/// 需求决定头顶提示符与气泡台词，用户用对应的互动来满足它。
/// </summary>
public enum PetNeedKind
{
    None,
    Hungry,
    Bored,
    Lonely
}

/// <summary>台词场景。本地台词库与模型台词共用这套分类。</summary>
public enum PetChatterTopic
{
    Greeting,
    Pat,
    PatCooldown,
    Feed,
    FeedCooldown,
    Play,
    PlayCooldown,
    LevelUp,
    NeedHungry,
    NeedBored,
    NeedLonely,
    NeedMet,
    FileCaught,
    ResponseDone,
    Sleepy,
    Idle
}

/// <summary>台词的情绪档位。只有三档，避免台词库按连续心情值爆炸。</summary>
public enum PetMoodBand
{
    Low,
    Normal,
    High
}

/// <summary>pet_profile.json 的根文档。</summary>
public sealed class VirtualPetProfileDocument
{
    /// <summary>存储 schema 版本。结构变更时递增。</summary>
    public int SchemaVersion { get; set; } = VirtualPetProgressionRules.CurrentSchemaVersion;

    public List<VirtualPetCompanionRecord> Pets { get; set; } = new();
}

/// <summary>
/// 单只宠物的养成存档。养成按 slug 分别记账——换一只宠物是"认识新伙伴"，
/// 而不是把上一只的进度清零。
/// </summary>
public sealed class VirtualPetCompanionRecord
{
    public string Slug { get; set; } = string.Empty;

    /// <summary>累计经验。只增不减，等级由它推导。</summary>
    public int Exp { get; set; }

    /// <summary>心情 0-100。事件冲击 + 随时间向基线回归。</summary>
    public double Mood { get; set; } = VirtualPetProgressionRules.MoodBaseline;

    /// <summary>精力 0-100。活动时下降、休息时恢复、投喂时补满一截。</summary>
    public double Energy { get; set; } = 80;

    public DateTimeOffset FirstMetAt { get; set; }

    /// <summary>
    /// 上次把时间推进到的时刻。心情/精力是按这个时间戳惰性推算的，
    /// 不靠计时器累加——否则应用关着的那几小时就凭空消失了。
    /// </summary>
    public DateTimeOffset LastTickAt { get; set; }

    public DateTimeOffset? LastPatAt { get; set; }
    public DateTimeOffset? LastFedAt { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public DateTimeOffset? LastInteractionAt { get; set; }

    /// <summary>最近一次互动所在的本地日期（yyyy-MM-dd），用于陪伴天数与每日首次互动奖励。</summary>
    public string? LastCompanionDate { get; set; }

    public int CompanionDays { get; set; }
    public int TotalPats { get; set; }
    public int TotalFeeds { get; set; }
    public int TotalPlays { get; set; }
    public int TotalConversations { get; set; }
    public int TotalToolCalls { get; set; }
    public int TotalNeedsMet { get; set; }

    public PetNeedKind ActiveNeed { get; set; }
    public DateTimeOffset? NeedRaisedAt { get; set; }

    /// <summary>需求冷却截止时刻。刚被满足过就不再立刻提新需求，避免变成骚扰。</summary>
    public DateTimeOffset? NeedCooldownUntil { get; set; }

    /// <summary>当前这次需求是否已经因为长时间没人理而扣过一次心情（每次需求只扣一次）。</summary>
    public bool NeedNeglectApplied { get; set; }

    public VirtualPetCompanionRecord Clone() => (VirtualPetCompanionRecord)MemberwiseClone();
}

/// <summary>
/// 提供给 UI 的只读投影。ViewModel 只读这个结构，不碰存档对象本身。
/// </summary>
public readonly record struct VirtualPetSnapshot(
    string Slug,
    int Level,
    int Exp,
    int ExpIntoLevel,
    int ExpForNextLevel,
    bool IsMaxLevel,
    double Mood,
    double Energy,
    int Bond,
    int CompanionDays,
    int TotalPats,
    int TotalFeeds,
    int TotalPlays,
    int TotalConversations,
    int TotalToolCalls,
    int TotalNeedsMet,
    PetNeedKind ActiveNeed)
{
    public static VirtualPetSnapshot Empty { get; } = new(
        string.Empty, 1, 0, 0, VirtualPetProgressionRules.ExpForLevel(2), false,
        VirtualPetProgressionRules.MoodBaseline, 80, 0, 0, 0, 0, 0, 0, 0, 0, PetNeedKind.None);

    public PetMoodBand MoodBand => VirtualPetProgressionRules.BandOf(Mood);

    /// <summary>当前等级内的经验完成度 0-1，满级恒为 1。</summary>
    public double LevelProgress => IsMaxLevel || ExpForNextLevel <= 0
        ? 1
        : Math.Clamp(ExpIntoLevel / (double)ExpForNextLevel, 0, 1);
}

/// <summary>一次互动的结果。冷却拒绝也是一种结果，调用方据此换一句台词。</summary>
public readonly record struct PetInteractionResult(
    PetInteractionKind Kind,
    bool Accepted,
    TimeSpan CooldownRemaining,
    int ExpGained,
    bool LeveledUp,
    int Level,
    PetNeedKind SatisfiedNeed,
    VirtualPetSnapshot Snapshot)
{
    public bool SatisfiedANeed => SatisfiedNeed != PetNeedKind.None;
}

/// <summary>
/// 一次模型台词请求。只带宠物自己看得见的状态，外加一小段当前话题，
/// 不携带完整对话内容——这条线用的是便宜的小模型，也不该成为新的数据出口。
/// </summary>
public sealed record PetChatterRequest(
    PetChatterTopic Topic,
    string PetName,
    int Level,
    double Mood,
    double Energy,
    PetNeedKind ActiveNeed,
    string? RecentToolName,
    string? RecentUserText,
    string Language);

/// <summary>
/// 养成规则的唯一出处。每一个数字都在这里，测试直接引用这些常量，
/// 不在断言里重抄一份魔数。
/// </summary>
public static class VirtualPetProgressionRules
{
    public const int CurrentSchemaVersion = 1;

    public const int MaxLevel = 20;

    /// <summary>心情基线：没有任何事件时，心情最终回到这里。</summary>
    public const double MoodBaseline = 55;

    public const double MoodMin = 0;
    public const double MoodMax = 100;
    public const double EnergyMin = 0;
    public const double EnergyMax = 100;

    /// <summary>心情每小时向基线回归的点数。</summary>
    public const double MoodRegressionPerHour = 9;

    /// <summary>休息（空闲/睡眠）时每小时恢复的精力。</summary>
    public const double EnergyRecoveryPerHour = 14;

    /// <summary>忙碌（思考/工具/漫游）时每小时消耗的精力。</summary>
    public const double EnergyDrainPerHour = 7;

    /// <summary>低于这个间隔的 Advance 调用直接跳过，避免每帧重算与属性风暴。</summary>
    public static readonly TimeSpan MinTickInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan PatCooldown = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan FeedCooldown = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PlayCooldown = TimeSpan.FromMinutes(5);

    public const int PatExp = 2;
    public const int FeedExp = 5;
    public const int PlayExp = 6;

    /// <summary>满足一次需求的额外经验。需求是宠物发起的，回应它比自发互动更值钱。</summary>
    public const int NeedMetBonusExp = 8;

    public const int ConversationExp = 3;
    public const int DailyFirstInteractionExp = 15;

    public const double PatMood = 6;
    public const double FeedMood = 12;
    public const double PlayMood = 10;
    public const double NeedMetMood = 6;
    public const double ToolFailureMood = -5;
    public const double ConversationSuccessMood = 2;

    /// <summary>需求挂了这么久还没人理，扣一次心情（每次需求只扣一次）。</summary>
    public static readonly TimeSpan NeedNeglectAfter = TimeSpan.FromMinutes(12);
    public const double NeedNeglectMood = -4;

    public const double FeedEnergy = 25;
    public const double PlayEnergy = -8;

    /// <summary>精力低于此值时提"饿了"。</summary>
    public const double HungryEnergyThreshold = 30;

    /// <summary>心情低于此值时提"无聊"。</summary>
    public const double BoredMoodThreshold = 40;

    /// <summary>这么久没有任何互动就提"想被摸摸"。</summary>
    public static readonly TimeSpan LonelyAfter = TimeSpan.FromMinutes(45);

    /// <summary>需求被满足后的静默期。</summary>
    public static readonly TimeSpan NeedCooldown = TimeSpan.FromMinutes(20);

    /// <summary>升到 <paramref name="level"/> 级所需的累计经验。L1 = 0。</summary>
    public static int ExpForLevel(int level)
    {
        if (level <= 1) return 0;
        var capped = Math.Min(level, MaxLevel);
        return 25 * capped * (capped - 1) / 2;
    }

    /// <summary>由累计经验推导等级。</summary>
    public static int LevelForExp(int exp)
    {
        var level = 1;
        while (level < MaxLevel && exp >= ExpForLevel(level + 1)) level++;
        return level;
    }

    public static PetMoodBand BandOf(double mood) => mood switch
    {
        <= 35 => PetMoodBand.Low,
        >= 75 => PetMoodBand.High,
        _ => PetMoodBand.Normal
    };

    /// <summary>某种需求由哪种互动来满足。用错的互动不会清掉需求——头顶的提示符就是答案。</summary>
    public static PetInteractionKind SatisfyingInteraction(PetNeedKind need) => need switch
    {
        PetNeedKind.Hungry => PetInteractionKind.Feed,
        PetNeedKind.Bored => PetInteractionKind.Play,
        _ => PetInteractionKind.Pat
    };

    public static TimeSpan CooldownOf(PetInteractionKind kind) => kind switch
    {
        PetInteractionKind.Feed => FeedCooldown,
        PetInteractionKind.Play => PlayCooldown,
        _ => PatCooldown
    };
}
