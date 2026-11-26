using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static readonly List<string> _availableLanguages = new() { "en-US", "zh-CN" };
    private static readonly IReadOnlyList<string> LanguageNames = Array.AsReadOnly(new[] { "English", "中文" });
    private static readonly IReadOnlyList<string> LanguageCodes = _availableLanguages.AsReadOnly();

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
    public IReadOnlyList<string> AvailableLanguages => LanguageCodes;

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableLanguageNames => LanguageNames;

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
        var systemLanguage = CultureInfo.CurrentUICulture.Name;
        SwitchLanguage(_availableLanguages.Contains(systemLanguage) ? systemLanguage : "en-US");
        _logger.Information("Localization service initialized, default language: {Language}", CurrentLanguage);
    }

    /// <inheritdoc />
    public void SwitchLanguage(string languageCode)
    {
        if (!_availableLanguages.Contains(languageCode))
        {
            _logger.Warning("Language resource not found: {LanguageCode}", languageCode);
            return;
        }
        if (!_languages.TryGetValue(languageCode, out var resources))
        {
            try
            {
                var uri = new Uri($"avares://Athena.UI/Assets/Locales/Locale.{languageCode}.axaml");
                resources = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
                _languages.Add(languageCode, resources);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load language resources: {LanguageCode}", languageCode);
                return;
            }
        }
        var alreadySelected = CurrentLanguage == languageCode;
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
            _logger.Warning(ex, "Failed to set culture info: {LanguageCode}", languageCode);
        }

        // 触发属性变更通知
        if (alreadySelected) OnPropertyChanged(nameof(CurrentLanguage));
        LanguageChanged?.Invoke(this, EventArgs.Empty);

        _logger.Information("Language switched to: {LanguageCode}", languageCode);
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
        if (!value.Contains('\\')) return value;
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
