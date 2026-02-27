using System;
using System.IO;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services.Platform;

/// <summary>
/// 桌面平台路径服务实现
/// </summary>
public class DesktopPlatformPathService : IPlatformPathService
{
    private readonly string _baseDirectory;

    public DesktopPlatformPathService()
    {
        // 使用 ~/.local/share/Athena 作为数据目录
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _baseDirectory = Path.Combine(homeDir, ".local", "share", "Athena");
    }

    public string GetAppDataDirectory() => _baseDirectory;

    public string GetConfigFilePath() => Path.Combine(_baseDirectory, "config.json");

    public string GetLogDirectory() => Path.Combine(_baseDirectory, "Logs");

    public string GetKnowledgeBaseDirectory() => Path.Combine(_baseDirectory, "KnowledgeBase");

    public string GetHistoryDirectory() => Path.Combine(_baseDirectory, "history");
}
