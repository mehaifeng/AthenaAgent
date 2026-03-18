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
        FileSystemFunctions fileSystemFunctions,
        CliFunctions cliFunctions,
        WebSearchFunctions webSearchFunctions,
        IConfigService? configService,
        ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<FunctionRegistry>();

        // --- CLI Control ---
        RegisterFunction("execute_terminal_command", cliFunctions.ExecuteTerminalCommandAsync,
            $"Executes a shell command on the current OS ({(OperatingSystem.IsWindows() ? "Windows — use PowerShell/cmd syntax" : OperatingSystem.IsMacOS() ? "macOS — use zsh/POSIX syntax" : "Linux — use bash/POSIX syntax")}). " +
            "By default, waits for the process to exit and captures output. For GUI applications or long-running background tasks, set 'waitForExit' to false. " +
            "DO NOT use this for file system tasks like 'ls', 'mkdir', or 'rm'—use the dedicated file system tools instead.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "The command to execute (e.g., 'git', 'dotnet', 'npm', 'regedit')." },
                    arguments = new { type = "array", items = new { type = "string" }, description = "List of command-line arguments." },
                    workingDirectory = new { type = "string", description = "The directory where the command should be executed." },
                    waitForExit = new { type = "boolean", description = "If true (default), waits for the process to complete and captures output. If false, launches the process in the background and returns immediately.", @default = true }
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

        // --- File System Control ---
        RegisterFunction("get_file_info", fileSystemFunctions.GetFileInfoAsync,
            "Gets metadata of a file without reading its content. Always call this first before reading any file larger than a few KB to understand its size, line count, and type before deciding how to read it.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Absolute or relative path to the file." } },
                required = new[] { "path" }
            });

        RegisterFunction("search_in_file", fileSystemFunctions.SearchInFileAsync,
            "Searches for a keyword or pattern in a file and returns matching positions with surrounding context. For code files, use this to locate class names, method names, or symbols before reading. For unstructured text, use this to find topic-relevant fragments before reading chunks.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    pattern = new { type = "string", description = "Keyword or regex pattern to search for." },
                    contextLines = new { type = "integer", description = "Lines of context to include around each match.", @default = 3 },
                    maxMatches = new { type = "integer", description = "Maximum number of matches to return.", @default = 10 }
                },
                required = new[] { "path", "pattern" }
            });

        RegisterFunction("get_document_outline", fileSystemFunctions.GetDocumentOutlineAsync,
            "Returns the structural outline of a document without full content. For Markdown returns headings and line numbers. For JSON/YAML returns top-level keys. For code files returns class and method signatures. For unstructured text returns the first sentence of each detected paragraph block.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Path to the file." } },
                required = new[] { "path" }
            });

        RegisterFunction("read_system_file", fileSystemFunctions.ReadSystemFileAsync,
            "Reads file content. Behavior adapts to file size automatically: files under 50KB are returned in full; larger files are split into chunks and require chunkIndex. For code files, use startLine/endLine after locating the target with search_in_file. For structured documents, use sectionTitle. For unstructured text, use chunkIndex to paginate.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    startLine = new { type = "integer", description = "First line to read (1-based). For code files only." },
                    endLine = new { type = "integer", description = "Last line to read (inclusive). For code files only." },
                    sectionTitle = new { type = "string", description = "Heading or section name to jump to. For structured documents (Markdown, etc.)." },
                    chunkIndex = new { type = "integer", description = "0-based chunk index for large or unstructured files. Get total chunk count from get_file_info first." }
                },
                required = new[] { "path" }
            });

        RegisterFunction("write_system_file", fileSystemFunctions.WriteSystemFileAsync,
            "Creates a new file or fully overwrites an existing one. Only use this for new files or when replacing the entire content. For partial edits to existing files, use modify_system_file instead.",
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
            "Modifies a specific fragment of an existing file using SEARCH/REPLACE format. Use this for all partial edits to avoid overwriting unrelated content. The SEARCH block must uniquely identify the target location.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    diffContent = new { type = "string", description = "Modification in SEARCH/REPLACE format:\n<<<<<<< SEARCH\nOld content\n=======\nNew content\n>>>>>>> REPLACE" },
                    fuzzyMatch = new { type = "boolean", description = "Whether to tolerate minor whitespace differences in the SEARCH block. Defaults to true.", @default = true }
                },
                required = new[] { "path", "diffContent" }
            });

        RegisterFunction("delete_system_file", fileSystemFunctions.DeleteSystemFileAsync,
            "Permanently deletes a file. This action is irreversible. Always confirm with the user before calling this tool.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Path to the file." } },
                required = new[] { "path" }
            });

        RegisterFunction("list_system_directory", fileSystemFunctions.ListSystemDirectoryAsync,
            "Lists files and subdirectories at a given path. Use this to explore unfamiliar directory structures before deciding which files to read.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the directory." },
                    recursive = new { type = "boolean", description = "Whether to list subdirectories recursively.", @default = false },
                    filter = new { type = "string", description = "Optional glob pattern to filter results, e.g. '*.cs' or '*.md'." }
                },
                required = new[] { "path" }
            });

        RegisterFunction("create_directory", fileSystemFunctions.CreateDirectoryAsync,
            "Creates a new directory at the specified path. If the parent directories do not exist, they will be created as well.",
            new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Path to the directory to create." } },
                required = new[] { "path" }
            });

        RegisterFunction("move_system_file", fileSystemFunctions.MoveSystemFileAsync,
            "Moves or renames a file or directory. Source and destination must be valid paths.",
            new
            {
                type = "object",
                properties = new
                {
                    sourcePath = new { type = "string", description = "Current path of the file or directory." },
                    destinationPath = new { type = "string", description = "Target path for the file or directory." }
                },
                required = new[] { "sourcePath", "destinationPath" }
            });

        RegisterFunction("copy_system_file", fileSystemFunctions.CopySystemFileAsync,
            "Copies a file from the source path to the destination path. Source must be an existing file.",
            new
            {
                type = "object",
                properties = new
                {
                    sourcePath = new { type = "string", description = "Path of the source file." },
                    destinationPath = new { type = "string", description = "Target path for the copy." }
                },
                required = new[] { "sourcePath", "destinationPath" }
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
