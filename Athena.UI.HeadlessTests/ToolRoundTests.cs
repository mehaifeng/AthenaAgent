using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Athena.UI;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.ConfigSurface;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using Athena.UI.Services.SubAgents;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ChatTool = OpenAI.Chat.ChatTool;

internal static class ToolRoundTests
{
    public static async Task RunAsync()
    {
        await LegacyConfigurationAsync();
        foreach (var protocol in new[] { ProviderProtocol.ChatCompletions, ProviderProtocol.Responses })
        {
            // Exceed the former UI maxima as well as the defaults (50 main / 20 sub-agent).
            await MainAsync(protocol, 205);
            await SubAgentAsync(protocol, 105);
            await MainAsync(protocol, 100, cancelAt: 60);
            await SubAgentAsync(protocol, 100, cancelAt: 25);
            await SubAgentAsync(protocol, 100, cancelAt: 25, timedOut: true);
            await MainAsync(protocol, 6, truncatedRounds: 5);
            await MainAsync(protocol, 100, truncatedRounds: 100);
        }
        Console.WriteLine("[PASS] unlimited main/sub-agent tool rounds: both protocols past former ceilings, cancellation, timeout and bounded truncation recovery");
    }

    public static void VerifySettings()
    {
        var localization = App.Services!.GetRequiredService<ILocalizationService>();
        using var session = new AppConfigurationSession(new HeadlessConfigService(new AppConfig()));
        using var state = new AppSettingsState(session);
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                localization.SwitchLanguage(language);
                var view = new AgentRuntimeSettingsView { DataContext = new AgentRuntimeSettingsViewModel(state) };
                var window = new Window { Content = view, Width = 960, Height = 900 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var controls = view.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
                    Check(controls.Length == 5, "only concurrency, provider retries and timeout controls remain");
                    var timeout = controls.Single(control => control.Maximum == 3600);
                    timeout.Value = 420;
                    Dispatcher.UIThread.RunJobs();
                    Check(state.Config.SubAgentTimeoutSeconds == 420, "sub-agent timeout still binds to the live config");
                    var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text).ToArray();
                    Check(!labels.Any(label => label is "单次回复最大工具轮数" or "单代理最大工具轮数"
                        or "Maximum tool rounds per reply" or "Maximum tool rounds per agent"), "neither removed setting is rendered");
                    Check(labels.Any(label => label?.Contains(language == "zh-CN" ? "工具调用轮数不设上限" : "Tool rounds are unlimited", StringComparison.Ordinal) == true),
                        "settings explain unlimited rounds in both languages");
                }
                finally { window.Close(); }
            }
        }
        finally { localization.SwitchLanguage("zh-CN"); }
        Console.WriteLine("[PASS] execution settings remove both round limits and retain live timeout binding in both languages");
    }

    private static async Task LegacyConfigurationAsync()
    {
        Check(!ConfigFieldCatalog.Fields.Any(field => field.Key is "AI.MainConversationMaxIterations" or "SubAgents.MaxIterations"),
            "the model cannot read or write removed limits");
        Check(typeof(AppConfig).GetProperty("MainConversationMaxIterations") is null
            && typeof(AppConfig).GetProperty("SubAgentMaxIterations") is null, "new configs have no round-limit properties");
        var root = Path.Combine(Path.GetTempPath(), "athena-tool-round-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TemporaryPathService(root);
            foreach (var legacy in new[] { "\"mainConversationMaxIterations\":1", "\"subAgentMaxIterations\":1",
                "\"mainConversationMaxIterations\":1,\"subAgentMaxIterations\":1" })
            foreach (var asyncLoad in new[] { false, true })
            {
                await File.WriteAllTextAsync(paths.GetConfigFilePath(), "{" + legacy
                    + ",\"configSchemaVersion\":8,\"mainConversationMaxParallel\":3,\"subAgentMaxParallel\":5,\"subAgentTimeoutSeconds\":360}");
                var service = new ConfigService(paths);
                var config = asyncLoad ? await service.LoadAsync() : service.Load();
                Check(config.MainConversationMaxParallel == 3 && config.SubAgentMaxParallel == 5 && config.SubAgentTimeoutSeconds == 360,
                    "migration preserves concurrency and timeout settings");
                var saved = await File.ReadAllTextAsync(paths.GetConfigFilePath());
                Check(!saved.Contains("MaxIterations", StringComparison.OrdinalIgnoreCase), "both old keys disappear on sync and async load");
                await service.SaveAsync(config);
                Check(!(await File.ReadAllTextAsync(paths.GetConfigFilePath())).Contains("MaxIterations", StringComparison.OrdinalIgnoreCase),
                    "saving never reintroduces either limit");
            }
        }
        finally { Directory.Delete(root, recursive: true); } // Only this test's fresh GUID directory.
    }

    private static AppConfig Configuration(string endpoint, ProviderProtocol protocol)
    {
        var config = new AppConfig();
        config.ContextPolicy.AutoCompress = false;
        var provider = new OpenAiProviderConfiguration
        {
            Id = "tool-round-test", ProviderPreset = "Custom", BaseUrl = endpoint,
            ApiKey = "tool-round-test-key-not-real", Protocol = protocol
        };
        provider.Models.Add(new ProviderModelDescriptor { Id = "tool-round-model", Capability = ModelCapability.Text });
        config.AiModels.Providers.Add(provider);
        config.AiModels.MainConversation.ProviderId = provider.Id;
        config.AiModels.MainConversation.Model = "tool-round-model";
        config.AiModels.SubAgent.ProviderId = provider.Id;
        config.AiModels.SubAgent.Model = "tool-round-model";
        return config;
    }

    private static async Task MainAsync(ProviderProtocol protocol, int rounds, int cancelAt = 0, int truncatedRounds = 0)
    {
        await using var model = new LoopbackModel(rounds, truncatedRounds);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var registry = new ProbeRegistry(() => { if (cancelAt > 0 && model.RequestCount == cancelAt) stop.Cancel(); });
        var config = Configuration(model.BaseUrl, protocol);
        var service = new OpenAIChatService(config, new HeadlessPromptService(), functionRegistry: registry,
            metadataResolver: new ModelMetadataResolver(new ModelIdentityMatcher()), contextPolicyResolver: new ModelContextPolicyResolver());
        var context = new ConversationContext();
        var text = new StringBuilder();
        try
        {
            await foreach (var delta in service.StreamMessageAsync("Run all tool rounds.", context, cancellationToken: stop.Token)) text.Append(delta);
        }
        catch (OperationCanceledException) when (cancelAt > 0 && stop.IsCancellationRequested) { }
        if (cancelAt > 0)
        {
            Check(registry.Calls == cancelAt && model.RequestCount == cancelAt, "main cancellation stops the loop after crossing its old default");
        }
        else if (truncatedRounds > 5)
        {
            Check(registry.Calls == 0 && model.RequestCount == 6 && text.ToString().Contains("incomplete", StringComparison.Ordinal),
                "repeatedly incomplete arguments stop after five retries without executing tools");
        }
        else
        {
            Check(text.ToString() == "done" && registry.Calls == rounds - truncatedRounds && model.RequestCount == rounds + 1,
                $"main completes every valid round and obtains the final answer on {protocol}: {text}");
            Check(context.Messages.Count(message => message.Role == "tool") == rounds - truncatedRounds,
                "all valid tool results remain in the conversation context");
        }
    }

    private static async Task SubAgentAsync(ProviderProtocol protocol, int rounds, int cancelAt = 0, bool timedOut = false)
    {
        await using var model = new LoopbackModel(rounds);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var progress = new SubAgentViewModel { Cts = stop };
        // The orchestrator links its timeout token; reproduce that signal at a deterministic tool boundary.
        if (timedOut) progress.TimeoutAt = DateTime.UtcNow.AddSeconds(-1);
        var registry = new ProbeRegistry(() =>
        {
            if (cancelAt <= 0 || model.RequestCount != cancelAt) return;
            if (timedOut) stop.Cancel();
            else progress.CancelCommand.Execute(null);
        });
        var runner = new SubAgentRunner(new HeadlessConfigService(Configuration(model.BaseUrl, protocol)), registry, Log.Logger);
        var result = await runner.RunAsync(new SubAgentTaskInput { Title = "long test", Instruction = "Run all tool rounds." },
            progress, _ => null, action => action(), stop.Token);
        if (cancelAt > 0)
        {
            Check(!result.Success && registry.Calls == cancelAt && model.RequestCount == cancelAt,
                "sub-agent cancellation stops after crossing its old default");
            Check(progress.State == (timedOut ? SubAgentState.Error : SubAgentState.Cancelled)
                && result.Summary == (timedOut ? "Timed out." : "Cancelled."), "timeout and user cancellation remain distinct");
        }
        else
        {
            Check(result.Success && result.Summary == "done" && progress.State == SubAgentState.Done
                && progress.Step == rounds && registry.Calls == rounds && model.RequestCount == rounds + 1,
                $"sub-agent completes beyond its former ceiling on {protocol}: {result.Summary}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Tool rounds: " + message);
    }

    private sealed class ProbeRegistry(Action onCall) : IFunctionRegistry
    {
        private readonly ChatTool _tool = ChatTool.CreateFunctionTool("get_file_info", "Return synthetic metadata.",
            BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}"));
        public int Calls { get; private set; }
        public bool HasFunctions => true;
        public IEnumerable<object> GetToolDefinitions(bool includeOfficeTools = false) => [_tool];
        public IEnumerable<object> GetToolDefinitions(IEnumerable<string> names) => names.Contains(_tool.FunctionName) ? [_tool] : [];
        public Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson)
        {
            Check(functionName == _tool.FunctionName && argumentsJson == "{}", "only complete declared calls execute");
            Calls++;
            onCall();
            return Task.FromResult(FunctionResult.SuccessResult("ok"));
        }
    }

    private sealed class LoopbackModel : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _pump;
        private readonly int _rounds;
        private readonly int _truncatedRounds;
        private bool _stopping;
        public string BaseUrl { get; }
        public int RequestCount { get; private set; }

        public LoopbackModel(int rounds, int truncatedRounds = 0)
        {
            _rounds = rounds;
            _truncatedRounds = truncatedRounds;
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!_stopping)
                {
                    var request = await _listener.GetContextAsync();
                    using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                    RequestCount++;
                    var responses = request.Request.Url!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal);
                    var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
                    var output = responses ? ResponsesBody(streaming) : ChatBody(streaming);
                    var bytes = Encoding.UTF8.GetBytes(output);
                    request.Response.ContentType = streaming ? "text/event-stream" : "application/json";
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
            }
            catch (HttpListenerException) when (_stopping) { }
            catch (ObjectDisposedException) when (_stopping) { }
        }

        private static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        private string Arguments => RequestCount <= _truncatedRounds ? "{\"path\":" : "{}";
        private string CallId => "call_" + RequestCount;

        private string ChatBody(bool streaming)
        {
            var tool = RequestCount <= _rounds;
            object message = tool
                ? new { role = "assistant", tool_calls = new[] { new { index = 0, id = CallId, type = "function", function = new { name = "get_file_info", arguments = Arguments } } } }
                : new { role = "assistant", content = "done" };
            object choice = streaming ? new { index = 0, delta = message, finish_reason = tool ? "tool_calls" : "stop" }
                : new { index = 0, message, finish_reason = tool ? "tool_calls" : "stop" };
            var response = new { id = "chat_tool_round", @object = streaming ? "chat.completion.chunk" : "chat.completion", created = 1785580000,
                model = "tool-round-model", choices = new[] { choice }, usage = new { prompt_tokens = 40, completion_tokens = 5, total_tokens = 45 } };
            return streaming ? Sse(response) + "data: [DONE]\n\n" : JsonSerializer.Serialize(response);
        }

        private string ResponsesBody(bool streaming)
        {
            var tool = RequestCount <= _rounds;
            var truncated = RequestCount <= _truncatedRounds;
            object item = tool ? new { id = "fc_" + RequestCount, type = "function_call", status = truncated ? "incomplete" : "completed", call_id = CallId, name = "get_file_info", arguments = Arguments }
                : new { id = "msg_final", type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text = "done", annotations = Array.Empty<object>() } } };
            var response = new { id = "resp_" + RequestCount, @object = "response", created_at = 1785580000, status = "completed", model = "tool-round-model",
                output = new[] { item }, usage = new { input_tokens = 40, output_tokens = 5, total_tokens = 45 } };
            if (!streaming) return JsonSerializer.Serialize(response);
            if (!tool) return ResponsesSseHandler.FinalRoundBody;
            return Sse(new { type = "response.created", sequence_number = 0, response = new { id = "resp_" + RequestCount, @object = "response", created_at = 1785580000, status = "in_progress", model = "tool-round-model", output = Array.Empty<object>() } })
                + Sse(new { type = "response.output_item.added", sequence_number = 1, output_index = 0, item = new { id = "fc_" + RequestCount, type = "function_call", status = "in_progress", call_id = CallId, name = "get_file_info", arguments = "" } })
                + Sse(new { type = "response.function_call_arguments.delta", sequence_number = 2, item_id = "fc_" + RequestCount, output_index = 0, delta = Arguments })
                + Sse(new { type = "response.function_call_arguments.done", sequence_number = 3, item_id = "fc_" + RequestCount, output_index = 0, arguments = Arguments })
                + Sse(new { type = "response.output_item.done", sequence_number = 4, output_index = 0, item })
                + Sse(new { type = "response.completed", sequence_number = 5, response }) + "data: [DONE]\n\n";
        }

        public async ValueTask DisposeAsync()
        {
            _stopping = true;
            _listener.Close();
            await _pump;
        }
    }
}
