using System.IO;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 平台路径服务接口，用于获取不同平台的数据存储路径
/// </summary>
public interface IPlatformPathService
{
    /// <summary>
    /// 获取应用数据目录
    /// </summary>
    string GetAppDataDirectory();

    /// <summary>
    /// 获取配置文件路径
    /// </summary>
    string GetConfigFilePath();

    /// <summary>
    /// 获取日志目录
    /// </summary>
    string GetLogDirectory();

    /// <summary>
    /// 获取知识库目录
    /// </summary>
    string GetKnowledgeBaseDirectory();

    /// <summary>
    /// 获取对话历史目录
    /// </summary>
    string GetHistoryDirectory();

    /// <summary>
    /// 获取待处理归档目录
    /// </summary>
    string GetPendingArchiveDirectory();

    /// <summary>
    /// 获取附件目录
    /// </summary>
    string GetAttachmentDirectory();

    /// <summary>
    /// 获取图像生成会话目录
    /// </summary>
    string GetImageGenerationSessionDirectory();

    /// <summary>
    /// 获取 cron 定时任务存储文件路径
    /// </summary>
    string GetCronTasksFilePath();

    /// <summary>
    /// 获取已废弃的旧任务调度文件路径。仅用于启动时删除遗留文件，绝不读取其内容。
    /// </summary>
    string GetLegacyScheduledTasksFilePath();

    /// <summary>
    /// 获取向量数据库文件路径
    /// </summary>
    string GetVectorStoreFilePath();

    /// <summary>
    /// 获取工作区配置存储目录
    /// </summary>
    string GetWorkspacesDirectory();

    /// <summary>获取虚拟宠物养成存档路径。</summary>
    string GetPetProfileFilePath() => Path.Combine(GetAppDataDirectory(), "pet_profile.json");

    /// <summary>获取模型元数据、目录缓存与校准数据根目录。</summary>
    string GetModelMetadataDirectory() => Path.Combine(GetAppDataDirectory(), "ModelMetadata");

    string GetTokenCalibrationFilePath() => Path.Combine(GetModelMetadataDirectory(), "token-calibration.json");

    string GetTokenCalibrationKeyPath() => Path.Combine(GetModelMetadataDirectory(), "token-calibration.key");

    /// <summary>
    /// 获取指定工作区的知识文件目录
    /// </summary>
    string GetWorkspaceKnowledgeDirectory(string workspaceId);
}
