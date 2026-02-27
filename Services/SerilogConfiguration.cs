using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.IO;
using System.Text.Json;

namespace Athena.UI.Services;

/// <summary>
/// Serilog SQLite Sink - 将日志写入 SQLite 数据库
/// </summary>
public class SQLiteSink : ILogEventSink, IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();
    private bool _initialized;

    public SQLiteSink(string dbPath)
    {
        _dbPath = dbPath;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

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

            _initialized = true;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            lock (_lock)
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                using var command = new SqliteCommand(
                    "INSERT INTO Logs (Timestamp, Level, Message, Exception, Properties) " +
                    "VALUES (@Timestamp, @Level, @Message, @Exception, @Properties)",
                    connection);

                command.Parameters.AddWithValue("@Timestamp", logEvent.Timestamp.LocalDateTime.ToString("O"));
                command.Parameters.AddWithValue("@Level", logEvent.Level.ToString());
                command.Parameters.AddWithValue("@Message", logEvent.RenderMessage());

                var exception = logEvent.Exception?.ToString();
                command.Parameters.AddWithValue("@Exception", exception ?? (object)DBNull.Value);

                var props = JsonSerializer.Serialize(logEvent.Properties);
                command.Parameters.AddWithValue("@Properties", props);

                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // 忽略日志写入错误，避免无限循环
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Serilog 配置扩展
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>
    /// 配置 Serilog
    /// </summary>
    public static Logger CreateLogger(string dbPath)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(Path.GetDirectoryName(dbPath)!, "log_.txt"),
                rollingInterval: Serilog.RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new SQLiteSink(dbPath))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Athena")
            .CreateLogger();
    }
}
