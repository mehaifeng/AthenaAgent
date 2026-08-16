using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// <c>parse_office_document</c> 工具的实现：仅在模型显式调用该工具时，
/// 将受支持的本地 Office/PDF 文档上传至 MinerU，并通过“上传、轮询、下载”流程返回 Markdown。
/// MainConversationView 底部的附件入口只会把文件复制到附件存储区并向模型提供元数据，
/// 不会预加载、解析、摘要或索引文档内容，也不会自动调用本工具。
/// </summary>
public class DocumentParserFunctions
{
    private readonly IDocumentParserService _documentParserService;
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger _logger;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx"
    };

    public DocumentParserFunctions(
        IDocumentParserService documentParserService,
        IFileSystemService fileSystemService,
        ILogger logger)
    {
        _documentParserService = documentParserService;
        _fileSystemService = fileSystemService;
        _logger = logger.ForContext<DocumentParserFunctions>();
    }

    public async Task<FunctionResult> ParseOfficeDocumentAsync(string path, string? outputPath = null)
    {
        if (!_documentParserService.IsEnabled)
        {
            return FunctionResult.FailureResult("Document parsing is not enabled. Ask the user to enable it in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FunctionResult.FailureResult("A file 'path' is required.");
        }

        string fullPath;
        try
        {
            // The remote parser applies its mode-specific 10/200 MB limit. Keep all path,
            // blacklist and symlink checks here, but do not incorrectly cap Precision mode
            // at the ordinary local-read quota.
            fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return FunctionResult.FailureResult($"Document path was blocked by the file-system security policy: {ex.Message}");
        }

        if (!File.Exists(fullPath))
        {
            return FunctionResult.FailureResult($"File not found: {path}");
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            return FunctionResult.FailureResult(
                $"Unsupported document format '{extension}'. Supported formats: {string.Join(", ", SupportedExtensions.OrderBy(value => value))}.");
        }

        var fileName = Path.GetFileName(fullPath);
        var token = ToolExecutionContext.CurrentCancellationToken;
        _logger.Information("parse_office_document invoked for {FileName}", fileName);

        DocumentParseResult result;
        try
        {
            result = await _documentParserService.ParseAsync(fullPath, fileName, token);
        }
        catch (OperationCanceledException)
        {
            return FunctionResult.FailureResult("Document parsing was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "parse_office_document failed: {FileName}", fileName);
            return FunctionResult.FailureResult($"Document parsing failed: {ex.Message}");
        }

        if (!result.Success)
        {
            return FunctionResult.FailureResult(result.ErrorMessage ?? "Document parsing failed.");
        }

        // 落盘模式：一本书的 Markdown 可能是几十万 token。写文件后只回摘要 + 大纲，
        // 让模型用 read_system_file / search_in_file 按需分段取用，而不是一次灌满上下文。
        // 与 convert_document / convert_spreadsheet 的「导出后再读」设计保持一致。
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var markdown = result.Markdown ?? string.Empty;
            try
            {
                if (!await _fileSystemService.WriteFileAsync(outputPath, markdown))
                {
                    return FunctionResult.FailureResult($"Parsed the document but could not write the Markdown to '{outputPath}'.");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                return FunctionResult.FailureResult(
                    $"Parsed the document but the output path was blocked by the file-system security policy: {ex.Message}");
            }

            var outline = DocumentOutlineExtractor.FromMarkdown(markdown, ".md", "mineru");
            return FunctionResult.SuccessResult(
                $"Parsed '{fileName}' and wrote {markdown.Length} characters of Markdown to '{outputPath}'. "
                + "Read it with read_system_file (line ranges or sectionTitle) or locate sections with search_in_directory.",
                new
                {
                    outputPath,
                    characters = markdown.Length,
                    headings = outline.Entries.Take(80)
                        .Select(entry => new { entry.Level, entry.Title, entry.LineNumber })
                        .ToList(),
                    headingsTruncated = outline.Entries.Count > 80
                });
        }

        return FunctionResult.SuccessResult(result.Markdown);
    }
}
