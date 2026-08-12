using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class GeneralSettingsViewModel : ViewModelBase, IDisposable
{
    private const int PetResultLimit = 60;
    private readonly IPetDexCatalogService? _petDexCatalogService;
    private readonly ILocalizationService? _localizationService;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private IReadOnlyList<PetDexCatalogEntry> _petCatalog = [];
    private bool _catalogLoaded;
    private bool _disposed;

    public GeneralSettingsViewModel(
        AppSettingsState state,
        IPetDexCatalogService? petDexCatalogService = null,
        ILocalizationService? localizationService = null)
    {
        State = state;
        _petDexCatalogService = petDexCatalogService;
        _localizationService = localizationService;
        State.Config.PropertyChanged += OnConfigPropertyChanged;
        if (_localizationService is not null)
            _localizationService.LanguageChanged += OnLanguageChanged;
        ApplyPetCatalog(petDexCatalogService?.GetLocalCatalog() ?? BuiltInEntries());
    }

    public AppSettingsState State { get; }

    /// <summary>可选配色方案（与 Config.ColorScheme 一一对应；专有名词不本地化）。</summary>
    public string[] ColorSchemes { get; } = ["Default", "Solarized", "Cyberpunk", "Tokyo", "Monokai"];

    public ObservableCollection<PetDexCatalogItemViewModel> PetResults { get; } = [];

    [ObservableProperty]
    private string _petSearchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetCatalogStatusText))]
    private bool _isPetCatalogLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPetCatalogError))]
    [NotifyPropertyChangedFor(nameof(PetCatalogStatusText))]
    private string _petCatalogError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetCatalogStatusText))]
    private int _petMatchCount;

    public bool HasPetCatalogError => !string.IsNullOrWhiteSpace(PetCatalogError);

    public string PetCatalogStatusText => IsPetCatalogLoading
        ? L("Settings.General.VirtualPetLoading", "Loading PetDex…")
        : HasPetCatalogError
            ? PetCatalogError
            : PetMatchCount > PetResultLimit
                ? string.Format(
                    L("Settings.General.VirtualPetCountCapped", "Showing {0} of {1} pets"),
                    PetResultLimit,
                    PetMatchCount)
                : string.Format(
                    L("Settings.General.VirtualPetCount", "{0} pets"),
                    PetMatchCount);

    partial void OnPetSearchQueryChanged(string value) => RefreshPetResults();

    public async Task LoadPetCatalogAsync()
    {
        if (_catalogLoaded || IsPetCatalogLoading || _petDexCatalogService is null) return;
        IsPetCatalogLoading = true;
        PetCatalogError = string.Empty;
        try
        {
            var catalog = await _petDexCatalogService.GetCatalogAsync(_disposeCancellation.Token);
            if (_disposed) return;
            ApplyPetCatalog(catalog);
            _catalogLoaded = true;
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep the already-visible built-ins/installed pets, matching Hermes' local-first gallery.
            PetCatalogError = ex.Message;
        }
        finally
        {
            IsPetCatalogLoading = false;
        }
    }

    internal async Task SelectPetAsync(PetDexCatalogItemViewModel item)
    {
        if (item.IsBusy) return;
        item.IsBusy = true;
        PetCatalogError = string.Empty;
        try
        {
            if (!item.IsInstalled)
            {
                if (_petDexCatalogService is null)
                    throw new InvalidOperationException("PetDex catalog service is unavailable.");
                await _petDexCatalogService.InstallAsync(item.Entry, _disposeCancellation.Token);
                item.IsInstalled = true;
                item.Thumbnail = null;
                _petCatalog = _petCatalog
                    .Select(entry => entry.Slug.Equals(item.Slug, StringComparison.OrdinalIgnoreCase)
                        ? entry with { IsInstalled = true }
                        : entry)
                    .ToArray();
            }
            State.Config.VirtualPetSlug = item.Slug;
            State.Config.VirtualPetEnabled = true;
            SyncPetSelection();
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PetCatalogError = ex.Message;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>字号档位在 ComboBox 中的索引（0=最小 … 4=最大），映射到 Config.FontScale 字符串。</summary>
    public int FontScaleIndex
    {
        get => ConfigScaleToIndex(State.Config.FontScale);
        set
        {
            var next = IndexToConfigScale(value);
            if (!string.Equals(State.Config.FontScale, next, StringComparison.Ordinal))
                State.Config.FontScale = next;
        }
    }

    /// <summary>宠物漫游范围在设置下拉框中的索引。</summary>
    public int PetRoamAreaIndex
    {
        get => State.Config.VirtualPetRoamArea switch
        {
            VirtualPetRoamArea.LowerHalf => 0,
            VirtualPetRoamArea.LogTerminalBottom => 1,
            VirtualPetRoamArea.SessionListBottom => 2,
            _ => 0,
        };
        set
        {
            var next = value switch
            {
                1 => VirtualPetRoamArea.LogTerminalBottom,
                2 => VirtualPetRoamArea.SessionListBottom,
                _ => VirtualPetRoamArea.LowerHalf,
            };
            if (State.Config.VirtualPetRoamArea != next)
                State.Config.VirtualPetRoamArea = next;
        }
    }

    private void ApplyPetCatalog(IReadOnlyList<PetDexCatalogEntry> catalog)
    {
        _petCatalog = catalog;
        RefreshPetResults();
    }

    private void RefreshPetResults()
    {
        var query = PetSearchQuery.Trim();
        var filtered = _petCatalog
            .Where(entry => query.Length == 0
                            || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || entry.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || entry.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || entry.SubmittedBy.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.IsBuiltIn)
            .ThenByDescending(entry => entry.IsInstalled)
            .ThenByDescending(entry => entry.IsCurated)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PetMatchCount = filtered.Length;

        PetResults.Clear();
        foreach (var entry in filtered.Take(PetResultLimit))
        {
            var item = new PetDexCatalogItemViewModel(entry, this)
            {
                IsSelected = State.Config.VirtualPetEnabled
                             && entry.Slug.Equals(State.Config.VirtualPetSlug, StringComparison.OrdinalIgnoreCase)
            };
            PetResults.Add(item);
            if (!entry.IsInstalled && _petDexCatalogService is not null)
                _ = LoadThumbnailAsync(item);
        }
        OnPropertyChanged(nameof(PetCatalogStatusText));
    }

    private async Task LoadThumbnailAsync(PetDexCatalogItemViewModel item)
    {
        try
        {
            item.Thumbnail = await _petDexCatalogService!.GetThumbnailAsync(
                item.Entry,
                _disposeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SyncPetSelection()
    {
        foreach (var item in PetResults)
        {
            item.IsSelected = State.Config.VirtualPetEnabled
                              && item.Slug.Equals(
                                  State.Config.VirtualPetSlug,
                                  StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.FontScale))
            OnPropertyChanged(nameof(FontScaleIndex));
        else if (e.PropertyName == nameof(AppConfig.VirtualPetRoamArea))
            OnPropertyChanged(nameof(PetRoamAreaIndex));
        else if (e.PropertyName is nameof(AppConfig.VirtualPetSlug) or nameof(AppConfig.VirtualPetEnabled))
            SyncPetSelection();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(PetCatalogStatusText));

    private string L(string key, string fallback) =>
        _localizationService?.GetString(key, fallback) ?? fallback;

    private static IReadOnlyList<PetDexCatalogEntry> BuiltInEntries() =>
        PetDexPetLibrary.BuiltIns.Select(pet => new PetDexCatalogEntry(
            pet.Slug,
            pet.DisplayName,
            pet.Kind,
            pet.SubmittedBy,
            string.Empty,
            string.Empty,
            IsBuiltIn: true,
            IsInstalled: true,
            IsCurated: true)).ToArray();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
        State.Config.PropertyChanged -= OnConfigPropertyChanged;
        if (_localizationService is not null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private static int ConfigScaleToIndex(string? scale) => scale switch
    {
        "Tiny" => 0,
        "Small" => 1,
        "Medium" => 2,
        "Large" => 3,
        "Maximum" => 4,
        _ => 2,
    };

    private static string IndexToConfigScale(int index) => index switch
    {
        0 => "Tiny",
        1 => "Small",
        2 => "Medium",
        3 => "Large",
        4 => "Maximum",
        _ => "Medium",
    };
}

public sealed partial class PetDexCatalogItemViewModel : ObservableObject
{
    private readonly GeneralSettingsViewModel _owner;

    internal PetDexCatalogItemViewModel(PetDexCatalogEntry entry, GeneralSettingsViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        _isInstalled = entry.IsInstalled;
    }

    public PetDexCatalogEntry Entry { get; }
    public string Slug => Entry.Slug;
    public string DisplayName => Entry.DisplayName;
    public string Attribution => string.IsNullOrWhiteSpace(Entry.SubmittedBy)
        ? Entry.Slug
        : $"{Entry.Slug} · {Entry.SubmittedBy}";
    public bool IsRemoteOnly => !IsInstalled;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteOnly))]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    [RelayCommand]
    private Task UseAsync() => _owner.SelectPetAsync(this);
}
