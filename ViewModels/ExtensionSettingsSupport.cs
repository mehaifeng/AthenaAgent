using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.ViewModels;

internal static class ExtensionSettingsSupport
{
    public static void EnsureSettings(
        ObservableCollection<ExtensionProviderSettings> settings,
        IReadOnlyList<ExtensionProviderOption> providers,
        string selectedProviderId,
        Action<ExtensionProviderSettings> migrateSelected)
    {
        var wasEmpty = settings.Count == 0;
        foreach (var provider in providers)
        {
            if (settings.Any(item => item.ProviderId == provider.Id)) continue;
            settings.Add(new ExtensionProviderSettings
            {
                ProviderId = provider.Id,
                BaseUrl = provider.DefaultBaseUrl,
                Model = provider.DefaultModel,
                Voice = provider.DefaultVoice
            });
        }

        if (!wasEmpty) return;
        var selected = settings.FirstOrDefault(item => item.ProviderId == selectedProviderId);
        if (selected != null) migrateSelected(selected);
    }
}
