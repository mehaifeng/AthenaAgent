using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
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

        return CreateBinding(localizationService, Key, DefaultValue ?? Key);
    }

    internal static Binding CreateBinding(ILocalizationService localizationService, string key, string fallback)
        // Follow the service's existing property notification through Avalonia's binding lifetime.
        => new(nameof(ILocalizationService.CurrentLanguage))
        {
            Source = localizationService,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<string, string>(_ => localizationService.GetString(key, fallback))
        };
}
