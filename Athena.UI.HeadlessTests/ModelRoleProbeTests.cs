using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using SkiaSharp;

internal static class ModelRoleProbeTests
{
    public static async Task RunAsync()
    {
        await VerifyMigrationAsync();
        var config = new AppConfig();
        var provider = new OpenAiProviderConfiguration { BaseUrl = "https://role-test.invalid", ApiKey = "role-test" };
        var vision = new ProviderModelDescriptor { Id = "vision" };
        var text = new ProviderModelDescriptor
        {
            Id = "text-only", Capability = ModelCapability.Text,
            Reported = new() { InputModalities = ["text"] }
        };
        provider.Models.Add(vision);
        provider.Models.Add(text);
        config.AiModels.Providers.Add(provider);
        foreach (var role in Enum.GetValues<AiModelRole>())
        {
            var settings = OpenAiModelRuntimeFactory.GetRoleSettings(config, role);
            settings.ProviderId = provider.Id;
            settings.Model = role is AiModelRole.ComputerUse or AiModelRole.ImageRecognition ? "vision" : "text-only";
        }
        var service = new ModelRoleProbeService();
        var computerUse = service.Resolve(config, AiModelRole.ComputerUse);
        Check(service.Vision(config, provider, text) == CapabilitySupport.Unsupported
            && service.NeedsVisionProof(computerUse), "Known text-only models must be excluded and unknown vision models must be proved.");

        using var endpoint = new ProbeEndpoint();
        using var http = new HttpClient(endpoint, disposeHandler: false);
        using var transport = new HttpClientPipelineTransport(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.TestAsync(service.Resolve(config, AiModelRole.TitleGeneration), timeout.Token, transport);
        await service.TestAsync(service.Resolve(config, AiModelRole.Embedding), timeout.Token, transport);
        await service.TestAsync(service.Resolve(config, AiModelRole.ImageRecognition), timeout.Token, transport);
        await service.TestAsync(computerUse, timeout.Token, transport);
        Check(!service.NeedsVisionProof(computerUse), "A successful image action must unlock the exact model selection.");

        provider.Protocol = ProviderProtocol.Responses;
        var responses = service.Resolve(config, AiModelRole.ComputerUse);
        Check(service.NeedsVisionProof(responses), "A protocol change invalidates the previous vision proof.");
        await service.TestAsync(responses, timeout.Token, transport);
        await service.TestAsync(service.Resolve(config, AiModelRole.Companion), timeout.Token, transport);
        Check(endpoint.Requests == 6 && endpoint.ImagesRead == 3 && !service.NeedsVisionProof(responses),
            "Role probes must send real image bytes, embeddings and selected-protocol requests.");

        provider.BaseUrl = "https://another-probe.invalid";
        endpoint.BreakImage = true;
        var broken = service.Resolve(config, AiModelRole.ComputerUse);
        try
        {
            await service.TestAsync(broken, timeout.Token, transport);
            throw new InvalidOperationException("A wrong visual answer passed the role test.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("expected Computer Use action", StringComparison.Ordinal)) { }
        Check(service.NeedsVisionProof(broken), "A visually incorrect answer cannot authorize Computer Use.");
        Console.WriteLine("[PASS] Role model tests use Chat Completions, Responses, real synthetic image codes and finite embeddings");
        await VerifyRowStateAsync();
    }

    private static async Task VerifyMigrationAsync()
    {
        var directory = Directory.CreateTempSubdirectory("athena-computer-role-");
        try
        {
            var paths = new TemporaryPathService(directory.FullName);
            var config = new AppConfig();
            var provider = new OpenAiProviderConfiguration { BaseUrl = "https://api.deepseek.com", ApiKey = "fixture" };
            config.AiModels.Providers.Add(provider);
            var legacy = JsonSerializer.SerializeToNode(config, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            legacy["aiModels"]!.AsObject().Remove("computerUse");
            await File.WriteAllTextAsync(paths.GetConfigFilePath(), legacy.ToJsonString());
            var store = new ConfigService(paths);
            var migrated = store.Load();
            Check(migrated.AiModels.ComputerUse.ProviderId == provider.Id
                && migrated.AiModels.ComputerUse.Model == "deepseek-flash", "The missing role must be seeded using the former browser assignment.");
            migrated.AiModels.ComputerUse = new();
            await store.SaveAsync(migrated);
            Check(new ConfigService(paths).Load().AiModels.ComputerUse.Model.Length == 0,
                "Reloading an explicitly cleared role must not seed it a second time.");
            Console.WriteLine("[PASS] Existing browser models migrate once and explicitly empty Computer Use choices survive reload");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifyRowStateAsync()
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
        port.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var config = new AppConfig();
        var provider = new OpenAiProviderConfiguration { BaseUrl = origin, ApiKey = "role-test-secret" };
        provider.Models.Add(new() { Id = "first" });
        provider.Models.Add(new() { Id = "second" });
        config.AiModels.Providers.Add(provider);
        config.AiModels.TitleGeneration.ProviderId = provider.Id;
        config.AiModels.TitleGeneration.Model = "first";
        using var row = new ProviderRoleSelectionViewModel("Title", AiModelRole.TitleGeneration, config,
            new ModelRoleProbeService(), (_, fallback) => fallback);
        var pending = row.TestCommand.ExecuteAsync(null);
        var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
        Check(row.IsTesting, "A running probe must render its busy state.");
        row.SelectedModel = provider.Models[1];
        await RespondAsync(first, "first", 200, timeout.Token);
        await pending.WaitAsync(timeout.Token);
        Check(!row.IsTesting && !row.TestFailed && row.TestLatency == null,
            "The old response must not update the changed model's row.");

        var success = row.TestCommand.ExecuteAsync(null);
        await RespondAsync(await listener.GetContextAsync().WaitAsync(timeout.Token), "second", 200, timeout.Token);
        await success.WaitAsync(timeout.Token);
        Check(row.TestLatency != null && !row.ShowTestIcon && row.TestTooltip.Contains("succeeded", StringComparison.Ordinal),
            "A successful test must expose a compact latency and success tooltip.");

        provider.ApiKey = "second-secret";
        Check(row.TestLatency == null, "Editing credentials must clear the prior test state.");
        var failure = row.TestCommand.ExecuteAsync(null);
        await RespondAsync(await listener.GetContextAsync().WaitAsync(timeout.Token), "second", 401, timeout.Token);
        await failure.WaitAsync(timeout.Token);
        Check(row.TestFailed && row.TestIcon == "AthenaIconFailure" && !row.TestTooltip.Contains(provider.ApiKey, StringComparison.Ordinal),
            "An error must appear as an icon with a credential-free tooltip.");
        Console.WriteLine("[PASS] Provider-role rows show loading, latency and safe errors without accepting stale results");
    }

    private static async Task RespondAsync(HttpListenerContext context, string model, int status, CancellationToken token)
    {
        using var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json";
        var body = status == 200 ? JsonSerializer.Serialize(new
        {
            id = "chat_model_rows", @object = "chat.completion", model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } }
        }) : "{\"error\":{\"message\":\"Rejected key second-secret\"}}";
        try { await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body), token); }
        catch (HttpListenerException) { /* Settings changes may have cancelled the old request. */ }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeEndpoint : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int ImagesRead { get; private set; }
        public bool BreakImage { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            using var incoming = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = incoming.RootElement;
            var model = root.GetProperty("model").GetString()!;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/embeddings", StringComparison.Ordinal))
            {
                Check(root.GetProperty("encoding_format").GetString() == "float", "The embedding probe must use the shipped wire format.");
                return Reply(new { data = new[] { new { index = 0, embedding = new[] { 0.5, 0.25 } } } });
            }

            var responses = path.EndsWith("/responses", StringComparison.Ordinal);
            var messages = root.GetProperty(responses ? "input" : "messages").EnumerateArray().ToArray();
            var code = "OK";
            if (model == "vision")
            {
                var image = messages.Last().GetProperty("content").EnumerateArray().Last().GetProperty("image_url");
                var uri = image.ValueKind == JsonValueKind.Object ? image.GetProperty("url").GetString()! : image.GetString()!;
                Check(uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "The vision challenge must carry a PNG.");
                var png = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
                code = Decode(png);
                Check(code.Length == 6, "The transmitted image must contain a random six-digit code.");
                ImagesRead++;
                if (BreakImage) code = "000000";
                if (responses && root.GetProperty("instructions").GetString()?.Contains("Computer Use", StringComparison.OrdinalIgnoreCase) == true
                    || responses && root.GetProperty("instructions").GetString()?.Contains("Operate the user's", StringComparison.Ordinal) == true
                    || !responses && messages.Length > 1)
                    code = JsonSerializer.Serialize(new { action = "finish", success = true, summary = code });
            }

            return responses ? Reply(new
            {
                id = "resp_model_test", @object = "response", status = "completed", model,
                output = new[] { new { id = "msg_model_test", type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = code, annotations = Array.Empty<object>() } } } }
            }) : Reply(new
            {
                id = "chat_model_test", @object = "chat.completion", model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = code }, finish_reason = "stop" } }
            });
        }

        private static string Decode(byte[] png)
        {
            using var bitmap = SKBitmap.Decode(png);
            var result = new StringBuilder();
            (int X, int Y)[] sample = [(13, 3), (3, 14), (38, 14), (13, 36), (3, 46), (38, 46), (13, 69)];
            for (var digit = 0; digit < 6; digit++)
            {
                var lit = string.Concat(sample.Select((pixel, index) => bitmap.GetPixel(
                    13 + digit * 52 + pixel.X, 11 + pixel.Y).Red < 128 ? index.ToString(CultureInfo.InvariantCulture) : ""));
                var value = Array.IndexOf(new[] { "012456", "25", "02346", "02356", "1235", "01356", "013456", "025", "0123456", "012356" }, lit);
                Check(value >= 0, "Every challenge glyph must be legible.");
                result.Append(value);
            }
            return result.ToString();
        }

        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
