using Athena.UI.Services.Functions;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// Function 注册表
/// </summary>
public class FunctionRegistry : IFunctionRegistry
{
    private readonly Dictionary<string, Func<string, Task<FunctionResult>>> _executors = new();
    private readonly List<ChatTool> _tools = new();
    private readonly ILogger _logger;
    private readonly IConfigService? _configService;
    private int? _cachedToolDeclarationTokens = null;

    public bool HasFunctions => _tools.Count > 0;

    public FunctionRegistry(
        ProactiveMessagingFunctions proactiveFunctions,
        KnowledgeBaseFunctions knowledgeFunctions,
        ConfigurationFunctions configFunctions,
        CliFunctions cliFunctions,
        WebSearchFunctions webSearchFunctions,
        IConfigService? configService,
        ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<FunctionRegistry>();

        // --- CLI Control ---
        RegisterFunction("execute_terminal_command", cliFunctions.ExecuteTerminalCommandAsync,
            $"Executes a command or command chain via shell on the current OS ({(OperatingSystem.IsWindows() ? "Windows — cmd.exe" : "POSIX — zsh")}). " +
            "Supports full shell syntax: pipes (|), redirects (>), chains (&&, ||), glob patterns (*, ?), and subshells. " +
            "Examples: 'cat file.txt | grep pattern' / 'ls -la *.cs > output.txt' / 'find . -name \"*.md\" | head -20'. " +
            "For file operations: use 'cat/read', 'echo/printf/write', 'rm/del', 'mv/move', 'cp/copy', 'mkdir', 'ls/dir'.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "Shell command or command chain with pipes, redirects, and glob patterns. Examples: 'git status', 'cat file.txt | grep foo', 'ls -la *.cs'" },
                    workingDirectory = new { type = "string", description = "The directory where the command should be executed." },
                    waitForExit = new { type = "boolean", description = "If true (default), waits for completion. Set to false for GUI apps or background tasks.", @default = true }
                },
                required = new[] { "command" }
            });

        // --- Tasks & Reminders ---
        RegisterFunction("create_task", proactiveFunctions.ScheduleProactiveMessage,
            "Schedules a proactive conversation task. Use this to set reminders or follow-ups based on explicit or implicit time mentions (e.g., 'remind me in 5m', 'next Friday', 'later').",
            new
            {
                type = "object",
                properties = new
                {
                    scheduledTime = new { type = "string", description = "Natural language time description (e.g., '2024-08-15 09:00', 'in 2 hours', 'every Friday 3pm')." },
                    intent = new { type = "string", description = "The topic or message for the proactive session." },
                    recurrence = new { type = "string", description = "Recurrence pattern: 'none' (default), 'daily', 'weekly', 'every N days'.", @default = "none" }
                },
                required = new[] { "scheduledTime", "intent" }
            });

        RegisterFunction("cancel_task", proactiveFunctions.CancelScheduledMessage,
            "Cancels a previously scheduled task using its unique ID.",
            new
            {
                type = "object",
                properties = new { taskId = new { type = "string", description = "The ID of the task to cancel." } },
                required = new[] { "taskId" }
            });

        RegisterFunction("list_tasks", proactiveFunctions.ListScheduledMessages,
            "Lists all currently active scheduled tasks. Use this to review or manage pending reminders.",
            new { type = "object", properties = new { } });

        // --- Long-term Memory ---
        RegisterFunction("create_new_memory", knowledgeFunctions.CreateKnowledgeFile,
            "Creates a new memory record in the knowledge base. ONLY use this when recall_from_memory confirms no existing record covers this information. Never call this without searching first.",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "Relative path for the memory file (e.g., 'user_preferences/coding_style.md')." },
                    content = new { type = "string", description = "The detailed information to be remembered." }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("recall_from_memory", knowledgeFunctions.SearchKnowledgeBase,
            "Searches across all memory domains using semantic vector search. MUST be called before create_new_memory — this is mandatory, no exceptions. Also call this whenever the user asks something that may rely on past context.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query or keywords." },
                    maxResults = new { type = "integer", description = "Maximum number of results to return.", @default = 3 }
                },
                required = new[] { "query" }
            });

        // --- Web Search (始终注册，执行时检查是否启用) ---
        RegisterFunction("web_search", webSearchFunctions.WebSearchAsync,
            "Searches the web for current information. Use this when you need up-to-date information that may not be in your training data, such as recent news, current events, or real-time data. NOTE: This tool requires Web Search to be enabled in settings.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query or question to look up on the web." },
                    maxResults = new { type = "integer", description = "Maximum number of search results to return.", @default = 5 }
                },
                required = new[] { "query" }
            });

        // --- Self-Configuration ---
        RegisterFunction("modify_self_configuration", configFunctions.ModifyAppConfig,
            "Modifies your own operational parameters (e.g., Temperature, Language, Theme) based on user mood or explicit requests.",
            new
            {
                type = "object",
                properties = new
                {
                    key = new { type = "string", description = "Parameter name (e.g., 'Temperature', 'Language', 'Theme')." },
                    value = new { type = "string", description = "New value for the parameter." }
                },
                required = new[] { "key", "value" }
            });

        RegisterFunction("view_self_configuration", configFunctions.GetAppConfig,
            "Views your current operational configuration. Use this to check your internal state.",
            new
            {
                type = "object",
                properties = new { section = new { type = "string", description = "Category: 'AI', 'Appearance', 'Memory', or 'All'.", @default = "All" } }
            });

        _logger.Information("FunctionRegistry initialized with {Count} functions", _tools.Count);
    }

    private void RegisterFunction(string name, Delegate function, string description, object parameters)
    {
        var tool = ChatTool.CreateFunctionTool(
            name,
            description,
            BinaryData.FromString(JsonSerializer.Serialize(parameters))
        );

        _tools.Add(tool);

        _executors[name] = async (argsJson) =>
        {
            try
            {
                var args = JsonSerializer.Deserialize<JsonElement>(argsJson);
                var method = function.Method;
                var parameters = method.GetParameters();
                var argsArray = new object?[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    if (args.TryGetProperty(param.Name!, out var prop))
                    {
                        argsArray[i] = JsonSerializer.Deserialize(prop.GetRawText(), param.ParameterType);
                    }
                    else if (param.HasDefaultValue)
                    {
                        argsArray[i] = param.DefaultValue;
                    }
                    else
                    {
                        argsArray[i] = param.ParameterType.IsValueType
                            ? Activator.CreateInstance(param.ParameterType)
                            : null;
                    }
                }

                var result = function.DynamicInvoke(argsArray);
                if (result is Task<FunctionResult> taskResult)
                {
                    return await taskResult;
                }

                return FunctionResult.FailureResult("Function return type mismatch");
            }
            catch (JsonException jsonEx)
            {
                _logger.Warning("LLM provided invalid JSON for function {FunctionName}. Error: {Message}", name, jsonEx.Message);
                return FunctionResult.FailureResult($"Invalid JSON format in arguments: {jsonEx.Message}. Please ensure you output valid JSON.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Execution failed for function {FunctionName}", name);
                return FunctionResult.FailureResult($"Execution failed: {ex.Message}");
            }
        };

        _logger.Debug("Registered function: {Name}", name);
    }

    public IEnumerable<object> GetToolDefinitions() => _tools;

    public IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames)
    {
        var nameSet = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        return _tools.Where(t => t is ChatTool chatTool && nameSet.Contains(chatTool.FunctionName));
    }

    public async Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson)
    {
        _logger.Information("Executing function: {FunctionName}", functionName);

        if (!_executors.TryGetValue(functionName, out var executor))
        {
            return FunctionResult.FailureResult($"Function not found: {functionName}");
        }

        try
        {
            var result = await executor(argumentsJson);
            _logger.Information("Function {FunctionName} execution status: {Success}", functionName, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Exception in function {FunctionName}", functionName);
            return FunctionResult.FailureResult($"Execution exception: {ex.Message}");
        }
    }

    public int GetToolDeclarationTokenCount()
    {
        if (_cachedToolDeclarationTokens.HasValue)
        {
            return _cachedToolDeclarationTokens.Value;
        }

        int totalTokens = 0;
        foreach (var tool in _tools)
        {
            // Simple estimation: serialize the tool definition and estimate based on length.
            // A more precise calculation would involve tokenizing the JSON representation.
            string serializedTool = JsonSerializer.Serialize(tool, new JsonSerializerOptions { WriteIndented = false });
            totalTokens += Models.ConversationContext.EstimateTokens(serializedTool);
        }

        _cachedToolDeclarationTokens = totalTokens;
        _logger.Debug("Calculated total tool declaration tokens: {Tokens}", totalTokens);
        return totalTokens;
    }
}
