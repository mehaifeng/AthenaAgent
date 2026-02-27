using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 日志条目
/// </summary>
public class LogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public string? Properties { get; set; }
}

/// <summary>
/// 日志查询参数
/// </summary>
public class LogQueryParams
{
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Level { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    /// <summary>
    /// 搜索关键词（在日志内容中搜索）
    /// </summary>
    public string? SearchKeyword { get; set; }
}

/// <summary>
/// 日志查询结果
/// </summary>
public class LogQueryResult
{
    public List<LogEntry> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>
/// 日志服务接口
/// </summary>
public interface ILogService
{
    /// <summary>
    /// 查询日志
    /// </summary>
    Task<LogQueryResult> QueryLogsAsync(LogQueryParams queryParams);

    /// <summary>
    /// 清除所有日志
    /// </summary>
    Task ClearAllLogsAsync();

    /// <summary>
    /// 导出日志到文件
    /// </summary>
    Task<string> ExportLogsAsync(DateTime? startTime, DateTime? endTime);

    /// <summary>
    /// 获取数据库路径
    /// </summary>
    string DatabasePath { get; }
}
