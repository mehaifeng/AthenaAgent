using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.SubAgents;

/// <summary>
/// 隔离的子代理执行循环（非流式逐轮）。每个子代理拥有独立消息列表与模型客户端，
/// 只把最终摘要返回。工具执行经外部传入的"全局工具闸"串行，规避并发副作用竞态。
/// </summary>
public sealed class SubAgentRunner
{
    private readonly IConfigService _configService;
    private readonly IFunctionRegistry _functionRegistry;
    private readonly ILogger _logger;

    public SubAgentRunner(IConfigService configService, IFunctionRegistry functionRegistry, ILogger logger)
    {
        _configService = configService;
        _functionRegistry = functionRegistry;
        _logger = logger.ForContext<SubAgentRunner>();
    }

    public async Task<SubAgentResult> RunAsync(
        SubAgentTaskInput task,
        SubAgentViewModel vm,
        Func<string, SemaphoreSlim?> gateFor,
        Action<Action> uiPost,
        CancellationToken cancellationToken)
    {
        var preset = SubAgentTypes.Resolve(task.AgentType);
        var config = _configService.Load();
        var effective = SubAgentModelResolver.Resolve(config);

        _logger.Information(
            "SubAgentRunner starting: Title={Title}, AgentType={AgentType}, Model={Model}",
            task.Title, task.AgentType, effective.Model);

        if (string.IsNullOrWhiteSpace(effective.ApiKey) || string.IsNullOrWhiteSpace(effective.Model))
        {
            _logger.Warning("SubAgentRunner model not configured: Title={Title}", task.Title);
            return Fail(vm, uiPost, task, "Sub-agent model is not configured (missing API key or model).");
        }

        ChatClient chatClient;
        try
        {
            var clientOptions = OpenAiClientOptionsFactory.Create(effective.BaseUrl, config.Timeout);

            var client = new OpenAIClient(new ApiKeyCredential(effective.ApiKey), clientOptions);
            chatClient = client.GetChatClient(effective.Model);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SubAgentRunner client initialization failed: Title={Title}", task.Title);
            return Fail(vm, uiPost, task, $"Failed to initialize sub-agent client: {ex.Message}");
        }

        var tools = _functionRegistry.GetToolDefinitions(preset.AllowedTools).OfType<ChatTool>().ToList();

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(preset.SystemPrompt),
            new UserChatMessage(task.Instruction)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = (float)effective.Temperature,
            MaxOutputTokenCount = effective.MaxTokens
        };
        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

        uiPost(() =>
        {
            vm.State = SubAgentState.Running;
            vm.RequestZone(SubAgentZone.Meditation);
            vm.CurrentAction = "thinking…";
        });

        var maxIterations = Math.Max(1, config.SubAgentMaxIterations);

        // 子代理是无人值守路径：审批闸门绝不弹窗，敏感/破坏性工具按静态策略处理。
        // 必须显式进入非交互作用域，覆盖来自主对话（dispatch_subagents 调用点）的交互式环境。
        using var approvalScope = Athena.UI.Services.ToolApprovalContext.EnterNonInteractive();
        _logger.Debug("SubAgentRunner entered NonInteractive approval scope: Title={Title}", task.Title);

        try
        {
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.Debug("SubAgentRunner thinking iteration: Title={Title}, Iteration={Iteration}", task.Title, iteration);
                uiPost(() =>
                {
                    vm.RequestZone(SubAgentZone.Meditation);
                    vm.CurrentAction = "thinking…";
                });

                ChatCompletion value;
                try
                {
                    var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
                    value = completion.Value;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "SubAgentRunner model call failed: Title={Title}, Iteration={Iteration}", task.Title, iteration);
                    return Fail(vm, uiPost, task, $"Model call failed: {ex.Message}");
                }

