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

    public bool HasFunctions => _tools.Count > 0;

    public FunctionRegistry(
        ProactiveMessagingFunctions proactiveFunctions,
        KnowledgeBaseFunctions knowledgeFunctions,
        ConfigurationFunctions configFunctions,
        ILogger logger)
    {
        _logger = logger.ForContext<FunctionRegistry>();

        // --- 任务与提醒能力 ---
        RegisterFunction("create_task", proactiveFunctions.ScheduleProactiveMessage,
            "为用户创建定时或循环的提醒任务。当我需要根据用户要求在特定时间（如'明天下午3点'或'每周五'）进行提醒时使用此能力。",
            new
            {
                type = "object",
                properties = new
                {
                    scheduledTime = new { type = "string", description = "任务触发的自然语言时间描述，例如 '2024-08-15 09:00', 'in 2 hours', 'every friday 3pm'。" },
                    intent = new { type = "string", description = "任务的核心内容，即需要提醒用户做什么事。例如 '提交周报'。" },
                    recurrence = new { type = "string", description = "任务的循环周期。'none' (默认), 'daily', 'weekly', 'every N days'。", @default = "none" }
                },
                required = new[] { "scheduledTime", "intent" }
            });

        RegisterFunction("cancel_task", proactiveFunctions.CancelScheduledMessage,
            "取消一个之前设定好的提醒任务。需要提供任务的唯一ID。",
            new
            {
                type = "object",
                properties = new { taskId = new { type = "string", description = "要取消的任务ID。可以从任务列表中获取。" } },
                required = new[] { "taskId" }
            });

        RegisterFunction("list_tasks", proactiveFunctions.ListScheduledMessages,
            "列出所有当前已设定的、未完成的提醒任务。当我需要回顾或管理已有任务时使用。",
            new { type = "object", properties = new { } });

        // --- 长期记忆能力 ---
        RegisterFunction("save_to_memory", knowledgeFunctions.CreateKnowledgeFile,
            "将一小段信息保存到我的长期记忆中。当用户告诉我需要记住的个人偏好、事实、或上下文时（例如'我的项目代号是猎户座'），我应该使用此能力创建一个新的记忆片段。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "记忆片段的标题或分类路径，应根据内容生成一个有意义的名称，例如 'user_preferences/project_code_name.md' 或 'facts/pet_name.md'。" },
                    content = new { type = "string", description = "需要长期记住的具体信息内容。" },
                    tags = new { type = "array", items = new { type = "string" }, description = "用于对信息分类的关键词标签（可选）。" }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("update_memory_fragment", knowledgeFunctions.UpdateKnowledgeFileDiff,
            "修改我长期记忆中的某个片段。这是更新已有信息（如修改一个记住的偏好）的首选方式。我需要提供记忆片段的标题和具体修改内容。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "要修改的记忆片段的标题或分类路径。" },
                    diffContent = new { type = "string", description = "描述如何修改的文本，格式为：\n<<<<<<< SEARCH\n要被替换的旧内容\n=======\n用来替换的新内容\n>>>>>>> REPLACE" },
                    fuzzyMatch = new { type = "boolean", description = "是否忽略空格和换行符的差异进行匹配，默认为 true。", @default = true }
                },
                required = new[] { "filePath", "diffContent" }
            });

        RegisterFunction("recall_from_memory", knowledgeFunctions.SearchKnowledgeBase,
            "在我的长期记忆中搜索信息。当我需要回答一个可能基于我之前被告知的信息的问题时，我应该使用此能力来回忆相关内容。",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "我需要回忆的主题或关键词。" },
                    maxResults = new { type = "integer", description = "最多返回几条相关的记忆片段。", @default = 3 }
                },
                required = new[] { "query" }
            });

        RegisterFunction("review_memory_fragment", knowledgeFunctions.ReadKnowledgeFile,
            "完整地阅读我长期记忆中的某一个记忆片段。当回忆出的信息不完整，需要查看全部细节时使用。",
            new
            {
                type = "object",
                properties = new { filePath = new { type = "string", description = "我要阅读的记忆片段的标题或分类路径。" } },
                required = new[] { "filePath" }
            });

        RegisterFunction("forget_memory_fragment", knowledgeFunctions.DeleteKnowledgeFile,
            "从我的长期记忆中永久删除一个记忆片段。当用户明确要求我忘记某件事时使用。这是一个不可逆的操作。",
            new
            {
                type = "object",
                properties = new { filePath = new { type = "string", description = "我要删除的记忆片段的标题或分类路径。" } },
                required = new[] { "filePath" }
            });

        RegisterFunction("list_all_memories", knowledgeFunctions.ListKnowledgeFiles,
            "列出我长期记忆中所有记忆片段的标题。当我需要对我的记忆进行整体回顾或管理时使用。",
            new { type = "object", properties = new { } });

        // --- 自我配置能力 ---
        RegisterFunction("modify_self_configuration", configFunctions.ModifyAppConfig,
            "修改我自己的运行配置参数。当我接收到调整自身行为的指令时使用，例如调整我的回应风格或语言。",
            new
            {
                type = "object",
                properties = new
                {
                    key = new { type = "string", description = "可以修改的配置项名称。例如: 'Temperature', 'Language', 'Theme'。" },
                    value = new { type = "string", description = "要设置的新值。" }
                },
                required = new[] { "key", "value" }
            });

        RegisterFunction("view_self_configuration", configFunctions.GetAppConfig,
            "查看我当前的运行配置。当被问及我当前的设置或状态时使用。",
            new
            {
                type = "object",
                properties = new { section = new { type = "string", description = "要查看的配置类别：'AI' (关于我的思考方式), 'Appearance' (关于我的外观), 'Memory' (关于我的记忆机制), 或 'All' (全部)。", @default = "All" } }
            });

        // 以下两个函数较为底层，建议在更自然的 update_memory_fragment 不可用时作为备选
        RegisterFunction("update_memory_fragment_by_replacement", knowledgeFunctions.UpdateKnowledgeFile,
            "通过替换或追加的方式更新记忆片段。这是更新已有信息的备用方式。",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "记忆片段的标题或分类路径。" },
                    content = new { type = "string", description = "新的内容。" },
                    mode = new { type = "string", description = "更新模式：'append' (在末尾追加) 或 'replace' (完全替换原有内容)。", @default = "append" }
                },
                required = new[] { "filePath", "content" }
            });

        _logger.Information("FunctionRegistry 初始化完成，注册了 {Count} 个函数", _tools.Count);
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

    public IEnumerable<object> GetToolDefinitions() => _tools;

    public IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames)
    {
        var nameSet = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        return _tools.Where(t => t is ChatTool chatTool && nameSet.Contains(chatTool.FunctionName));
    }

    public async Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson)
    {
        _logger.Information("执行函数: {FunctionName}", functionName);

        if (!_executors.TryGetValue(functionName, out var executor))
        {
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
