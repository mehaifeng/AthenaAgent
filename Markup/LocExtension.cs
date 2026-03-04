using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Markup;

/// <summary>
/// XAML markup extension for localized string binding.
/// Usage: {loc:Loc KeyName} or {loc:Loc Key=KeyName}
/// </summary>
public class LocExtension : MarkupExtension
{
    /// <summary>
    /// The resource key to look up
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Default value if key is not found
    /// </summary>
    public string? DefaultValue { get; set; }

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localizationService = App.Services?.GetService(typeof(ILocalizationService)) as ILocalizationService;

        if (localizationService == null)
        {
            // Return key as fallback during design time or before service initialization
            return DefaultValue ?? Key;
        }

        // Create a binding to the LocalizedStringProvider
        var binding = new Binding
        {
            Source = new LocalizedStringProvider(localizationService, Key, DefaultValue),
            Path = nameof(LocalizedStringProvider.GetString),
            Mode = BindingMode.OneWay
        };

        return binding;
    }
}

/// <summary>
/// Helper class that provides localized strings and notifies when language changes
/// </summary>
public class LocalizedStringProvider : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ILocalizationService _localizationService;
    private readonly string _key;
    private readonly string? _defaultValue;

    public LocalizedStringProvider(ILocalizationService localizationService, string key, string? defaultValue)
    {
        _localizationService = localizationService;
        _key = key;
        _defaultValue = defaultValue;

        // Subscribe to language changes
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GetString)));
    }

    public string GetString => _localizationService.GetString(_key, _defaultValue ?? _key);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
