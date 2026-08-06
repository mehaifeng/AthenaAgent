using System.Collections.Generic;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 模型自配置工具（view_self_configuration / modify_self_configuration）的投影与修改服务。
/// 视图永远不直接暴露 config.json 原文；派生数据（模型目录等）只以摘要形式出现。
/// </summary>
public interface IConfigSurfaceService
{
    /// <summary>所有分区名（展示顺序）。</summary>
    IReadOnlyList<string> Sections { get; }

    /// <summary>所有可修改字段键（用于工具 schema 枚举）。</summary>
    IReadOnlyList<string> ModifiableKeys { get; }

    /// <summary>
    /// 构建配置投影。section 为空 / "All" 返回全部分区；分区名大小写不敏感，支持旧别名（Memory→Context）。
    /// 返回的 JSON 结构：{ sections: [...], summary: {...} }。
    /// </summary>
    object BuildView(AppConfig config, string? section);

    /// <summary>应用一次修改。失败时 Message 给出可行动的提示（合法键 / 取值范围）。</summary>
    (bool Success, string Message, object? Data) Apply(AppConfig config, string key, string value);
}
