using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 本地化服务接口，提供多语言支持
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>
    /// 当前语言代码（如 "en-US", "zh-CN"）
    /// </summary>
    string CurrentLanguage { get; }

    /// <summary>
    /// 可用的语言代码列表
    /// </summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>
    /// 可用的语言显示名称列表
    /// </summary>
    IReadOnlyList<string> AvailableLanguageNames { get; }

    /// <summary>
    /// Gets the index of a language code
    /// </summary>
    /// <param name="languageCode">The language code</param>
    /// <returns>The index, or -1 if not found</returns>
    int GetLanguageIndex(string languageCode);

    /// <summary>
    /// 切换语言
    /// </summary>
    /// <param name="languageCode">语言代码</param>
    void SwitchLanguage(string languageCode);

    /// <summary>
    /// 获取本地化字符串
    /// </summary>
    /// <param name="key">字符串键</param>
    /// <returns>本地化后的字符串</returns>
    string GetString(string key);

    /// <summary>
    /// 获取本地化字符串，如果不存在则返回默认值
    /// </summary>
    /// <param name="key">字符串键</param>
    /// <param name="defaultValue">默认值</param>
    /// <returns>本地化后的字符串</returns>
    string GetString(string key, string defaultValue);

    /// <summary>
    /// 语言变更事件
    /// </summary>
    event EventHandler? LanguageChanged;
}
