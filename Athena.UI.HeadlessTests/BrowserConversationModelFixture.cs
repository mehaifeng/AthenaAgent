using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athena.UI.Services.ComputerUse;
using Avalonia;

internal sealed class BrowserConversationModelFixture : HttpMessageHandler
{
    private int _chatRequests;
    private int _browserRequests;
    private int _browserRetryRequests;
    private string? _retryObservation;

    public const string NextBrowserModel = "test-vision-replacement";
    public string StartingUrl { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool WaitForStop { get; set; }
    public int ChatRequests => Volatile.Read(ref _chatRequests);
    public int BrowserRetryRequests => Volatile.Read(ref _browserRetryRequests);
    public PixelSize ViewportBeforeResize { get; private set; }
    public PixelSize ViewportAfterResize { get; private set; }
    public TaskCompletionSource WaitingToSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WaitingForResizedSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowResizedSubmit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                && result.RootElement.GetProperty("message").GetString() == "Received 苏州",
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
        if (WaitForStop)
        {
            Check(request.GetProperty("model").GetString() == NextBrowserModel
                && request.GetProperty("input")[0].GetProperty("content")[1].GetProperty("type").GetString() == "input_image",
                "the next browser task in the same chat must send its observation to the newly selected model");
            WaitingToStop.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DecisionCancelled.TrySetResult();
                throw;
            }
        }

