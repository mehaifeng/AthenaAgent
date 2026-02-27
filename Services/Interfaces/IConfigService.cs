using Athena.UI.Models;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 配置服务接口
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// 异步加载配置
    /// </summary>
    Task<AppConfig> LoadAsync();

    /// <summary>
    /// 同步加载配置（用于启动时避免死锁）
    /// </summary>
    AppConfig Load();

    /// <summary>
    /// 保存配置
    /// </summary>
    Task SaveAsync(AppConfig config);

    /// <summary>
    /// 获取配置文件路径
    /// </summary>
    string ConfigFilePath { get; }
}
