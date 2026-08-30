using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// cron 任务的持久化端。只做文件 I/O：不校验业务规则、不创建会话、不碰 Avalonia。
/// </summary>
public interface ICronTaskStore
{
    /// <summary>
    /// 读取全部任务。单条记录损坏时被隔离并计数，绝不阻止应用启动。
    /// </summary>
    CronTaskLoadResult Load();

    /// <summary>原子写入（临时文件 + 替换），避免半截文件。</summary>
    Task SaveAsync(IReadOnlyList<CronTask> tasks);

    /// <summary>删除遗留的旧调度文件（若存在），返回是否真的删掉了。</summary>
    bool DeleteLegacyStoreIfPresent();
}
