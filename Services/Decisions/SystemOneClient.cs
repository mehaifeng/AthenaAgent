using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Decisions;

/// <summary>一道 Noul 题：决策模型返回「这条陈述成立」的概率（0~1）。</summary>
public sealed record SystemOneNoulQuestion(string Name, string Instructions);

public sealed record SystemOneRequest(
    string BaseUrl,
    string ApiKey,
    string Model,
    JsonObject State,
    IReadOnlyList<SystemOneNoulQuestion> Questions);

public sealed record SystemOneResult(
    IReadOnlyDictionary<string, double> Nouls,
    string? Model,
    string? Id,
    double? Cost);

/// <summary>决策请求失败：HTTP 非 2xx、响应不是 JSON、或有题目没拿到答案。消息里不回显请求内容。</summary>
public sealed class SystemOneException(string message) : Exception(message);

public interface ISystemOneClient
{
    Task<SystemOneResult> DecideAsync(SystemOneRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// TypeSafe「System One」决策接口（Jev）：<c>POST {BaseUrl}/systemone</c>。请求是一份 state 加一组带类型的问题，
/// 响应只有类型化的答案与概率，没有生成文本。OpenRouter（<c>https://openrouter.ai/api/v1</c>）与 TypeSafe 官方
/// 都是这个路径，所以直接复用已有的 provider 连接；OpenAI SDK 的客户端只认 chat / responses，用不上。
/// 目前只实现 Noul（是非概率）一种题型，Choice / Score 等真有用处再加。
/// </summary>
public sealed class SystemOneClient : ISystemOneClient
{
    // 超时由调用方的 CancellationToken 决定，共享客户端本身不设上限。
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;

    public SystemOneClient() : this(SharedHttpClient) { }

    public SystemOneClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<SystemOneResult> DecideAsync(SystemOneRequest request, CancellationToken cancellationToken)
    {
        var questions = new JsonObject();
        foreach (var question in request.Questions)
        {
            questions[question.Name] = new JsonObject { ["type"] = "noul", ["instructions"] = question.Instructions };
        }
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["state"] = request.State.DeepClone(),
            ["questions"] = questions
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, request.BaseUrl.TrimEnd('/') + "/systemone")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SystemOneException($"HTTP {(int)response.StatusCode}: {ErrorMessage(text)}");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new SystemOneException("response is not JSON: " + ex.Message);
        }

        var nouls = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (root?["answers"]?[question.Name]?["noul"] is JsonValue value && value.TryGetValue<double>(out var probability))
            {
                nouls[question.Name] = probability;
            }
        }
        // 少一道题就是失败：悄悄少记一个分数，事后统计会把缺失读成「没问过」。
        if (nouls.Count != request.Questions.Count)
        {
            throw new SystemOneException($"answered {nouls.Count} of {request.Questions.Count} questions");
        }

        return new SystemOneResult(nouls, StringOf(root?["model"]), StringOf(root?["id"]), DoubleOf(root?["usage"]?["cost"]));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? DoubleOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static string ErrorMessage(string body)
    {
        try
        {
            var text = StringOf(JsonNode.Parse(body)?["error"]?["message"]) ?? "(no message)";
            return text.Length <= 200 ? text : text[..200] + "…";
        }
        catch (JsonException)
        {
            return "(response is not JSON)";
        }
    }
}
