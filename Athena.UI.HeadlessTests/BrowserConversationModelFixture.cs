using System.Net;
using System.Text;
using System.Text.Json;
using Athena.UI.Services.ComputerUse;

internal sealed class BrowserConversationModelFixture : HttpMessageHandler
{
    private int _chatRequests;
    private int _browserRequests;

    public string StartingUrl { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool WaitForStop { get; set; }
    public int ChatRequests => Volatile.Read(ref _chatRequests);
    public TaskCompletionSource WaitingToSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WaitingToStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DecisionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var browser = request.RequestUri!.Host == "api.deepseek.com";
        var body = browser
            ? await ReplyToBrowserAsync(input.RootElement, cancellationToken)
            : ReplyToConversation(input.RootElement);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, browser ? "application/json" : "text/event-stream")
        };
    }

    private string ReplyToConversation(JsonElement request)
    {
        var round = Interlocked.Increment(ref _chatRequests);
        if (round == 2)
        {
            var output = request.GetProperty("input").EnumerateArray().Last();
            Check(output.GetProperty("type").GetString() == "function_call_output"
                && output.GetProperty("call_id").GetString() == "call_browser_1", "result must be paired in the next model request");
            using var result = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Check(result.RootElement.GetProperty("success").GetBoolean()
                && result.RootElement.GetProperty("message").GetString() == "Received Athena verified",
                "the main model receives the result confirmed by the next page");
            return ResponsesSseHandler.FinalRoundBody;
        }

        Check(round is 1 or 3, "only the completed task may resume the model loop");
        Check(request.GetProperty("tools").EnumerateArray().Any(tool =>
            tool.TryGetProperty("name", out var name) && name.GetString() == "run_computer_task"
            && tool.GetProperty("parameters").GetProperty("properties").TryGetProperty("startingUrl", out _)),
            "the main model receives the actual browser tool declaration");
        var arguments = JsonSerializer.Serialize(new { instruction = "Click the address bar and navigate to " + Destination, startingUrl = StartingUrl });
        var callId = $"call_browser_{round}";
        var itemId = $"fc_browser_{round}";
        var item = new { id = itemId, type = "function_call", status = "completed", call_id = callId, name = "run_computer_task", arguments };
        return Sse(new { type = "response.created", sequence_number = 0,
            response = new { id = $"resp_browser_{round}", @object = "response", created_at = 1785580000, status = "in_progress", model = "test-main", output = Array.Empty<object>() } })
            + Sse(new { type = "response.output_item.added", sequence_number = 1, output_index = 0,
                item = new { id = itemId, type = "function_call", status = "in_progress", call_id = callId, name = "run_computer_task", arguments = "" } })
            + Sse(new { type = "response.function_call_arguments.delta", sequence_number = 2, item_id = itemId, output_index = 0, delta = arguments })
            + Sse(new { type = "response.function_call_arguments.done", sequence_number = 3, item_id = itemId, output_index = 0, arguments })
            + Sse(new { type = "response.output_item.done", sequence_number = 4, output_index = 0, item })
            + Sse(new { type = "response.completed", sequence_number = 5,
                response = new { id = $"resp_browser_{round}", @object = "response", created_at = 1785580000, status = "completed", model = "test-main",
                    output = new[] { item }, usage = new { input_tokens = 40, output_tokens = 5, total_tokens = 45 } } });
    }

    private async Task<string> ReplyToBrowserAsync(JsonElement request, CancellationToken token)
    {
        var instructions = request.GetProperty("instructions").GetString()!;
        Check(instructions.Contains("Ctrl+L", StringComparison.Ordinal) && instructions.Contains("\"action\":\"navigate\"", StringComparison.Ordinal),
            "the browser planner understands address bar instructions");
        var observation = request.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Check(observation.Contains("image size 800 × 720", StringComparison.Ordinal), "the task viewport is stable");

        if (WaitForStop)
        {
            WaitingToStop.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DecisionCancelled.TrySetResult();
                throw;
            }
        }

        var step = Interlocked.Increment(ref _browserRequests);
        if (step == 1)
            Check(observation.Contains("Starting page", StringComparison.Ordinal), "startingUrl overrides the browsing tab");
        if (step == 2)
            Check(observation.Contains("No navigation occurred", StringComparison.Ordinal)
                && observation.Contains("Starting page", StringComparison.Ordinal), "a non-web address was rejected without navigation");
        if (step == 3)
            Check(observation.Contains("[1] text field Title", StringComparison.Ordinal), "the destination was observed after navigating");
        if (step == 4)
        {
            WaitingToSubmit.TrySetResult();
            await AllowSubmit.Task.WaitAsync(token);
        }
        if (step == 5)
            Check(observation.Contains("Received Athena verified", StringComparison.Ordinal), "hidden task submitted the real form");

        var action = step switch
        {
            1 => new ComputerUseAction("navigate", Url: "file:///tmp/athena-browser-test"),
            2 => new ComputerUseAction("navigate", Url: Destination),
            3 => new ComputerUseAction("type_text", Element: 1, Text: "Athena verified"),
            4 => new ComputerUseAction("click", Element: 2),
            5 => new ComputerUseAction("finish", Success: true, Summary: "Received Athena verified"),
            _ => throw new InvalidOperationException("Unexpected browser decision.")
        };
        var answer = JsonSerializer.Serialize(action, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonSerializer.Serialize(new
        {
            id = "resp_browser_decision", @object = "response", created_at = 1785580000, status = "completed", model = "deepseek-flash",
            output = new[] { new { id = "msg_browser_decision", type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text = answer, annotations = Array.Empty<object>() } } } }
        });
    }

    private static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Computer Use test: " + message);
    }
}
