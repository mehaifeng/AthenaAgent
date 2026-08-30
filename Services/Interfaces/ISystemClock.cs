using System;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 可注入的时钟。cron 的正确性完全依赖"现在几点"，测试必须能把时间拨到
/// 夏令时切换那一天而不是等它真的到来。
/// </summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>生产实现：直接读系统时钟。</summary>
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
