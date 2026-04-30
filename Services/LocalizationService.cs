using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// 本地化服务实现，支持运行时切换语言
/// </summary>
public partial class LocalizationService : ObservableObject, ILocalizationService
{
    private readonly ILogger _logger = Log.ForContext<LocalizationService>();
    private readonly Dictionary<string, ResourceDictionary> _languages = new();
    private ResourceDictionary? _currentResources;
    private string _currentLanguage = "en-US";

    private readonly List<string> _availableLanguages = new() { "en-US", "zh-CN" };
    private readonly List<string> _availableLanguageNames = new() { "English", "中文" };

    /// <inheritdoc />
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (SetProperty(ref _currentLanguage, value))
            {
                OnPropertyChanged(nameof(CurrentLanguageIndex));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableLanguages => _availableLanguages.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableLanguageNames => _availableLanguageNames.AsReadOnly();

    /// <inheritdoc />
    public int CurrentLanguageIndex
    {
        get => _availableLanguages.IndexOf(CurrentLanguage);
        set
        {
            if (value >= 0 && value < _availableLanguages.Count)
            {
                SwitchLanguage(_availableLanguages[value]);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? LanguageChanged;

    public LocalizationService()
    {
        LoadLanguageResources();
    }

    private void LoadLanguageResources()
    {
        try
        {
            // 加载英文资源
            var enUsUri = new Uri("avares://Athena.UI/Assets/Locales/Locale.en-US.axaml");
            var enUsResources = AvaloniaXamlLoader.Load(enUsUri) as ResourceDictionary;
            if (enUsResources != null)
            {
                _languages["en-US"] = enUsResources;
                _logger.Debug("已加载英文语言资源");
            }

            // 加载中文资源
            var zhCnUri = new Uri("avares://Athena.UI/Assets/Locales/Locale.zh-CN.axaml");
            var zhCnResources = AvaloniaXamlLoader.Load(zhCnUri) as ResourceDictionary;
            if (zhCnResources != null)
            {
                _languages["zh-CN"] = zhCnResources;
                _logger.Debug("已加载中文语言资源");
            }

            // 默认使用系统语言或英文
            var systemLang = CultureInfo.CurrentUICulture.Name;
            var defaultLang = _availableLanguages.Contains(systemLang) ? systemLang : "en-US";
            SwitchLanguage(defaultLang);

            _logger.Information("本地化服务初始化完成，默认语言: {Language}", defaultLang);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载语言资源失败");
        }
    }

    /// <inheritdoc />
    public void SwitchLanguage(string languageCode)
    {
        if (!_languages.TryGetValue(languageCode, out var resources))
        {
            _logger.Warning("未找到语言资源: {LanguageCode}", languageCode);
            return;
        }

        _currentResources = resources;
        CurrentLanguage = languageCode;

        // 更新当前线程的文化信息
        try
        {
            var culture = new CultureInfo(languageCode);
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "设置文化信息失败: {LanguageCode}", languageCode);
        }

        // 触发属性变更通知
        OnPropertyChanged(nameof(CurrentLanguage));
        LanguageChanged?.Invoke(this, EventArgs.Empty);

        _logger.Information("语言已切换为: {LanguageCode}", languageCode);
    }

    /// <inheritdoc />
    public string GetString(string key)
    {
        return GetString(key, key);
    }

    /// <inheritdoc />
    public string GetString(string key, string defaultValue)
    {
        if (_currentResources == null)
        {
            return NormalizeResourceString(defaultValue);
        }

        if (_currentResources.TryGetValue(key, out var value) && value is string strValue)
        {
            return NormalizeResourceString(strValue);
        }

        return NormalizeResourceString(defaultValue);
    }

    private static string NormalizeResourceString(string value)
    {
        return value
            .Replace("\\r\\n", Environment.NewLine, StringComparison.Ordinal)
            .Replace("\\n", Environment.NewLine, StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public int GetLanguageIndex(string languageCode)
    {
        return _availableLanguages.IndexOf(languageCode);
    }
}
