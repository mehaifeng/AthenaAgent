using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Spreadsheets;
using Serilog;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

public sealed class SpreadsheetFunctions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly XlsxPackageService _xlsx;
    private readonly ILogger _logger;

    public SpreadsheetFunctions(IFileSystemService fileSystemService, XlsxPackageService xlsx, ILogger logger)
    {
        _fileSystemService = fileSystemService;
        _xlsx = xlsx;
        _logger = logger.ForContext<SpreadsheetFunctions>();
    }

    public Task<FunctionResult> InspectSpreadsheetAsync(string path, string? sheet = null, int maxRows = 20, int maxColumns = 20,
        int startRow = 1, int startColumn = 1)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _xlsx.Inspect(fullPath, sheet, maxRows, maxColumns, startRow, startColumn);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet inspected successfully.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet inspection failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet inspection failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> ValidateSpreadsheetAsync(string path)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _xlsx.Validate(fullPath);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet static validation completed.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet validation failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet validation failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> CreateSpreadsheetAsync(string outputPath, string workbookJson, bool overwrite = false)
    {
        try
        {
            var estimate = Math.Max(Encoding.UTF8.GetByteCount(workbookJson) * 2L, 64 * 1024L);
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            _xlsx.Create(fullOutputPath, workbookJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet created successfully.", new
            {
                outputPath = fullOutputPath,
                nextStep = "Run validate_spreadsheet, then open in Excel or LibreOffice when formula recalculation or visual layout verification matters."
            }));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet creation failed for {Path}", outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet creation failed: {JsonPayloadDiagnostics.Explain(ex, ("workbookJson", workbookJson))}"));
        }
    }

    public Task<FunctionResult> EditSpreadsheetAsync(string inputPath, string outputPath, string updatesJson, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            var estimatedSize = new FileInfo(fullInputPath).Length;
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimatedSize);
            _xlsx.Edit(fullInputPath, fullOutputPath, updatesJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet edited successfully; the source workbook was left unchanged.", new
            {
                inputPath = fullInputPath,
                outputPath = fullOutputPath,
                nextStep = "Run validate_spreadsheet. Formula caches are intentionally cleared when formulas change and need recalculation in Excel or LibreOffice."
            }));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet edit failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet edit failed: {JsonPayloadDiagnostics.Explain(ex, ("updatesJson", updatesJson))}"));
        }
    }

    public Task<FunctionResult> ModifySpreadsheetStructureAsync(string inputPath, string outputPath, string operationsJson, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            var estimatedSize = new FileInfo(fullInputPath).Length;
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimatedSize);
            var data = _xlsx.ModifyStructure(fullInputPath, fullOutputPath, operationsJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet structure updated; the source workbook was left unchanged.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet structure edit failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet structure edit failed: {JsonPayloadDiagnostics.Explain(ex, ("operationsJson", operationsJson))}"));
        }
    }

    public Task<FunctionResult> ConvertSpreadsheetAsync(string inputPath, string outputPath, string? sheet = null,
        string? delimiter = null, bool headerRow = false, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            // Delimited text expands when it becomes XML, and a workbook shrinks when it becomes text;
            // reserve the larger of the two so the write quota check never rejects a legitimate conversion.
            var estimatedSize = Math.Max(new FileInfo(fullInputPath).Length * 3L, 256 * 1024L);
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimatedSize);
            var data = _xlsx.ConvertDelimited(fullInputPath, fullOutputPath, sheet, delimiter, headerRow, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Spreadsheet conversion completed.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Spreadsheet conversion failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Spreadsheet conversion failed: {ex.Message}"));
        }
    }
}
