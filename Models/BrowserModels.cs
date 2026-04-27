using System;
using System.Collections.Generic;

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
    Upload
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
    public bool SomEnabled { get; set; } = true;
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
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? Url { get; set; }
    public SomObservation? Observation { get; set; }
    public string? ExtractedText { get; set; }
    public BrowserActionEffect? Effect { get; set; }
    public BrowserRiskType Risk { get; set; } = BrowserRiskType.None;
    public bool RequiresUserConfirmation { get; set; }
    public bool IsRecoverableFailure { get; set; }
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
