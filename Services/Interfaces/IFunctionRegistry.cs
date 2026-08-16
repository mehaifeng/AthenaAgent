using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// Function 调用结果
/// </summary>
public class FunctionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public IReadOnlyList<ChatAttachment> GeneratedAttachments { get; set; } = [];

    /// <summary>
    /// 由 <see cref="IFunctionRegistry.ExecuteAsync"/> 的体量闸门写入的、已压进预算的结果 JSON。
    /// 设值后 <see cref="ToJson"/> 直接返回它——所有调用方（主对话 / 子代理 / 知识库维护）
    /// 都经 ToJson 取上下文载荷，因此截断在单一 chokepoint 生效，无法被绕过。
    /// </summary>
    public string? BudgetedJson { get; set; }

    public static FunctionResult SuccessResult(
        string message = "",
        object? data = null,
        IReadOnlyList<ChatAttachment>? generatedAttachments = null) =>
        new()
        {
            Success = true,
            Message = message,
            Data = data,
            GeneratedAttachments = generatedAttachments ?? []
        };

    public static FunctionResult FailureResult(
        string message,
        object? data = null,
        IReadOnlyList<ChatAttachment>? generatedAttachments = null) =>
        new()
        {
            Success = false,
            Message = message,
            Data = data,
            GeneratedAttachments = generatedAttachments ?? []
        };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string ToJson() => BudgetedJson ?? SerializeRaw();

    /// <summary>未经体量闸门的原始序列化。闸门自身用它取待压缩的载荷。</summary>
    public string SerializeRaw() => JsonSerializer.Serialize(new
    {
        success = Success,
        message = Message,
        data = Data
    }, _jsonOptions);
}

/// <summary>
/// Function 注册接口（预留用于 Function Calling）
/// </summary>
public interface IFunctionRegistry
{
    /// <summary>
    /// 获取所有已注册的工具定义。
    /// <paramref name="includeOfficeTools"/> 由调用方在构建请求快照时判定（见 OfficeToolRelevance）：
    /// 工具列表随快照一次性绑定、回合内不再重建，因此这个决定只能在此刻做出，
    /// 不能交给模型在回合中途"解锁"。
    /// </summary>
    IEnumerable<object> GetToolDefinitions(bool includeOfficeTools = false);

    /// <summary>
    /// 根据名称列表获取工具定义
    /// </summary>
    /// <param name="toolNames">工具名称列表</param>
    /// <returns>工具定义列表</returns>
    IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames);

    /// <summary>
    /// 执行指定的 Function
    /// </summary>
    /// <param name="functionName">函数名</param>
    /// <param name="argumentsJson">参数 JSON</param>
    /// <returns>执行结果</returns>
    Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson);

    /// <summary>
    /// Gets the estimated token count for the active tool declarations.
    /// </summary>
    int GetToolDeclarationTokenCount(bool includeOfficeTools = false);

    /// <summary>
    /// 是否有注册的 Function
    /// </summary>
    bool HasFunctions { get; }
}
