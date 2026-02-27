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
/// Function 注册表实现
/// 管理所有 Function Calling 工具的定义和执行
/// </summary>
public class FunctionRegistry : IFunctionRegistry
{
    private readonly Dictionary<string, Func<string, Task<FunctionResult>>> _executors = new();
    private readonly List<ChatTool> _tools = new();
    private readonly ILogger _logger;

    public bool HasFunctions => _tools.Count > 0;

    public FunctionRegistry(
        ProactiveMessagingFunctions proactiveFunctions,
        KnowledgeBaseFunctions knowledgeFunctions,
        ConfigurationFunctions configFunctions,
        ILogger logger)
    {
        _logger = logger.ForContext<FunctionRegistry>();

        // 注册主动消息相关函数
        RegisterFunction("schedule_proactive_message", proactiveFunctions.ScheduleProactiveMessage,
            "安排一个主动消息在指定的未来时间发送给用户。当用户要求提醒，或者你想要跟进重要话题时使用此功能。",
            new
            {
                type = "object",
                properties = new
                {
                    scheduledTime = new
                    {
                        type = "string",
                        description = "触发时间，支持多种格式：'2024-02-10 08:00', 'in 2 hours', 'tomorrow morning', 'tomorrow 14:00'"
                    },
                    intent = new
                    {
                        type = "string",
                        description = "对自己的提醒，描述触发时应该说什么或做什么（用户不会看到此内容）"
                    },
                    recurrence = new
                    {
                        type = "string",
                        description = "循环模式：'none'（一次性）、'daily'（每天）、'weekly'（每周）、'every N days'（每N天）",
                        @default = "none"
                    }
                },
                required = new[] { "scheduledTime", "intent" }
            });

        RegisterFunction("cancel_scheduled_message", proactiveFunctions.CancelScheduledMessage,
            "取消一个已安排的主动消息。",
            new
            {
                type = "object",
                properties = new
                {
                    taskId = new
                    {
                        type = "string",
                        description = "要取消的任务ID"
                    }
                },
                required = new[] { "taskId" }
            });

        RegisterFunction("list_scheduled_messages", proactiveFunctions.ListScheduledMessages,
            "列出所有已安排的主动消息。当用户发出'有什么提醒'类似提问时使用。",
            new
            {
                type = "object",
                properties = new { }
            });

        // 注册知识库相关函数
        RegisterFunction("create_knowledge_file", knowledgeFunctions.CreateKnowledgeFile,
            "在知识库中创建一个新的 Markdown 文件。仅用于创建新文件，如需修改现有文件请使用 update_knowledge_file_diff。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "相对路径，如 'Characters/user_profile.md', 'Memories/vacation_2024.md'"
                    },
                    content = new
                    {
                        type = "string",
                        description = "Markdown 格式的内容"
                    },
                    tags = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "可选的标签，用于分类"
                    }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("update_knowledge_file_diff", knowledgeFunctions.UpdateKnowledgeFileDiff,
            "使用 SEARCH/REPLACE 模式精确更新知识库文件。这是推荐的文件修改方式。格式示例：\n<<<<<<< SEARCH\n要查找的原始内容\n=======\n替换后的新内容\n>>>>>>> REPLACE\n可以包含多个 SEARCH/REPLACE 块来修改多处内容。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "文件相对路径，如 'Characters/user_profile.md'"
                    },
                    diffContent = new
                    {
                        type = "string",
                        description = @"SEARCH/REPLACE 格式的修改内容，格式如下：
<<<<<<< SEARCH
原始内容（必须与文件中的内容匹配）
=======
新内容
>>>>>>> REPLACE"
                    },
                    fuzzyMatch = new
                    {
                        type = "boolean",
                        description = "是否启用模糊匹配（忽略空格差异），默认 true",
                        @default = true
                    }
                },
                required = new[] { "filePath", "diffContent" }
            });

        RegisterFunction("update_knowledge_file", knowledgeFunctions.UpdateKnowledgeFile,
            "更新知识库文件。支持 append（追加内容）或 replace（替换整体内容）模式。对于精确修改文件的特定部分，建议使用 update_knowledge_file_diff。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "要更新的文件相对路径"
                    },
                    content = new
                    {
                        type = "string",
                        description = "要添加或替换的内容"
                    },
                    mode = new
                    {
                        type = "string",
                        description = "'append' 追加内容，'replace' 替换整个文件（谨慎使用）",
                        @enum = new[] { "append", "replace" },
                        @default = "append"
                    }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("search_knowledge_base", knowledgeFunctions.SearchKnowledgeBase,
            "搜索知识库，使用向量语义检索查找相关内容。这是获取相关上下文的首选方法，适合查找与某个主题相关的内容或回忆之前记录的信息。",
            new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        description = "搜索关键词或自然语言问题"
                    },
                    maxResults = new
                    {
                        type = "integer",
                        description = "最大返回结果数",
                        @default = 5
                    }
                },
                required = new[] { "query" }
            });

        RegisterFunction("read_knowledge_file", knowledgeFunctions.ReadKnowledgeFile,
            "读取知识库文件的完整内容。用于在使用 update_knowledge_file_diff 之前查看当前内容，或获取完整文件内容进行精确编辑。对于搜索相关内容，建议使用 search_knowledge_base。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "要读取的文件相对路径，如 'Characters/user_profile.md'"
                    }
                },
                required = new[] { "filePath" }
            });

        RegisterFunction("delete_knowledge_file", knowledgeFunctions.DeleteKnowledgeFile,
            "删除知识库中的文件。请谨慎使用，删除后无法恢复。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new
                    {
                        type = "string",
                        description = "要删除的文件相对路径"
                    }
                },
                required = new[] { "filePath" }
            });

        RegisterFunction("list_knowledge_files", knowledgeFunctions.ListKnowledgeFiles,
            "列出知识库中的所有文件。",
            new
            {
                type = "object",
                properties = new { }
            });

        // 注册配置相关函数
        RegisterFunction("modify_app_config", configFunctions.ModifyAppConfig,
            "修改应用配置参数，如 Temperature、MaxTokens 等。",
            new
            {
                type = "object",
                properties = new
                {
                    key = new
                    {
                        type = "string",
                        description = "配置项名称：Temperature（创造性）、MaxTokens（响应长度）、FontSize（字体大小）等"
                    },
                    value = new
                    {
                        type = "string",
                        description = "新值"
                    }
                },
                required = new[] { "key", "value" }
            });

        RegisterFunction("get_app_config", configFunctions.GetAppConfig,
            "获取当前应用配置。",
            new
            {
                type = "object",
                properties = new
                {
                    section = new
                    {
                        type = "string",
                        description = "配置部分：'AI'、'Appearance'、'Memory' 或 'All'",
                        @default = "All"
                    }
                }
            });

        _logger.Information("FunctionRegistry 初始化完成，注册了 {Count} 个函数", _tools.Count);
    }

    /// <summary>
    /// 注册一个函数
    /// </summary>
    private void RegisterFunction(string name, Delegate function, string description, object parameters)
    {
        // 创建 OpenAI ChatTool
        var tool = ChatTool.CreateFunctionTool(
            name,
            description,
            BinaryData.FromString(JsonSerializer.Serialize(parameters))
        );

        _tools.Add(tool);

        // 创建执行器
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

                return FunctionResult.FailureResult("函数返回类型错误");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "执行函数 {FunctionName} 失败", name);
                return FunctionResult.FailureResult($"执行失败: {ex.Message}");
            }
        };

        _logger.Debug("注册函数: {Name}", name);
    }

    /// <summary>
    /// 获取所有工具定义
    /// </summary>
    public IEnumerable<object> GetToolDefinitions() => _tools;

    /// <summary>
    /// 根据名称列表获取工具定义
    /// </summary>
    public IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames)
    {
        var nameSet = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        return _tools.Where(t =>
        {
            // ChatTool 的 FunctionName 属性
            if (t is OpenAI.Chat.ChatTool chatTool)
            {
                return nameSet.Contains(chatTool.FunctionName);
            }
            return false;
        });
    }

    /// <summary>
    /// 执行指定的函数
    /// </summary>
    public async Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson)
    {
        _logger.Information("执行函数: {FunctionName}, 参数: {Args}", functionName, argumentsJson);

        if (!_executors.TryGetValue(functionName, out var executor))
        {
            _logger.Warning("未找到函数: {FunctionName}", functionName);
            return FunctionResult.FailureResult($"未找到函数: {functionName}");
        }

        try
        {
            var result = await executor(argumentsJson);
            _logger.Information("函数 {FunctionName} 执行结果: {Success}", functionName, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "函数 {FunctionName} 执行异常", functionName);
            return FunctionResult.FailureResult($"执行异常: {ex.Message}");
        }
    }
}
