using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace Athena.UI.Services.ComputerUse;

public sealed record BrowserHistoryEntry(string Url, string Title, DateTimeOffset VisitedAt)
{
    public string Caption => string.IsNullOrWhiteSpace(Title) ? Url : Title;
    public string LocalTime => VisitedAt.ToLocalTime().ToString("g");
}

internal sealed class BrowserHistory(string directory)
{
    public void Record(Uri url, string title, bool visit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = visit
            ? """
              INSERT INTO History(Url, Title, VisitedAt) VALUES ($url,$title,$time)
              ON CONFLICT(Url) DO UPDATE SET Title = CASE WHEN excluded.Title = '' THEN History.Title ELSE excluded.Title END,
                  VisitedAt = excluded.VisitedAt
              """
            : "UPDATE History SET Title = $title WHERE Url = $url AND $title <> ''";
        command.Parameters.AddWithValue("$url", url.AbsoluteUri);
        command.Parameters.AddWithValue("$title", title);
        if (visit) command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BrowserHistoryEntry> Search(string text)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Url, Title, VisitedAt FROM History
            WHERE instr(lower(Url),lower($q)) > 0 OR instr(lower(Title),lower($q)) > 0 OR $q = ''
            ORDER BY VisitedAt DESC LIMIT 500
            """;
        command.Parameters.AddWithValue("$q", text.Trim());
        using var reader = command.ExecuteReader();
        var results = new List<BrowserHistoryEntry>();
        while (reader.Read()) results.Add(new(reader.GetString(0), reader.GetString(1),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        return results;
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM History";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "History.db"), Pooling = false, DefaultTimeout = 3
        }.ConnectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS History(Url TEXT PRIMARY KEY, Title TEXT NOT NULL, VisitedAt INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS History_VisitedAt ON History(VisitedAt DESC);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