        var instructions = request.GetProperty("instructions").GetString()!;
        Check(instructions.Contains("Ctrl+L", StringComparison.Ordinal) && instructions.Contains("\"action\":\"navigate\"", StringComparison.Ordinal),
            "the browser planner understands address bar instructions");
        var format = request.GetProperty("text").GetProperty("format");
        var schema = format.GetProperty("schema");
        Check(format.GetProperty("type").GetString() == "json_schema"
            && format.GetProperty("strict").GetBoolean()
            && schema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "element")
            && schema.GetProperty("properties").GetProperty("element").GetProperty("type")
                .EnumerateArray().Select(type => type.GetString()).SequenceEqual(["integer", "null"]),
            "the browser planner requests a structured action with a numeric control index");
        var effort = request.GetProperty("reasoning").GetProperty("effort").GetString();
        var retry = effort == "none";
        Check(effort is "low" or "none" && request.GetProperty("max_output_tokens").GetInt32() == (retry ? 8192 : 4096),
            "only the retry turns off reasoning and increases the output budget");
        var content = request.GetProperty("input")[0].GetProperty("content");
        var observation = content[0].GetProperty("text").GetString()!;
        var imageUrl = content[1].GetProperty("image_url").GetString()!;
        var screenshot = Convert.FromBase64String(imageUrl[(imageUrl.IndexOf(',') + 1)..]);
        var viewport = new PixelSize(BinaryPrimitives.ReadInt32BigEndian(screenshot.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(screenshot.AsSpan(20, 4)));
        Check(observation.Contains($"image size {viewport.Width} × {viewport.Height}", StringComparison.Ordinal),
            "the model receives the actual capture dimensions");

        var step = retry ? Volatile.Read(ref _browserRequests) : Interlocked.Increment(ref _browserRequests);
        if (retry)
        {
            var retryNumber = Interlocked.Increment(ref _browserRetryRequests);
            var correction = step switch
            {
                1 when retryNumber == 1 => "reached its output limit",
                1 or 2 => "returned no action text",
                3 => "Use a numbered control index as an integer",
                _ => throw new InvalidOperationException("Unexpected browser retry.")
            };
            Check(instructions.Contains(correction, StringComparison.Ordinal)
                && _retryObservation == content.GetRawText(), "the retry corrects the failure against exactly the same observation");
            if (step == 1 && retryNumber == 1)
                return CompletedAction(" ");
            _retryObservation = null;
        }
        else if (step is >= 1 and <= 3)
        {
            _retryObservation = content.GetRawText();
            return step switch
            {
                1 => JsonSerializer.Serialize(new
                {
                    id = "resp_browser_truncated", @object = "response", created_at = 1785580000, status = "incomplete", model = "deepseek-flash",
                    incomplete_details = new { reason = "max_output_tokens" }, output = Array.Empty<object>()
                }),
                2 => CompletedAction(" "),
                _ => CompletedAction("""{"action":"type_text","element":"Title","x":40,"y":70,"text":"wrong input"}""")
            };
        }

        if (step == 1)
            Check(observation.Contains("Starting page", StringComparison.Ordinal), "startingUrl overrides the browsing tab");
        if (step == 2)
            Check(observation.Contains("No navigation occurred", StringComparison.Ordinal)
                && observation.Contains("Starting page", StringComparison.Ordinal), "a non-web address was rejected without navigation");
        if (step == 3)
            Check(observation.Contains("[1] text field Title", StringComparison.Ordinal), "the destination was observed after navigating");
        if (step == 4)
            Check(observation.Contains("[2] button 苏州", StringComparison.Ordinal),
                "sequential trusted key events opened the station suggestions before the model moved focus");
        if (step == 5)
        {
            Check(observation.Contains("""Progress from previous observations: {"candidate":"苏州"}""", StringComparison.Ordinal),
                "a structured summary remains available after clicking the station suggestion");
            ViewportBeforeResize = viewport;
            WaitingToSubmit.TrySetResult();
            await AllowSubmit.Task.WaitAsync(token);
        }
        if (step == 6)
        {
            Check(observation.Contains("The browser viewport changed.", StringComparison.Ordinal),
                "a click selected before the resize was discarded");
            var button = Regex.Match(observation, @"\[2\] button Submit at \(\d+, (\d+)\)");
            Check(button.Success && int.Parse(button.Groups[1].Value, CultureInfo.InvariantCulture) > 720,
                "the new observation reaches the button below the former fixed viewport");
            ViewportAfterResize = viewport;
            WaitingForResizedSubmit.TrySetResult();
            await AllowResizedSubmit.Task.WaitAsync(token);
        }
        if (step == 7)
        {
            Check(viewport == ViewportAfterResize && observation.Contains("Received 苏州", StringComparison.Ordinal),
                "the selected station survived losing focus and the hidden task submitted it");
            return CompletedAction("""{"action":"finish","success":"true","summary":"Received 苏州"}""");
        }
        if (step == 4)
            return CompletedAction("""{"action":"click","element":2,"x":[],"y":{},"key":[],"success":null,"summary":{"candidate":"苏州"}}""");

        var action = step switch
        {
            1 => new ComputerUseAction("navigate", Url: "file:///tmp/athena-browser-test"),
            2 => new ComputerUseAction("navigate", Url: Destination),
            3 => new ComputerUseAction("type_text", Element: 1, Text: "苏州"),
            5 or 6 => new ComputerUseAction("click", Element: 2),
            _ => throw new InvalidOperationException("Unexpected browser decision.")
        };
        var answer = JsonSerializer.Serialize(action, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return step == 3
            ? CompletedAction(answer.Replace("\"element\":1", "\"element\":\"[1]\"", StringComparison.Ordinal), split: true)
            : CompletedAction(answer);
    }

    private static string CompletedAction(string answer, bool split = false)
    {
        string[] parts = split ? [answer[..(answer.Length / 2)], answer[(answer.Length / 2)..]] : [answer];
        return JsonSerializer.Serialize(new
        {
            id = "resp_browser_decision", @object = "response", created_at = 1785580000, status = "completed", model = "deepseek-flash",
            output = new[] { new { id = "msg_browser_decision", type = "message", role = "assistant", status = "completed",
                content = parts.Select(part => new { type = "output_text", text = part, annotations = Array.Empty<object>() }) } }
        });
    }

    private static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Computer Use test: " + message);
    }
}
