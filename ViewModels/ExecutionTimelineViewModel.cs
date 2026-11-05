using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.ExecutionTimeline;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.ViewModels;

/// <summary>Presentation-only projection of one bubble's metadata, independent of the selected conversation.</summary>
public sealed partial class ExecutionTimelineViewModel : ObservableObject, IDisposable
{
    private readonly ExecutionTimelineService? _service;
    private readonly string _messageId;
    private readonly ILocalizationService? _localization;
    private long? _selectedId;
    private string? _loadError;

    public ExecutionTimelineViewModel(ExecutionTimelineService? service, string messageId, ILocalizationService? localization)
    {
        _service = service;
        _messageId = messageId;
        _localization = localization;
        if (_localization != null) _localization.LanguageChanged += OnLanguageChanged;
        _notice = Get("Timeline.Loading", "Loading execution record…");
        _detail = Get("Timeline.DetailHint", "Select a bar for details.");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    private ExecutionTimelineSnapshot? _snapshot;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _notice;
    [ObservableProperty] private string _detail;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private int _languageVersion;
    public bool HasSnapshot => Snapshot != null;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_service is null)
        {
            _loadError = "storage_error";
            Snapshot = null;
            Notice = Get("Timeline.StorageError", "The execution timeline service is unavailable.");
            IsLive = false;
            return;
        }
        var loaded = await _service.LoadAsync(_messageId, cancellationToken);
        _loadError = loaded.ErrorCode;
        Snapshot = loaded.Snapshot;
        IsLive = loaded.IsLive;
        if (Snapshot is null)
        {
            Summary = string.Empty;
            Notice = loaded.ErrorCode is null
                ? Get("Timeline.Empty", "No execution record was collected for this reply.")
                : Get("Timeline.StorageError", "Could not read the execution record.");
            return;
        }
        UpdateText();
    }

    public void Select(long? spanId)
    {
        _selectedId = spanId;
        UpdateDetail();
    }

    private void UpdateText()
    {
        if (Snapshot is not { } snapshot) return;
        Summary = string.Format(CultureInfo.CurrentCulture,
            Get("Timeline.Summary", "Elapsed {0} · model attempts {1} · outer attempts {2} (started {3}) · PTC attempts {4} (started {5})"),
            FormatDuration(snapshot.DurationUs), snapshot.ModelAttempts, snapshot.OuterTools, snapshot.OuterToolsStarted, snapshot.PtcAttempts, snapshot.PtcStarted);
        Notice = snapshot.PersistenceFailed
            ? Get("Timeline.NotSaved", "This record could not be saved.")
            : snapshot.IsPartial
                ? Get("Timeline.Partial", "This record is incomplete.")
                : IsLive ? snapshot.State == ExecutionTimelineState.Running
                    ? Get("Timeline.Live", "Running · timeline updates live")
                    : Get("Timeline.Saving", "Saving execution record…")
                : string.Empty;
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        var span = Snapshot?.Spans.FirstOrDefault(item => item.Id == _selectedId);
        if (span is null)
        {
            Detail = Get("Timeline.DetailHint", "Select a bar for details.");
            return;
        }
        var end = span.EndUs ?? Snapshot!.DurationUs;
        var state = Get("Timeline.State." + span.State, span.State.ToString());
        var usage = span.Kind != ExecutionTimelineKind.ModelRequest ? string.Empty
            : span.InputTokens is { } input && span.OutputTokens is { } output
                ? string.Format(CultureInfo.CurrentCulture, Get("Timeline.Usage", "Input {0} · output {1} · cached {2} · first text {3}"),
                    input, output, span.CachedInputTokens?.ToString(CultureInfo.CurrentCulture) ?? "—",
                    span.FirstTextUs is { } first ? FormatDuration(Math.Max(0, first - span.StartUs)) : "—")
                : Get("Timeline.UsageMissing", "Provider did not report usage") + " · "
                    + Get("Timeline.FirstText", "First text") + " "
                    + (span.FirstTextUs is { } firstWithoutUsage ? FormatDuration(Math.Max(0, firstWithoutUsage - span.StartUs)) : "—");
        var parent = span.ParentId is { } parentId ? Snapshot?.Spans.FirstOrDefault(item => item.Id == parentId) : null;
        var parentText = parent is null ? string.Empty : " · ↳ " + NameFor(parent);
        Detail = NameFor(span) + parentText + " · " + state + Environment.NewLine
            + Get("Timeline.Start", "Start") + " " + FormatDuration(span.StartUs) + " · "
            + Get("Timeline.Duration", "Duration") + " " + FormatDuration(Math.Max(0, end - span.StartUs))
            + (string.IsNullOrEmpty(usage) ? string.Empty : Environment.NewLine + usage);
    }

    public static string DisplayName(ExecutionTimelineSpan span) => span.Kind switch
    {
        ExecutionTimelineKind.ModelRequest => $"MODEL #{span.Round ?? 0}" + (span.Attempt is > 1 ? $" / retry {span.Attempt - 1}" : string.Empty),
        ExecutionTimelineKind.ScriptRun => "execute_program",
        ExecutionTimelineKind.ToolQueue => "tool queue",
        ExecutionTimelineKind.ConversationQueue => "conversation queue",
        ExecutionTimelineKind.Approval => "approval",
        ExecutionTimelineKind.RetryWait => "retry backoff",
        ExecutionTimelineKind.Compression => "context compression",
        ExecutionTimelineKind.Preparation => "request preparation",
        _ => span.Name
    };

    public string NameFor(ExecutionTimelineSpan span) => span.Kind switch
    {
        ExecutionTimelineKind.ToolQueue => Get("Timeline.Kind.ToolQueue", "tool queue"),
        ExecutionTimelineKind.ConversationQueue => Get("Timeline.Kind.ConversationQueue", "conversation queue"),
        ExecutionTimelineKind.Approval => Get("Timeline.Kind.Approval", "approval"),
        ExecutionTimelineKind.RetryWait => Get("Timeline.Kind.RetryWait", "retry backoff"),
        ExecutionTimelineKind.Compression => Get("Timeline.Kind.Compression", "context compression"),
        ExecutionTimelineKind.Preparation => Get("Timeline.Kind.Preparation", "request preparation"),
        ExecutionTimelineKind.ToolCall when span.Name == "unavailable_tool" => Get("Timeline.UnavailableTool", "unavailable tool"),
        _ => DisplayName(span)
    };

    public string LaneDescription(ExecutionTimelineLane lane)
        => Get("Timeline.Lane." + lane, DefaultLaneDescription(lane));

    public static string DefaultLaneDescription(ExecutionTimelineLane lane) => lane switch
    {
        ExecutionTimelineLane.Model => "main model",
        ExecutionTimelineLane.Tools => "outer calls",
        ExecutionTimelineLane.Script => "script lifetime",
        ExecutionTimelineLane.PtcTools => "PTC child tools",
        _ => "waits / approval"
    };

    public static string FormatDuration(long microseconds)
    {
        var ms = microseconds / 1000d;
        if (ms < 1000) return ms.ToString("0.##", CultureInfo.CurrentCulture) + " ms";
        var seconds = ms / 1000d;
        if (seconds < 60) return seconds.ToString("0.00", CultureInfo.CurrentCulture) + " s";
        return TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.ff" : @"m\:ss\.ff", CultureInfo.CurrentCulture);
    }

    private string Get(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // The chart draws its labels itself, so make a language change invalidate that surface
        // even when a completed trace no longer has the live refresh timer.
        LanguageVersion++;
        if (Snapshot is null) Notice = _loadError is null
            ? Get("Timeline.Empty", "No execution record was collected for this reply.")
            : Get("Timeline.StorageError", "Could not read the execution record.");
        else UpdateText();
    }
    public void Dispose()
    {
        if (_localization != null) _localization.LanguageChanged -= OnLanguageChanged;
    }
}
