using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// SQLite 日志 Sink：Emit 无锁入队立即返回，后台单线程用常驻连接攒批写入。
/// </summary>
public class SQLiteSink : ILogEventSink, IDisposable
{
    // 队列上限：溢出即丢弃最旧事件，日志绝不拖垮主流程
    private const int MaxQueuedEvents = 10_000;
    // 单事务最多插入条数：够大以摊薄事务提交开销，够小以控制单次锁窗口
    private const int MaxBatchSize = 256;

    private readonly string _dbPath;
    private readonly Channel<LogEvent> _channel;
    private readonly Task _worker;

    public SQLiteSink(string dbPath)
    {
        _dbPath = dbPath;
        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(MaxQueuedEvents)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _worker = Task.Run(WriteLoopAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        // 无锁入队，调用线程零阻塞
        _channel.Writer.TryWrite(logEvent);
    }

    private async Task WriteLoopAsync()
    {
        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection();

            var batch = new List<LogEvent>(MaxBatchSize);
            var reader = _channel.Reader;

            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }

                if (batch.Count > 0)
                {
                    WriteBatch(connection, batch);
                }
            }
        }
        catch
        {
            // 日志管线自身失败静默吞掉
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        // WAL 提交无需重建 journal；synchronous=NORMAL 断电只丢最近未 checkpoint 一批，日志可接受
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = @"
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
            create.ExecuteNonQuery();
        }

        return connection;
    }

    private static void WriteBatch(SqliteConnection connection, List<LogEvent> batch)
    {
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO Logs (Timestamp, Level, Message, Exception, Properties) " +
                "VALUES (@Timestamp, @Level, @Message, @Exception, @Properties)";

            var pTimestamp = command.Parameters.Add("@Timestamp", SqliteType.Text);
            var pLevel = command.Parameters.Add("@Level", SqliteType.Text);
            var pMessage = command.Parameters.Add("@Message", SqliteType.Text);
            var pException = command.Parameters.Add("@Exception", SqliteType.Text);
            var pProperties = command.Parameters.Add("@Properties", SqliteType.Text);

            foreach (var logEvent in batch)
            {
                pTimestamp.Value = logEvent.Timestamp.LocalDateTime.ToString("O");
                pLevel.Value = logEvent.Level.ToString();
                pMessage.Value = logEvent.RenderMessage();
                pException.Value = logEvent.Exception?.ToString() ?? (object)DBNull.Value;
                pProperties.Value = JsonSerializer.Serialize(logEvent.Properties);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            // 单批失败不影响后续批次
        }
    }

    public void Dispose()
    {
        try
        {
            // 关闭入口后给后台线程冲刷余量，超时放弃（进程退出优先）
            _channel.Writer.TryComplete();
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 冲刷失败忽略
        }
    }
}

/// <summary>
/// Serilog 配置扩展
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>
    /// 文本文件保留 Debug 便于线下排障；Console 与 SQLite 只收 Information+，
    /// 避免高频 Debug 事件膨胀数据库与终端。
    /// </summary>
    public static Logger CreateLogger(string dbPath)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(Path.GetDirectoryName(dbPath)!, "log_.txt"),
                rollingInterval: Serilog.RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new SQLiteSink(dbPath), restrictedToMinimumLevel: LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Athena")
            .CreateLogger();
    }
}
