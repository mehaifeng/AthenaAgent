using System.Linq;
using Athena.UI.Models;

namespace Athena.UI.ViewModels;

/// <summary>UI-thread-only projection. Does not add protocol messages or change archive fields.</summary>
public static class PtcProgressProjection
{
    public static void Apply(ChatMessage bubble, PtcProgress progress)
    {
        var parent = bubble.Segments.Where(segment => segment.IsToolCallGroup).SelectMany(segment => segment.ToolCalls)
            .FirstOrDefault(call => call.ToolCallId == progress.ParentToolCallId && call.Name == "execute_program");
        if (parent is null) return; // A retired bubble has intentionally stopped observing this run.
        var children = parent.EnsureProgramCalls(progress.RunId);
        if (progress.Sequence <= parent.ProgramSequence) return;
        parent.ProgramSequence = progress.Sequence;
        if (progress.CallId is not null)
        {
            var child = children.FirstOrDefault(item => item.ToolCallId == progress.CallId);
            if (child is null)
            {
                child = new ToolCallEntry { ToolCallId = progress.CallId, Name = progress.ToolName ?? string.Empty,
                    Summary = progress.ToolName ?? string.Empty, Status = ToolCallStatus.Running };
                children.Add(child);
            }
            child.Result = progress.ErrorCode ?? progress.Kind;
            if (progress.Kind == "CallFinished")
                child.Status = progress.CallState is null
                    ? progress.ErrorCode is null ? ToolCallStatus.Success : ToolCallStatus.Failed
                    : progress.CallState == "Succeeded" ? ToolCallStatus.Success : ToolCallStatus.Failed;
        }
        if (progress.Kind == "RunFinished")
        {
            foreach (var child in children.Where(item => item.IsRunning))
            {
                child.Status = ToolCallStatus.Failed;
                child.Result = progress.ErrorCode ?? "outcome_unknown";
            }
        }
        parent.NotifyProgramCallsChanged();
    }
}
