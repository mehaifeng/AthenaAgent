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
    private int? _cachedToolDeclarationTokens = null;

    public bool HasFunctions => _tools.Count > 0;

    public FunctionRegistry(
        ProactiveMessagingFunctions proactiveFunctions,
        KnowledgeBaseFunctions knowledgeFunctions,
        ConfigurationFunctions configFunctions,
        FileSystemFunctions fileSystemFunctions,
        ILogger logger)
    {
        _logger = logger.ForContext<FunctionRegistry>();

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
            "Creates a new memory record in the knowledge base. Only use this for information that should be persisted for long-term recall.",
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
            "Searches across all memory domains using semantic vector search. Use this to find relevant context from the knowledge base.",
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

        // --- File System Control ---
        RegisterFunction("read_system_file", fileSystemFunctions.ReadSystemFileAsync,
            "Reads the content of a local system file.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Absolute or relative path to the file." } },
                required = new[] { "path" }
            });

        RegisterFunction("write_system_file", fileSystemFunctions.WriteSystemFileAsync,
            "Writes content to a local system file. If the file exists, it will be completely overwritten. If not, it will be created.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    content = new { type = "string", description = "The full content to write." }
                },
                required = new[] { "path", "content" }
            });

        RegisterFunction("modify_system_file", fileSystemFunctions.ModifySystemFileAsync,
            "Modifies a specific fragment of an existing local system file.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    diffContent = new { type = "string", description = "The modification in SEARCH/REPLACE format:\n<<<<<<< SEARCH\nOld content\n=======\nNew content\n>>>>>>> REPLACE" },
                    fuzzyMatch = new { type = "boolean", description = "Whether to ignore whitespace differences. Defaults to true.", @default = true }
                },
                required = new[] { "path", "diffContent" }
            });

        RegisterFunction("delete_system_file", fileSystemFunctions.DeleteSystemFileAsync,
            "Deletes a local system file. This is irreversible.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Path to the file." } },
                required = new[] { "path" }
            });

        RegisterFunction("list_system_directory", fileSystemFunctions.ListSystemDirectoryAsync,
            "Lists all files and subdirectories in a given path. Use this to explore the local file system.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the directory." },
                    recursive = new { type = "boolean", description = "Whether to list subdirectories recursively.", @default = false }
                },
                required = new[] { "path" }
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
