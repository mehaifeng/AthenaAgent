using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// Function 调用结果
/// </summary>
public class FunctionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }

    public static FunctionResult SuccessResult(string message = "", object? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static FunctionResult FailureResult(string message) =>
        new() { Success = false, Message = message };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        success = Success,
        message = Message,
        data = Data
    });
}

/// <summary>
/// Function 注册接口（预留用于 Function Calling）
/// </summary>
public interface IFunctionRegistry
{
    /// <summary>
    /// 获取所有已注册的工具定义
    /// </summary>
    IEnumerable<object> GetToolDefinitions();

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
    /// 是否有注册的 Function
    /// </summary>
    bool HasFunctions { get; }
}
