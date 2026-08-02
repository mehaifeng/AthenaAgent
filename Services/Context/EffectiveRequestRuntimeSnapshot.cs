using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using System;
using System.Collections.Generic;

namespace Athena.UI.Services.Context;

/// <summary>
/// Immutable semantic inputs for one top-level user request. The SDK option instance and
/// tool objects are created once and are never updated during its tool loop.
/// </summary>
public sealed record EffectiveRequestRuntimeSnapshot(
    string RequestId,
    ChatClient ChatClient,
    EffectiveOpenAiModel MainModel,
    EffectiveOpenAiModel? ImageRecognitionModel,
    ResolvedModelMetadata ModelMetadata,
    ResolvedContextPolicy ContextPolicy,
    OpenAiModelExecutionPolicyIdentity ExecutionPolicyIdentity,
    ChatCompletionOptions ChatOptions,
    IReadOnlyList<ChatTool> ToolDefinitions,
    string ToolFingerprint,
    bool FunctionCallingEnabled,
    string BaseSystemPrompt,
    int TimeoutSeconds,
    int RequestFormatVersion,
    DateTimeOffset CapturedAtUtc,
    EffectiveContextPolicySnapshot? CompressionPolicySnapshot = null);
