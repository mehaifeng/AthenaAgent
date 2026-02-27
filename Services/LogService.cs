using Athena.UI.Services.Interfaces;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 日志服务实现
/// </summary>
public class LogService : ILogService
{
    private readonly string _dbPath;
    private readonly string _logDir;
    private readonly IPlatformPathService _platformPathService;

    public string DatabasePath => _dbPath;

    public LogService(IPlatformPathService platformPathService)
    {
        _platformPathService = platformPathService;
        _logDir = _platformPathService.GetLogDirectory();
        Directory.CreateDirectory(_logDir);

        _dbPath = Path.Combine(_logDir, "logs.db");
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS Logs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Level TEXT NOT NULL,
                Message TEXT NOT NULL,
                Exception TEXT,
                Properties TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Logs_Timestamp ON Logs(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Logs_Level ON Logs(Level);
        ";

        using var command = new SqliteCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    public async Task<LogQueryResult> QueryLogsAsync(LogQueryParams queryParams)
    {
        var result = new LogQueryResult
        {
            Page = queryParams.Page,
            PageSize = queryParams.PageSize
        };

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        // 构建查询条件
        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (queryParams.StartTime.HasValue)
        {
            whereClauses.Add("Timestamp >= @StartTime");
            parameters.Add(new SqliteParameter("@StartTime", queryParams.StartTime.Value.ToString("O")));
        }

        if (queryParams.EndTime.HasValue)
        {
            whereClauses.Add("Timestamp <= @EndTime");
            parameters.Add(new SqliteParameter("@EndTime", queryParams.EndTime.Value.ToString("O")));
        }

        if (!string.IsNullOrEmpty(queryParams.Level) && queryParams.Level != "All")
        {
            whereClauses.Add("Level = @Level");
            parameters.Add(new SqliteParameter("@Level", queryParams.Level.ToUpper()));
        }

        if (!string.IsNullOrWhiteSpace(queryParams.SearchKeyword))
        {
            whereClauses.Add("Message LIKE @SearchKeyword");
            parameters.Add(new SqliteParameter("@SearchKeyword", $"%{queryParams.SearchKeyword}%"));
        }

        var whereClause = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        // 查询总数
        var countSql = $"SELECT COUNT(*) FROM Logs {whereClause}";
        using (var countCommand = new SqliteCommand(countSql, connection))
        {
            countCommand.Parameters.AddRange(parameters.ToArray());
            result.TotalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
        }

        // 查询数据
        var offset = (queryParams.Page - 1) * queryParams.PageSize;
        var dataSql = $@"
            SELECT Id, Timestamp, Level, Message, Exception, Properties
            FROM Logs
            {whereClause}
            ORDER BY Timestamp DESC
            LIMIT @PageSize OFFSET @Offset
        ";

        using (var dataCommand = new SqliteCommand(dataSql, connection))
        {
            dataCommand.Parameters.AddRange(parameters.ToArray());
            dataCommand.Parameters.Add(new SqliteParameter("@PageSize", queryParams.PageSize));
            dataCommand.Parameters.Add(new SqliteParameter("@Offset", offset));

            using var reader = await dataCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Entries.Add(new LogEntry
                {
                    Id = reader.GetInt64(0),
                    Timestamp = DateTime.Parse(reader.GetString(1)),
                    Level = reader.GetString(2),
                    Message = reader.GetString(3),
                    Exception = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Properties = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        return result;
    }

    public async Task ClearAllLogsAsync()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        using var command = new SqliteCommand("DELETE FROM Logs", connection);
        await command.ExecuteNonQueryAsync();

        // 清理数据库空间
        using var vacuumCommand = new SqliteCommand("VACUUM", connection);
        await vacuumCommand.ExecuteNonQueryAsync();
    }

    public async Task<string> ExportLogsAsync(DateTime? startTime, DateTime? endTime)
    {
        var queryParams = new LogQueryParams
        {
            StartTime = startTime,
            EndTime = endTime,
            PageSize = int.MaxValue
        };

        var result = await QueryLogsAsync(queryParams);

        var exportDir = Path.Combine(_logDir, "Exports");
        Directory.CreateDirectory(exportDir);

        var fileName = $"logs_export_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        var filePath = Path.Combine(exportDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"=== Athena Log Export ===");
        sb.AppendLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total Entries: {result.TotalCount}");
        sb.AppendLine();

        foreach (var entry in result.Entries)
        {
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
            if (!string.IsNullOrEmpty(entry.Exception))
            {
                sb.AppendLine($"  Exception: {entry.Exception}");
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString());
        return filePath;
    }
}
