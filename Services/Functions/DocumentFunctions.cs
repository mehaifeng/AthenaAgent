using Athena.UI.Services.Documents;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// Tool surface for Word documents. Every write goes to a distinct output path and the source is
/// never modified, mirroring the spreadsheet tools.
/// </summary>
public sealed class DocumentFunctions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly DocxPackageService _docx;
    private readonly ILogger _logger;

    public DocumentFunctions(IFileSystemService fileSystemService, DocxPackageService docx, ILogger logger)
    {
        _fileSystemService = fileSystemService;
        _docx = docx;
        _logger = logger.ForContext<DocumentFunctions>();
    }

    public Task<FunctionResult> InspectDocumentAsync(string path, int startParagraph = 1, int maxParagraphs = 60, bool includeTableText = false)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _docx.Inspect(fullPath, startParagraph, maxParagraphs, includeTableText);
            return Task.FromResult(FunctionResult.SuccessResult("Document inspected successfully.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Document inspection failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Document inspection failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> ValidateDocumentAsync(string path)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _docx.Validate(fullPath);
            return Task.FromResult(FunctionResult.SuccessResult("Document static validation completed.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Document validation failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Document validation failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> CreateDocumentAsync(string outputPath, string documentJson, bool overwrite = false)
    {
        try
        {
            var estimate = Math.Max(Encoding.UTF8.GetByteCount(documentJson) * 3L, 128 * 1024L);
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            var data = _docx.Create(fullOutputPath, documentJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Document created successfully.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Document creation failed for {Path}", outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Document creation failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> EditDocumentAsync(string inputPath, string outputPath, string operationsJson, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            var estimate = new FileInfo(fullInputPath).Length + Encoding.UTF8.GetByteCount(operationsJson) * 3L;
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            var data = _docx.Edit(fullInputPath, fullOutputPath, operationsJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Document edited successfully; the source document was left unchanged.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Document edit failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Document edit failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> ConvertDocumentAsync(string inputPath, string outputPath, bool markdown = true, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            var estimate = Math.Max(new FileInfo(fullInputPath).Length, 256 * 1024L);
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            var data = _docx.ConvertToText(fullInputPath, fullOutputPath, markdown, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Document conversion completed.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Document conversion failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Document conversion failed: {ex.Message}"));
        }
    }
}