                var toolCalls = value.ToolCalls;
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    var finalText = value.Content.Count > 0 ? value.Content[0].Text : string.Empty;
                    _logger.Information(
                        "SubAgentRunner completed (no tool call): Title={Title}, Length={Length}",
                        task.Title, finalText?.Length ?? 0);
                    return Succeed(vm, uiPost, task, finalText);
                }

                // 把本轮（可能含正文 + 工具调用）的助手消息加入子上下文，供下一轮衔接。
                messages.Add(new AssistantChatMessage(value));

                foreach (var toolCall in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var zone = SubAgentZones.ForTool(toolCall.FunctionName);
                    var argsText = toolCall.FunctionArguments?.ToString() ?? "{}";

                    _logger.Debug(
                        "SubAgentRunner tool call: Title={Title}, Tool={Tool}, Zone={Zone}",
                        task.Title, toolCall.FunctionName, zone);
                    uiPost(() =>
                    {
                        vm.Step += 1;
                        vm.RequestZone(zone);
                        vm.CurrentAction = SummarizeToolCall(toolCall.FunctionName, argsText);
                    });

                    // 按工具类别取闸：浏览器/写入串行，其余（只读/网络/快速）无闸并行。
                    var gate = gateFor(toolCall.FunctionName);
                    FunctionResult toolResult;
                    if (gate != null)
                    {
                        await gate.WaitAsync(cancellationToken);
                    }
                    try
                    {
                        _logger.Debug(
                            "SubAgentRunner executing tool: Title={Title}, Tool={Tool}",
                            task.Title, toolCall.FunctionName);
                        toolResult = await _functionRegistry.ExecuteAsync(toolCall.FunctionName, argsText);
                    }
                    finally
                    {
                        gate?.Release();
                    }

                    var resultJson = toolResult.ToJson();
                    _logger.Debug(
                        "SubAgentRunner tool result: Title={Title}, Tool={Tool}, Preview={Preview}",
                        task.Title, toolCall.FunctionName, Preview(resultJson));
                    uiPost(() => vm.Log.Add(new SubAgentLogEntry
                    {
                        Kind = "tool",
                        Text = $"{toolCall.FunctionName} → {Preview(resultJson)}"
                    }));

                    messages.Add(new ToolChatMessage(toolCall.Id, resultJson));
                }
            }

            // 迭代用尽仍未给出最终答复。
            _logger.Warning(
                "SubAgentRunner iterations exhausted: Title={Title}, MaxIterations={MaxIterations}",
                task.Title, maxIterations);
            return Succeed(vm, uiPost, task, "(Reached the maximum number of steps without a final answer.)", success: false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !vm.WasCancelledByUser
                && vm.TimeoutAt != default
                && DateTime.UtcNow >= vm.TimeoutAt;
            _logger.Information(
                "SubAgentRunner terminated: Title={Title}, Reason={Reason}, TimeoutAt={TimeoutAt:o}",
                task.Title, timedOut ? "Timeout" : "Cancelled", vm.TimeoutAt);
            uiPost(() =>
            {
                vm.State = timedOut ? SubAgentState.Error : SubAgentState.Cancelled;
                vm.CurrentAction = timedOut ? "timeout" : "cancelled";
                vm.ErrorMessage = timedOut ? "Sub-agent execution timed out." : string.Empty;
            });
            return new SubAgentResult
            {
                Title = task.Title,
                AgentType = task.AgentType,
                Success = false,
                Summary = timedOut ? "Timed out." : "Cancelled."
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SubAgentRunner exception: Title={Title}", task.Title);
            return Fail(vm, uiPost, task, ex.Message);
        }
    }

    private static SubAgentResult Succeed(
        SubAgentViewModel vm, Action<Action> uiPost, SubAgentTaskInput task, string summary, bool success = true)
    {
        var text = string.IsNullOrWhiteSpace(summary) ? "(no output)" : summary.Trim();
        uiPost(() =>
        {
            vm.State = success ? SubAgentState.Done : SubAgentState.Error;
            if (success)
            {
                vm.RequestZone(SubAgentZone.Perch);
            }
            vm.CurrentAction = success ? "done" : "incomplete";
            vm.ResultSummary = text;
            vm.Log.Add(new SubAgentLogEntry { Kind = "assistant", Text = text });
        });

        return new SubAgentResult
        {
            Title = task.Title,
            AgentType = task.AgentType,
            Success = success,
            Summary = text
        };
    }

    private static SubAgentResult Fail(SubAgentViewModel vm, Action<Action> uiPost, SubAgentTaskInput task, string error)
    {
        uiPost(() =>
        {
            vm.State = SubAgentState.Error;
            vm.CurrentAction = "error";
            vm.ErrorMessage = error;
        });

        return new SubAgentResult
        {
            Title = task.Title,
            AgentType = task.AgentType,
            Success = false,
            Summary = "Error: " + error
        };
    }

    // 工具调用摘要恒为单行、截断（与主界面工具卡摘要风格一致）。
    private static string SummarizeToolCall(string name, string argsJson)
    {
        var compact = argsJson.Replace("\n", " ").Replace("\r", " ").Trim();
        if (compact.Length > 80)
        {
            compact = compact[..80] + "…";
        }
        return $"{name} {compact}";
    }

    private static string Preview(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
