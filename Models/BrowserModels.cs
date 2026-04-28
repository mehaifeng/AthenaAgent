using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Athena.UI.Models;

public enum BrowserActionType
{
    None,
    Navigate,
    Observe,
    Click,
    Type,
    PressKey,
    Scroll,
    Wait,
    ExtractText,
    Close,
    Finish,
    Upload,
    Search,
    GoBack,
    Input,
    SwitchTab,
    CloseTab,
    SearchPage,
    FindElements,
    FindText,
    Screenshot,
    SaveAsPdf,
    DropdownOptions,
    SelectDropdown,
    Evaluate
}

public enum BrowserObservationMode
{
    DomOnly,
    VisionWithSom
}

public enum BrowserRiskType
{
    None,
    Navigation,
    Login,
    FormSubmit,
    Payment,
    Download,
    Upload,
    DestructiveAction,
    LocalNetwork,
    Captcha,
    TwoFactor
}

public enum BrowserTaskCompletionStatus
{
    Unknown,
    Completed,
    CompletedWithRecoverableFailures,
    Failed,
    MaxStepsReached
}

public enum BrowserTaskGoalType
{
    Navigate,
    Fill,
    Select,
    Upload,
    SetChecked,
    Click,
    Submit,
    Extract,
    Verify
}

public enum BrowserTaskGoalStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public class BrowserViewport
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 900;
    public double DeviceScaleFactor { get; set; } = 1.0;
}

public class BrowserSessionOptions
{
    public bool Headless { get; set; } = true;
    public bool PersistSession { get; set; }
    public bool DownloadEnabled { get; set; }
    public BrowserObservationMode ObservationMode { get; set; } = BrowserObservationMode.VisionWithSom;
    public int SomMaxElements { get; set; } = 80;
    public bool SomIncludeText { get; set; } = true;
    public double ScreenshotScale { get; set; } = 1.0;
    public int ImageQuality { get; set; } = 85;
    public BrowserViewport Viewport { get; set; } = new();
    public int OperationTimeoutSeconds { get; set; } = 30;
    public int SessionTtlMinutes { get; set; } = 10;
}

public enum BrowserRuntimeState
{
    Unknown,
    Ready,
    BrowserNotInstalled,
    PackageUnavailable,
    Error
}

public class BrowserRuntimeStatus
{
    public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Unknown;
    public bool IsReady => State == BrowserRuntimeState.Ready;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}

public class BrowserRuntimeInstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

public class BrowserSessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastAccessedAt { get; set; } = DateTime.Now;
    public string? CurrentUrl { get; set; }
    public bool IsPersistent { get; set; }
    public int SessionTtlMinutes { get; set; } = 10;
}

