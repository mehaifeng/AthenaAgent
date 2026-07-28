using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// parse_office_document 工具的实现：把本地 Office/PDF 文档通过 MinerU 解析为 Markdown 文本，
/// 供模型直接读取内容。解析流程与 MainConversationView 底部附件插入 Office 文件的处理完全一致
/// （上传 -> 轮询 -> 下载 Markdown），只是入口从 UI 附件改为工具调用。
/// </summary>
public class DocumentParserFunctions
{
    private readonly IDocumentParserService _documentParserService;
    private readonly ILogger _logger;

    public DocumentParserFunctions(IDocumentParserService documentParserService, ILogger logger)
    {
        _documentParserService = documentParserService;
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

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return FunctionResult.FailureResult($"File not found: {path}");
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
            _logger.Warning(ex, "parse_office_document 解析失败: {FileName}", fileName);
            return FunctionResult.FailureResult($"Document parsing failed: {ex.Message}");
        }

        if (!result.Success)
        {
            return FunctionResult.FailureResult(result.ErrorMessage ?? "Document parsing failed.");
        }

        return FunctionResult.SuccessResult(result.Markdown);
    }
}
