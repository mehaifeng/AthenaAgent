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

    public async Task<FunctionResult> ParseOfficeDocumentAsync(string path)
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

        return FunctionResult.SuccessResult(result.Markdown);
    }
}
