using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Parsers;

/// <summary>
/// 基于 MinerU 的文档解析服务，支持两种远端 API：
/// - 极速解析（Agent Lightweight）：无需 Token，按 IP 限流。
/// - 精度解析（Precision）：需要 Token，支持更大文件与更高精度。
/// 两种方式均为「提交 -> 轮询 -> 下载结果」的异步流程。
/// </summary>
public class MinerUDocumentParserService : IDocumentParserService, IDisposable
{
    private const string AgentBaseUrl = "https://mineru.net/api/v1/agent";
    private const string PrecisionFileUrlsEndpoint = "https://mineru.net/api/v4/file-urls/batch";
    private const string PrecisionBatchResultEndpoint = "https://mineru.net/api/v4/extract-results/batch";

    // 轮询参数：每 3 秒查询一次，最长等待 5 分钟。
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);

    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public MinerUDocumentParserService(IConfigService configService, ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<MinerUDocumentParserService>();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public bool IsEnabled => _configService.Load().DocumentParserEnabled;

    public async Task<DocumentParseResult> ParseAsync(
        string filePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return DocumentParseResult.Fail($"File not found: {fileName}");
        }

        var config = _configService.Load();
        try
        {
            return config.DocumentParserMode == DocumentParserMode.Precision
                ? await ParseWithPrecisionAsync(filePath, fileName, config.DocumentParserToken, cancellationToken)
                : await ParseWithAgentAsync(filePath, fileName, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "MinerU document parsing failed: {FileName}", fileName);
            return DocumentParseResult.Fail(ex.Message);
        }
    }

    // ===================== Agent Lightweight =====================

    private async Task<DocumentParseResult> ParseWithAgentAsync(
        string filePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        // 1. 申请签名上传地址
        var requestBody = JsonSerializer.Serialize(new { file_name = fileName });
        using var submitContent = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var submitResponse = await _httpClient.PostAsync(
            $"{AgentBaseUrl}/parse/file",
            submitContent,
            cancellationToken);

        var submitJson = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        using var submitDoc = JsonDocument.Parse(submitJson);
        var submitRoot = submitDoc.RootElement;
        if (GetInt(submitRoot, "code") != 0)
        {
            return DocumentParseResult.Fail(GetString(submitRoot, "msg") ?? "Submit failed");
        }

        var data = submitRoot.GetProperty("data");
        var taskId = GetString(data, "task_id");
        var uploadUrl = GetString(data, "file_url");
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(uploadUrl))
        {
            return DocumentParseResult.Fail("Invalid submit response (missing task_id/file_url)");
        }

        // 2. PUT 上传文件到 OSS
        await UploadFileAsync(uploadUrl!, filePath, cancellationToken);

        // 3. 轮询结果
        var markdownUrl = await PollAgentResultAsync(taskId!, cancellationToken);
        if (markdownUrl == null)
        {
            return DocumentParseResult.Fail("Parsing timed out or failed");
        }

        // 4. 下载 Markdown
        var markdown = await _httpClient.GetStringAsync(markdownUrl, cancellationToken);
        return DocumentParseResult.Ok(markdown);
    }

    private async Task<string?> PollAgentResultAsync(string taskId, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < PollTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = await _httpClient.GetStringAsync($"{AgentBaseUrl}/parse/{taskId}", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var state = GetString(data, "state");

            if (string.Equals(state, "done", StringComparison.OrdinalIgnoreCase))
            {
                return GetString(data, "markdown_url");
            }

            if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(GetString(data, "err_msg") ?? "Extract failed");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    // ===================== Precision =====================

    private async Task<DocumentParseResult> ParseWithPrecisionAsync(
        string filePath,
        string fileName,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return DocumentParseResult.Fail("Precision API requires a Token");
        }

        // 1. 申请批量文件上传地址
        var requestBody = JsonSerializer.Serialize(new
        {
            files = new[] { new { name = fileName } },
            model_version = "vlm"
        });

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, PrecisionFileUrlsEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var submitResponse = await _httpClient.SendAsync(submitRequest, cancellationToken);
        var submitJson = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        using var submitDoc = JsonDocument.Parse(submitJson);
        var submitRoot = submitDoc.RootElement;
        if (GetInt(submitRoot, "code") != 0)
        {
            return DocumentParseResult.Fail(GetString(submitRoot, "msg") ?? "Submit failed");
        }

        var data = submitRoot.GetProperty("data");
        var batchId = GetString(data, "batch_id");
        string? uploadUrl = null;
        if (data.TryGetProperty("file_urls", out var urls) && urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0)
        {
            uploadUrl = urls[0].GetString();
        }

        if (string.IsNullOrWhiteSpace(batchId) || string.IsNullOrWhiteSpace(uploadUrl))
        {
            return DocumentParseResult.Fail("Invalid submit response (missing batch_id/file_urls)");
        }

        // 2. PUT 上传文件
        await UploadFileAsync(uploadUrl!, filePath, cancellationToken);

        // 3. 轮询批量结果
        var zipUrl = await PollPrecisionResultAsync(batchId!, fileName, token, cancellationToken);
        if (zipUrl == null)
        {
            return DocumentParseResult.Fail("Parsing timed out or failed");
        }

        // 4. 下载 zip 并提取 full.md
        var markdown = await DownloadMarkdownFromZipAsync(zipUrl, cancellationToken);
        return DocumentParseResult.Ok(markdown);
    }

    private async Task<string?> PollPrecisionResultAsync(string batchId, string fileName, string token, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < PollTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PrecisionBatchResultEndpoint}/{batchId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            if (data.TryGetProperty("extract_result", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var state = GetString(item, "state");
                    if (string.Equals(state, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        return GetString(item, "full_zip_url");
                    }

                    if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(GetString(item, "err_msg") ?? "Extract failed");
                    }
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private async Task<string> DownloadMarkdownFromZipAsync(string zipUrl, CancellationToken cancellationToken)
    {
        var bytes = await _httpClient.GetByteArrayAsync(zipUrl, cancellationToken);
        using var zipStream = new MemoryStream(bytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // 优先取 full.md；否则退而取任意 .md 文件。
        var entry = archive.GetEntry("full.md")
                    ?? archive.Entries.FirstOrDefaultMarkdown();
        if (entry == null)
        {
            throw new InvalidOperationException("No markdown file found in extract result");
        }

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // ===================== Shared helpers =====================

    private async Task UploadFileAsync(string uploadUrl, string filePath, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        using var content = new StreamContent(fileStream);
        // MinerU 上传不需要 Content-Type 头，移除默认值以兼容 OSS 签名校验。
        content.Headers.ContentType = null;
        using var response = await _httpClient.PutAsync(uploadUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"File upload failed: HTTP {(int)response.StatusCode}");
        }
    }

    private static int GetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : -1;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal static class ZipArchiveExtensions
{
    public static ZipArchiveEntry? FirstOrDefaultMarkdown(this System.Collections.ObjectModel.ReadOnlyCollection<ZipArchiveEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
