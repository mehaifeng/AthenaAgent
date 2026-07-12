using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// MCP 服务页 ViewModel：MCP 服务器的增删改、重连与 JSON 导入。
/// 与 ConfigTabViewModel 共享同一个 AppConfig 实例——配置的加载、监听与防抖
/// 自动保存仍由 ConfigTabViewModel（配置属主）负责，本 VM 只承载 MCP 相关的
/// UI 状态与命令。
/// </summary>
public partial class McpTabViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly ILocalizationService? _localizationService;

    /// <summary>与 ConfigTabViewModel 共享的配置实例（由 Initialize 注入并跟随其替换）。</summary>
    [ObservableProperty]
    private AppConfig _config = new();

    public McpTabViewModel() : this(null, null) { }

    public McpTabViewModel(IConfigService? configService, ILocalizationService? localizationService)
    {
        _configService = configService;
        _localizationService = localizationService;
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    /// <summary>
    /// 绑定到配置属主：镜像其 Config 实例，并在属主因外部变更整体替换 Config 时跟随。
    /// </summary>
    public void Initialize(ConfigTabViewModel configOwner)
    {
        Config = configOwner.Config;
        configOwner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigTabViewModel.Config))
            {
                Config = configOwner.Config;
            }
        };
    }

    /// <summary>新增一个空 MCP 服务器条目，等待用户填入命令。</summary>
    [RelayCommand]
    private async Task AddMcpServerAsync()
    {
        var idx = Config.McpServers.Count + 1;
        Config.McpServers.Add(new McpServerConfig
        {
            Name = $"server-{idx}",
            Enabled = false,
            Command = string.Empty,
            StartupTimeoutSeconds = 15,
            CallTimeoutSeconds = 60
        });
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除指定 MCP 服务器条目。</summary>
    [RelayCommand]
    private async Task RemoveMcpServerAsync(McpServerConfig? server)
    {
        if (server != null && Config.McpServers.Remove(server) && _configService != null)
        {
            await _configService.SaveAsync(Config);
        }
    }

    /// <summary>给指定服务器追加一个空参数条目。</summary>
    [RelayCommand]
    private async Task AddMcpArgAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Arguments.Add(new McpArgEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个参数条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpArgAsync(McpArgEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Arguments.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>给指定服务器追加一个空环境变量条目。</summary>
    [RelayCommand]
    private async Task AddMcpEnvAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Environment.Add(new McpEnvEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个环境变量条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpEnvAsync(McpEnvEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Environment.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>给指定 Http 服务器追加一个空请求头条目。</summary>
    [RelayCommand]
    private async Task AddMcpHeaderAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Headers.Add(new McpEnvEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个请求头条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpHeaderAsync(McpEnvEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Headers.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>重新应用 MCP 配置：再次保存即触发 ConfigChanged → 生命周期重连未生效（含失败）的服务器。</summary>
    [RelayCommand]
    private async Task ReconnectMcpAsync()
    {
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>粘贴的 MCP JSON（Claude Desktop 格式），供导入命令读取。</summary>
    [ObservableProperty]
    private string _mcpImportJson = string.Empty;

    /// <summary>导入结果提示（成功计数或错误信息）。</summary>
    [ObservableProperty]
    private string _mcpImportStatus = string.Empty;

    /// <summary>解析粘贴的 JSON，按名称合并进 McpServers（同名覆盖）。</summary>
    [RelayCommand]
    private async Task ImportMcpJsonAsync()
    {
        if (string.IsNullOrWhiteSpace(McpImportJson))
        {
            McpImportStatus = GetString("Config.Mcp.ImportEmpty", "Please paste the MCP config JSON first.");
            return;
        }

        try
        {
            var parsed = Services.Mcp.McpConfigImporter.Parse(McpImportJson);
            foreach (var incoming in parsed)
            {
                var existing = Config.McpServers.FirstOrDefault(
                    s => string.Equals(s.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) Config.McpServers.Remove(existing);
                Config.McpServers.Add(incoming);
            }
            McpImportStatus = string.Format(
                GetString("Config.Mcp.ImportSuccess", "Imported {0} server(s)."), parsed.Count);
            McpImportJson = string.Empty;
            if (_configService != null) await _configService.SaveAsync(Config);
        }
        catch (Exception ex)
        {
            McpImportStatus = string.Format(
                GetString("Config.Mcp.ImportFailed", "Import failed: {0}"), ex.Message);
        }
    }
}
