using System;
using System.Collections.Generic;
using System.Linq;
using Athena.UI.Models.Mcp;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 线程安全的 MCP 工具索引。ClientManager 在服务器连接/重连/断开时原子替换该服务器的工具集。
/// 供 <see cref="IMcpToolHost"/> 实现方（真实与 fake）复用。
/// </summary>
public sealed class McpToolRegistry
{
    private readonly object _gate = new();
    // key: FullyQualifiedName
    private Dictionary<string, McpToolDescriptor> _byFqName = new(StringComparer.OrdinalIgnoreCase);
    // key: server name
    private Dictionary<string, List<string>> _byServer = new(StringComparer.OrdinalIgnoreCase);

    public void ReplaceServerTools(string server, IEnumerable<McpToolDescriptor> tools)
    {
        lock (_gate)
        {
            // 移除旧集合
            if (_byServer.TryGetValue(server, out var oldNames))
            {
                foreach (var n in oldNames) _byFqName.Remove(n);
            }
            var newNames = new List<string>();
            foreach (var t in tools)
            {
                _byFqName[t.FullyQualifiedName] = t;
                newNames.Add(t.FullyQualifiedName);
            }
            _byServer[server] = newNames;
        }
    }

    public void RemoveServer(string server)
    {
        lock (_gate)
        {
            if (_byServer.TryGetValue(server, out var names))
            {
                foreach (var n in names) _byFqName.Remove(n);
                _byServer.Remove(server);
            }
        }
    }

    public McpToolDescriptor? Find(string fullyQualifiedName)
    {
        lock (_gate)
        {
            return _byFqName.TryGetValue(fullyQualifiedName, out var d) ? d : null;
        }
    }

    public IReadOnlyList<McpToolDescriptor> Snapshot(string? serverFilter = null)
    {
        lock (_gate)
        {
            IEnumerable<McpToolDescriptor> src = _byFqName.Values;
            if (!string.IsNullOrEmpty(serverFilter))
                src = src.Where(d => string.Equals(d.Server, serverFilter, StringComparison.OrdinalIgnoreCase));
            return src.OrderBy(d => d.Server, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(d => d.OriginalName, StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }
    }

    public IReadOnlyList<string> ServerNames()
    {
        lock (_gate)
        {
            return _byServer.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
