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
    /// 获取任务调度器文件路径
    /// </summary>
    string GetTaskSchedulerFilePath();

    /// <summary>
    /// 获取向量数据库文件路径
    /// </summary>
    string GetVectorStoreFilePath();
}