public class BrowserBoundingBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class SomElement
{
    public string ElementId { get; set; } = string.Empty;
    public int Index { get; set; }
    public string? StableKey { get; set; }
    public string TagName { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? Text { get; set; }
    public string? AriaLabel { get; set; }
    public string? Placeholder { get; set; }
    public string? Href { get; set; }
    public string? Value { get; set; }
    public string? InputType { get; set; }
    public BrowserBoundingBox BoundingBox { get; set; } = new();
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsEditable { get; set; }
    public bool? IsChecked { get; set; }
    public bool IsSensitive { get; set; }
    public string? Selector { get; set; }
}

public class SomObservation
{
    public string SessionId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string ScreenshotBase64 { get; set; } = string.Empty;
    public string ScreenshotMimeType { get; set; } = "image/png";
    public string? AnnotatedScreenshotPath { get; set; }
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public int ScrollX { get; set; }
    public int ScrollY { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public List<SomElement> Elements { get; set; } = new();
}

public class SomAnnotationRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Title { get; set; }
    public byte[] ScreenshotPng { get; set; } = Array.Empty<byte>();
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public int ScrollX { get; set; }
    public int ScrollY { get; set; }
    public int MaxElements { get; set; } = 80;
    public bool IncludeElementText { get; set; } = true;
    public bool DrawAnnotations { get; set; } = true;
    public double ScreenshotScale { get; set; } = 1.0;
    public int ImageQuality { get; set; } = 85;
    public List<SomElement> Elements { get; set; } = new();
}

public class BrowserActionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public BrowserActionType Action { get; set; } = BrowserActionType.None;
    public string? Url { get; set; }
    public string? ElementId { get; set; }
    public string? Text { get; set; }
    public string? FilePath { get; set; }
    public string? Key { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int WaitMilliseconds { get; set; }
    public string? Reason { get; set; }
    public double? Confidence { get; set; }
    public bool IsTerminalFailure { get; set; }
}

public class BrowserActionEffect
{
    public string? ElementId { get; set; }
    public string? TargetStableKey { get; set; }
    public string? TargetSelector { get; set; }
    public string? RequestedText { get; set; }
    public string? ValueBefore { get; set; }
    public string? ValueAfter { get; set; }
    public bool Changed { get; set; }
    public bool Skipped { get; set; }
    public bool MatchesRequestedValue { get; set; }
    public string? SkipReason { get; set; }
}

public class BrowserActionResult
{
    public bool Success { get; set; }
    public BrowserActionType Action { get; set; } = BrowserActionType.None;
    public string? ActionName { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public SomObservation? Observation { get; set; }
    public string? ExtractedText { get; set; }
    public string? ExtractedContent { get; set; }
    public bool IncludeExtractedContentOnlyOnce { get; set; }
    public string? LongTermMemory { get; set; }
    public bool IsDone { get; set; }
    public bool? CompletionSuccess { get; set; }
    public string? Error { get; set; }
    public bool PageChanged { get; set; }
    public List<string> Attachments { get; set; } = new();
    public Dictionary<string, string?> Metadata { get; set; } = new();
    public BrowserActionEffect? Effect { get; set; }
    public BrowserRiskType Risk { get; set; } = BrowserRiskType.None;
    public bool RequiresUserConfirmation { get; set; }
    public bool IsRecoverableFailure { get; set; }
}

public class BrowserAgentOutput
{
    public string? Thinking { get; set; }
    public string? EvaluationPreviousGoal { get; set; }
    public string? Memory { get; set; }
    public string? NextGoal { get; set; }
    public int? CurrentPlanItem { get; set; }
    public List<string> PlanUpdate { get; set; } = new();
    public List<BrowserAgentAction> Action { get; set; } = new();
}

public class BrowserAgentAction
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetString(string name)
    {
        if (!Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    public int? GetInt32(string name)
    {
        if (!Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(GetString(name), out var parsed) ? parsed : null;
    }

    public double? GetDouble(string name)
    {
        if (!Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return double.TryParse(GetString(name), out var parsed) ? parsed : null;
    }

    public bool? GetBool(string name)
    {
        if (!Parameters.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(GetString(name), out var parsed) ? parsed : null
        };
    }
}

public class BrowserAgentStepInfo
{
    public int StepNumber { get; set; }
    public int MaxSteps { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LongTermMemory { get; set; } = string.Empty;
    public string ReadState { get; set; } = string.Empty;
    public bool UseVision { get; set; }
}

public class BrowserActionDefinition
{
    public string Name { get; set; } = string.Empty;
    public BrowserActionType Type { get; set; } = BrowserActionType.None;
    public string Description { get; set; } = string.Empty;
    public string ParametersJsonSchema { get; set; } = "{}";
    public bool TerminatesSequence { get; set; }
    public bool IsTerminal { get; set; }
}

public class BrowserTabInfo
{
    public string TabId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}

public class BrowserPageInfo
{
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public int ScrollX { get; set; }
    public int ScrollY { get; set; }
    public int PixelsAbove { get; set; }
    public int PixelsBelow { get; set; }
}

public class BrowserStateSummary
{
    public string SessionId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Title { get; set; }
    public List<BrowserTabInfo> Tabs { get; set; } = new();
    public BrowserPageInfo PageInfo { get; set; } = new();
    public SomObservation? Observation { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public string? ScreenshotMimeType { get; set; }
    public List<SomElement> Elements { get; set; } = new();
    public List<string> BrowserErrors { get; set; } = new();
    public List<string> ClosedPopupMessages { get; set; } = new();
    public List<string> PendingDownloads { get; set; } = new();
    public Dictionary<string, string?> Metadata { get; set; } = new();
    public int DomVersion { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}

public class BrowserTaskRequest
{
    public string Instruction { get; set; } = string.Empty;
    public string? StartUrl { get; set; }
    public int? MaxSteps { get; set; }
    public bool CloseSessionOnCompletion { get; set; } = true;
    public List<BrowserActionRequest> PlannedActions { get; set; } = new();
}

public class BrowserTaskPlan
{
    public string? Summary { get; set; }
    public List<BrowserTaskGoal> Goals { get; set; } = new();
}

public class BrowserTaskGoal
{
    public string GoalId { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public BrowserTaskGoalType Type { get; set; } = BrowserTaskGoalType.Verify;
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Url { get; set; }
    public bool? Checked { get; set; }
    public bool Optional { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public BrowserTaskGoalStatus Status { get; set; } = BrowserTaskGoalStatus.Pending;
    public string? Message { get; set; }
}

public class BrowserGoalResult
{
    public string GoalId { get; set; } = string.Empty;
    public int Index { get; set; }
    public BrowserTaskGoalType Type { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    public BrowserTaskGoalStatus Status { get; set; }
    public int Attempts { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ElementId { get; set; }
    public List<string> Evidence { get; set; } = new();
}

public class BrowserTaskResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public List<string> Evidence { get; set; } = new();
    public int ActionsTakenCount { get; set; }
    public bool RequiresUserInput { get; set; }
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public SomObservation? FinalObservation { get; set; }
    public List<BrowserActionResult> ActionHistory { get; set; } = new();
    public BrowserTaskCompletionStatus CompletionStatus { get; set; } = BrowserTaskCompletionStatus.Unknown;
    public List<BrowserGoalResult> GoalResults { get; set; } = new();
}
