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

        // 主动消息
        RegisterFunction("schedule_message", proactiveFunctions.ScheduleProactiveMessage,
            "安排定时提醒或跟进消息",
            new
            {
                type = "object",
                properties = new
                {
                    scheduledTime = new { type = "string", description = "触发时间: '2024-02-10 08:00' / 'in 2 hours' / 'tomorrow 14:00'" },
                    intent = new { type = "string", description = "提醒内容（用户不可见）" },
                    recurrence = new { type = "string", description = "循环: none/daily/weekly/'every N days'", @default = "none" }
                },
                required = new[] { "scheduledTime", "intent" }
            });

        RegisterFunction("cancel_message", proactiveFunctions.CancelScheduledMessage,
            "取消定时消息",
            new
            {
                type = "object",
                properties = new { taskId = new { type = "string", description = "任务ID" } },
                required = new[] { "taskId" }
            });

        RegisterFunction("list_messages", proactiveFunctions.ListScheduledMessages,
            "列出所有定时消息",
            new { type = "object", properties = new { } });

        // 知识库
        RegisterFunction("create_file", knowledgeFunctions.CreateKnowledgeFile,
            "创建知识库文件",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "相对路径: 'Characters/user.md'" },
                    content = new { type = "string", description = "Markdown 内容" },
                    tags = new { type = "array", items = new { type = "string" }, description = "标签（可选）" }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("update_file_diff", knowledgeFunctions.UpdateKnowledgeFileDiff,
            "精确修改文件部分内容（推荐）",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "文件路径" },
                    diffContent = new { type = "string", description = "SEARCH/REPLACE 格式:\n<<<<<<< SEARCH\n原内容\n=======\n新内容\n>>>>>>> REPLACE" },
                    fuzzyMatch = new { type = "boolean", description = "模糊匹配，默认 true" }
                },
                required = new[] { "filePath", "diffContent" }
            });

        RegisterFunction("update_file", knowledgeFunctions.UpdateKnowledgeFile,
            "更新文件（追加或替换）",
            new
            {
                type = "object",
                properties = new
                {
                    filePath = new { type = "string", description = "文件路径" },
                    content = new { type = "string", description = "内容" },
                    mode = new { type = "string", description = "append/replace", @default = "append" }
                },
                required = new[] { "filePath", "content" }
            });

        RegisterFunction("search", knowledgeFunctions.SearchKnowledgeBase,
            "语义搜索知识库",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "搜索关键词或问题" },
                    maxResults = new { type = "integer", description = "最大结果数", @default = 5 }
                },
                required = new[] { "query" }
            });

        RegisterFunction("read_file", knowledgeFunctions.ReadKnowledgeFile,
            "读取文件完整内容",
            new
            {
                type = "object",
                properties = new { filePath = new { type = "string", description = "文件路径" } },
                required = new[] { "filePath" }
            });

        RegisterFunction("delete_file", knowledgeFunctions.DeleteKnowledgeFile,
            "删除文件",
            new
            {
                type = "object",
                properties = new { filePath = new { type = "string", description = "文件路径" } },
                required = new[] { "filePath" }
            });

        RegisterFunction("list_files", knowledgeFunctions.ListKnowledgeFiles,
            "列出所有知识库文件",
            new { type = "object", properties = new { } });

        // 配置
        RegisterFunction("set_config", configFunctions.ModifyAppConfig,
            "修改应用配置",
            new
            {
                type = "object",
                properties = new
                {
                    key = new { type = "string", description = "配置项: Temperature/MaxTokens/FontSize" },
                    value = new { type = "string", description = "新值" }
                },
                required = new[] { "key", "value" }
            });

        RegisterFunction("get_config", configFunctions.GetAppConfig,
            "获取应用配置",
            new
            {
                type = "object",
                properties = new { section = new { type = "string", description = "AI/Appearance/Memory/All", @default = "All" } }
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
