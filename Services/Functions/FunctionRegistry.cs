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

    public bool HasFunctions => _tools.Count > 0;

    public FunctionRegistry(
        ProactiveMessagingFunctions proactiveFunctions,
        KnowledgeBaseFunctions knowledgeFunctions,
        ConfigurationFunctions configFunctions,
        FileSystemFunctions fileSystemFunctions,
        CliFunctions cliFunctions,
        WebSearchFunctions webSearchFunctions,
        ImageGenerationFunctions imageGenerationFunctions,
        BrowserTaskFunctions browserTaskFunctions,
        SubAgentFunctions subAgentFunctions,
        DocumentParserFunctions documentParserFunctions,
        IConfigService? configService,
        ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<FunctionRegistry>();

        // --- CLI Control ---
        RegisterFunction("execute_terminal_command", cliFunctions.ExecuteTerminalCommandAsync,
            $"Executes a single executable or shell on the current OS ({(OperatingSystem.IsWindows() ? "Windows — prefer 'powershell' for shell built-ins" : "POSIX — use zsh/bash")}). " +
            "CRITICAL: The 'command' field MUST be a single executable name (e.g., 'powershell', 'git', 'npm'). " +
            "ALL parameters, flags, and URLs MUST be passed as separate strings in the 'arguments' array. " +
            "For example, to open a URL on Windows: command='powershell', arguments=['start', 'https://example.com']. " +
            "DO NOT use this for file system tasks like 'ls' or 'mkdir'—use dedicated file tools instead.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "The executable name ONLY (e.g., 'powershell', 'dotnet'). NO spaces or arguments allowed here." },
                    arguments = new { type = "array", items = new { type = "string" }, description = "List of arguments. Each flag or parameter should be a separate string (e.g., ['-c', 'dir'] or ['start', 'url'])." },
                    workingDirectory = new { type = "string", description = "The directory where the command should be executed." },
                    waitForExit = new { type = "boolean", description = "If true (default), waits for completion. Set to false for GUI apps or background tasks.", @default = true }
                },
                required = new[] { "command" }
            });

        // --- Tasks & Reminders ---
        RegisterFunction("create_task", proactiveFunctions.ScheduleProactiveMessage,
            "Schedules a proactive conversation task. Use this to set reminders or follow-ups based on explicit or implicit time mentions. Supported recurrence modes are: none, interval, and weekly_days. Do not invent free-text recurrence strings.",
            new
            {
                type = "object",
                properties = new
                {
                    scheduledTime = new { type = "string", description = "Natural language time description for the schedule boundary (e.g., '2024-08-15 09:00', 'in 2 hours', 'tomorrow morning')." },
                    intent = new { type = "string", description = "The topic or message for the proactive session." },
                    recurrence = new
                    {
                        type = "object",
                        description = "Optional structured recurrence rule. Supported forms: {mode:'none'}; {mode:'interval', interval:N, unit:'minute|hour|day|week'}; {mode:'weekly_days', interval:N, daysOfWeek:['Monday','Friday']}.",
                        properties = new
                        {
                            mode = new { type = "string", @enum = new[] { "none", "interval", "weekly_days" } },
                            interval = new { type = "integer", description = "Positive integer. Required for interval and weekly_days." },
                            unit = new { type = "string", @enum = new[] { "minute", "hour", "day", "week" }, description = "Required only when mode='interval'." },
                            daysOfWeek = new { type = "array", items = new { type = "string", @enum = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" } }, description = "Required only when mode='weekly_days'." }
                        }
                    }
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
            "Creates a NEW memory file in the knowledge base. Use this ONLY for a genuinely new topic that no existing memory covers. " +
            "Keep one topic in one file: if recall_from_memory returns any file on the same subject, do NOT create a parallel file — add the fact to that file with modify_system_file instead (the relative path returned by recall works directly). " +
            "This tool runs a semantic duplicate check and will REJECT the creation if a highly similar memory already exists, pointing you to the file to modify. " +
            "Only when you are certain the information is a distinct new topic should you retry with allowDuplicate=true.",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "Relative path for the memory file (e.g., 'user_preferences/coding_style.md'). Use a stable, descriptive path so the same topic always lands in the same file." },
                    content = new { type = "string", description = "The detailed information to be remembered." },
                    allowDuplicate = new { type = "boolean", description = "Set true ONLY to override the semantic duplicate guard after you have confirmed this is a distinct new topic, not a variant of an existing memory.", @default = false }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("recall_from_memory", knowledgeFunctions.SearchKnowledgeBase,
            "Searches across all memory domains using hybrid semantic + keyword vector search. Results are aggregated per file: each hit is a distinct memory file with its heading path and matchCount (how many sections of that file matched — a high count means the topic already lives there). " +
            "MUST be called before create_new_memory — this is mandatory, no exceptions. Before writing, check whether any returned file already owns this topic; if so, modify that file instead of creating a new one. " +
            "Also call this whenever the user asks something that may rely on past context.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query or keywords." },
                    maxResults = new { type = "integer", description = "Maximum number of memory files to return.", @default = 5 }
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

        RegisterFunction("generate_image", imageGenerationFunctions.GenerateImageAsync,
            "Generates a single image and attaches it to the current assistant reply. Use this only when the user explicitly asks for an image or the best answer is an image instead of plain text. continuityMode='new_root' starts a brand-new visual lineage. continuityMode='continue_last' continues only the latest generated image in the current conversation. continuityMode='continue_match' first resolves an earlier image lineage in the current conversation by matching referenceQuery against prior image prompts, then continues that lineage from its latest available image. For continue_match, referenceQuery must be a distinctive content description, not vague phrases like 'that one' or 'the earlier image'. If the backend cannot do reference-image generation, the tool will fail instead of silently creating an unrelated new image. If the user's intent is ambiguous, ask before calling this tool.",
            new
            {
                type = "object",
                properties = new
                {
                    prompt = new { type = "string", description = "The final image prompt to render." },
                    continuityMode = new { type = "string", @enum = new[] { "new_root", "continue_last", "continue_match" }, description = "Use new_root to start a fresh lineage, continue_last to continue only the latest image in the current conversation, or continue_match to semantically resolve and continue an earlier lineage from the current conversation.", @default = "new_root" },
                    referenceQuery = new { type = "string", description = "Required when continuityMode is continue_match. Provide a distinctive content-based description of the earlier image lineage you want to continue." }
                },
                required = new[] { "prompt" }
            });

        // --- Headless Browser (high-level entry only) ---
        RegisterFunction("run_browser_task", browserTaskFunctions.RunBrowserTaskAsync,
            "Runs an isolated visual browser task using a headless Chromium session. Use this when the user needs interactive webpage inspection or operation, such as opening a page, reading visible content, clicking a visible control, filling a simple form, uploading a local file to a file input, or navigating a site. The browser task keeps screenshots and low-level browser actions out of the main chat context. NOTE: Browser and Browser Vision must be configured in settings.",
            new
            {
                type = "object",
                properties = new
                {
                    instruction = new { type = "string", description = "The browser task to perform, including the user's goal and any constraints." },
                    startUrl = new { type = "string", description = "Optional URL to open first. Provide this when the task is tied to a specific page." },
                    maxSteps = new { type = "integer", description = "Optional maximum number of internal browser steps. Defaults to the browser setting; complex multi-control tasks may automatically use a larger internal limit.", @default = 40 }
                },
                required = new[] { "instruction" }
            });

        // --- Sub-Agents (parallel delegation) ---
        RegisterFunction("dispatch_subagents", subAgentFunctions.DispatchSubagentsAsync,
            "Delegates one or more INDEPENDENT subtasks to isolated sub-agents that run IN PARALLEL, each with its own private context and tools. " +
            "Use this when the user's request decomposes into several self-contained pieces that can progress at the same time (e.g., research multiple topics, process multiple files, gather several data points). " +
            "Each sub-agent only returns a concise final summary to you — its intermediate tool calls stay out of this conversation, which keeps your context clean. " +
            "Do NOT use this for a single small step you can do yourself, for strictly sequential work where one step depends on the previous, or when you need to keep tight control. " +
            "Each task's 'instruction' MUST be complete and self-contained, because the sub-agent cannot see this conversation. Sub-agents cannot themselves dispatch further sub-agents.",
            new
            {
                type = "object",
                properties = new
                {
                    tasks = new
                    {
                        type = "array",
                        description = "The batch of independent subtasks to run in parallel. Prefer 2–5 focused tasks.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string", description = "Short label shown in the UI (e.g., '竞品价格调研')." },
                                agent_type = new { type = "string", @enum = new[] { "general", "researcher", "file_worker" }, description = "researcher: web/browser/memory reading. file_worker: local file operations. general: broad toolset.", @default = "general" },
                                instruction = new { type = "string", description = "Complete, self-contained instruction. Include all needed context, constraints, and the expected deliverable — the sub-agent cannot see this conversation." }
                            },
                            required = new[] { "title", "instruction" }
                        }
                    }
                },
                required = new[] { "tasks" }
            });

        // --- Document Parsing (MinerU) ---
        RegisterFunction("parse_office_document", documentParserFunctions.ParseOfficeDocumentAsync,
            "Parses a local Office or PDF document (.doc, .docx, .ppt, .pptx, .xls, .xlsx, .pdf) into AI-readable Markdown text via the MinerU service, and returns the extracted content. " +
            "Use this when the user references a local Office/PDF file whose contents you need to read or summarize — plain-text and code files should be read with read_system_file instead, not this tool. " +
            "The parsing runs remotely (upload -> poll -> download) and may take a while for large files. NOTE: This tool requires Document Parsing to be enabled in settings.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Absolute or relative path to the local Office/PDF document to parse." }
                },
                required = new[] { "path" }
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
            "Modifies an existing file using one or more SEARCH/REPLACE blocks. Use this for all partial edits to avoid overwriting unrelated content. The SEARCH block must uniquely identify the target. Whitespace, indentation and line-ending (LF/CRLF) differences are tolerated by default. To delete code, leave the REPLACE side empty. If a SEARCH block matches multiple locations the edit fails and reports each location—add surrounding context to disambiguate, or set replaceAll=true to change every occurrence. For creating a new file or replacing entire content, use write_system_file instead.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file." },
                    diffContent = new { type = "string", description = "One or more blocks in the format:\n<<<<<<< SEARCH\nExisting lines\n=======\nNew lines\n>>>>>>> REPLACE" },
                    fuzzyMatch = new { type = "boolean", description = "Tolerate whitespace/indentation/line-ending differences in the SEARCH block. Defaults to true. Set false to require a byte-exact match.", @default = true },
                    replaceAll = new { type = "boolean", description = "Replace every occurrence of the SEARCH block instead of requiring a unique match. Defaults to false.", @default = false }
                },
                required = new[] { "path", "diffContent" }
            });

        RegisterFunction("delete_system_file", fileSystemFunctions.DeleteSystemFileAsync,
            "Deletes a file. If the path points to a directory, deletion is REFUSED unless recursive=true is set, in which case the ENTIRE directory tree is permanently removed. This action is irreversible. Always confirm with the user before calling this tool.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file or directory." },
                    recursive = new { type = "boolean", description = "Required to be true to delete a directory and all its contents recursively. Has no effect on single files. Defaults to false.", @default = false }
                },
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

    // 将参数名归一化为不含下划线的小写形式，用于跨命名风格（snake_case / camelCase）匹配。
    private static string Canonicalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

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

                // 归一化参数名（去下划线 + 小写），容忍模型输出 snake_case / 大小写差异，
                // 避免与 C# camelCase 形参对不上时静默丢参回落默认值。
                var propMap = new Dictionary<string, JsonElement>();
                if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in args.EnumerateObject())
                        propMap[Canonicalize(p.Name)] = p.Value;
                }

                var method = function.Method;
                var parameters = method.GetParameters();
                var argsArray = new object?[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    if (propMap.TryGetValue(Canonicalize(param.Name!), out var prop)
                        && prop.ValueKind != JsonValueKind.Null)
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

    public IEnumerable<object> GetToolDefinitions() => FilterTools(_tools);

    public IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames)
    {
        var nameSet = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        return FilterTools(_tools.Where(t => t is ChatTool chatTool && nameSet.Contains(chatTool.FunctionName)));
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
        int totalTokens = 0;
        foreach (var tool in FilterTools(_tools).OfType<ChatTool>())
        {
            // Simple estimation: serialize the tool definition and estimate based on length.
            // A more precise calculation would involve tokenizing the JSON representation.
            string serializedTool = JsonSerializer.Serialize(tool, new JsonSerializerOptions { WriteIndented = false });
            totalTokens += Models.ConversationContext.EstimateTokens(serializedTool);
        }

        _logger.Debug("Calculated total tool declaration tokens: {Tokens}", totalTokens);
        return totalTokens;
    }

    private IEnumerable<object> FilterTools(IEnumerable<object> tools)
    {
        var config = _configService?.Load();
        foreach (var tool in tools)
        {
            if (tool is not ChatTool chatTool)
            {
                yield return tool;
                continue;
            }

            if (string.Equals(chatTool.FunctionName, "generate_image", StringComparison.OrdinalIgnoreCase)
                && config?.ImageGenerationEnabled != true)
            {
                continue;
            }

            // 子代理派发仅在开启时对模型可见，避免默认状态下污染工具列表。
            if (string.Equals(chatTool.FunctionName, "dispatch_subagents", StringComparison.OrdinalIgnoreCase)
                && config?.EnableSubAgents != true)
            {
                continue;
            }

            // Office/PDF 文档解析仅在开启文档解析时对模型可见。
            if (string.Equals(chatTool.FunctionName, "parse_office_document", StringComparison.OrdinalIgnoreCase)
                && config?.DocumentParserEnabled != true)
            {
                continue;
            }

            yield return tool;
        }
    }
}
