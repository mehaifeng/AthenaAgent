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
        // 使用应用程序根目录下的 AthenaData 文件夹，确保所有内容都在一起
        _baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AthenaData");

        // 确保基础目录存在
        if (!Directory.Exists(_baseDirectory))
        {
            Directory.CreateDirectory(_baseDirectory);
        }
    }

    public string GetAppDataDirectory() => _baseDirectory;

    public string GetConfigFilePath() => Path.Combine(_baseDirectory, "config.json");

    public string GetLogDirectory() => Path.Combine(_baseDirectory, "Logs");

    public string GetKnowledgeBaseDirectory() => Path.Combine(_baseDirectory, "KnowledgeBase");

    public string GetHistoryDirectory() => Path.Combine(_baseDirectory, "history");

    public string GetPendingArchiveDirectory() => Path.Combine(_baseDirectory, "PendingArchives");

    public string GetAttachmentDirectory() => Path.Combine(_baseDirectory, "Attachments");

    public string GetImageGenerationSessionDirectory() => Path.Combine(_baseDirectory, "ImageGenerationSessions");

    public string GetTaskSchedulerFilePath() => Path.Combine(_baseDirectory, "scheduled_tasks.json");

    public string GetVectorStoreFilePath() => Path.Combine(GetKnowledgeBaseDirectory(), "vectors.db");

    public string GetWorkspacesDirectory() => Path.Combine(_baseDirectory, "Workspaces");

    public string GetWorkspaceKnowledgeDirectory(string workspaceId) =>
        Path.Combine(GetWorkspacesDirectory(), workspaceId, "knowledge");
}
