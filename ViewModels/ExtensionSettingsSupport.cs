using Athena.UI.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.ViewModels;

internal static class ExtensionSettingsSupport
{
    public static void EnsureSettings(
        ObservableCollection<ExtensionProviderSettings> settings,
        IReadOnlyList<ExtensionProviderOption> providers)
    {
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
    }
}
