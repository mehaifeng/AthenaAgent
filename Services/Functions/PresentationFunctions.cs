using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Presentations;
using Serilog;

namespace Athena.UI.Services.Functions;

/// <summary>Sandboxed tool surface for native PresentationML operations.</summary>
public sealed class PresentationFunctions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly PptxPackageService _pptx;
    private readonly ILogger _logger;

    public PresentationFunctions(IFileSystemService fileSystemService, PptxPackageService pptx, ILogger logger)
    {
        _fileSystemService = fileSystemService;
        _pptx = pptx;
        _logger = logger.ForContext<PresentationFunctions>();
    }

    public Task<FunctionResult> InspectPresentationAsync(string path, int startSlide = 1, int maxSlides = 40,
        bool includeShapes = true, bool includeNotes = true)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _pptx.Inspect(fullPath, startSlide, maxSlides, includeShapes, includeNotes);
            return Task.FromResult(FunctionResult.SuccessResult("Presentation inspected successfully.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Presentation inspection failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Presentation inspection failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> CreatePresentationAsync(string outputPath, string presentationJson, bool overwrite = false)
    {
        try
        {
            var estimate = Math.Max(Encoding.UTF8.GetByteCount(presentationJson) * 4L, 256 * 1024L);
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            var securedJson = ResolveEmbeddedImagePaths(presentationJson);
            var data = _pptx.Create(fullOutputPath, securedJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Presentation created successfully.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Presentation creation failed for {Path}", outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Presentation creation failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> EditPresentationAsync(string inputPath, string outputPath, string operationsJson, bool overwrite = false)
    {
        try
        {
            var fullInputPath = _fileSystemService.GetAbsoluteSecurePath(inputPath, enforceReadSizeLimit: false);
            var estimate = new FileInfo(fullInputPath).Length + Encoding.UTF8.GetByteCount(operationsJson) * 3L;
            var fullOutputPath = _fileSystemService.GetAbsoluteSecureWritePath(outputPath, estimate);
            var data = _pptx.Edit(fullInputPath, fullOutputPath, operationsJson, overwrite);
            return Task.FromResult(FunctionResult.SuccessResult("Presentation edited successfully; the source presentation was left unchanged.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Presentation edit failed from {InputPath} to {OutputPath}", inputPath, outputPath);
            return Task.FromResult(FunctionResult.FailureResult($"Presentation edit failed: {ex.Message}"));
        }
    }

    public Task<FunctionResult> ValidatePresentationAsync(string path)
    {
        try
        {
            var fullPath = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            var data = _pptx.Validate(fullPath);
            return Task.FromResult(FunctionResult.SuccessResult("Presentation static validation completed.", data));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Presentation validation failed for {Path}", path);
            return Task.FromResult(FunctionResult.FailureResult($"Presentation validation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Image paths are nested inside the presentation specification rather than top-level tool
    /// arguments. Resolve them through the same sandbox policy as every ordinary file read before
    /// the package service sees them.
    /// </summary>
    private string ResolveEmbeddedImagePaths(string presentationJson)
    {
        var root = JsonNode.Parse(presentationJson) as JsonObject
            ?? throw new ArgumentException("presentationJson must be a JSON object.");
        if (root["slides"] is not JsonArray slides) return root.ToJsonString();
        foreach (var slide in slides.OfType<JsonObject>())
        {
            if (slide["elements"] is not JsonArray elements) continue;
            foreach (var element in elements.OfType<JsonObject>())
            {
                if (!string.Equals(element["type"]?.GetValue<string>(), "image", StringComparison.OrdinalIgnoreCase)) continue;
                var path = element["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path)) continue;
                element["path"] = _fileSystemService.GetAbsoluteSecurePath(path, enforceReadSizeLimit: false);
            }
        }
        return root.ToJsonString();
    }
}
