using System.CommandLine;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MiniMaxAIDocx.Core.Commands;

/// <summary>
/// Pipeline C: safely apply template resources and section layout, or replace a
/// marker-delimited template content zone while preserving package integrity.
/// </summary>
public static class ApplyTemplateCommand
{
    public static Command Create()
    {
        var input = RequiredString("--input", "Source DOCX containing content");
        var template = RequiredString("--template", "Template DOCX containing formatting/structure");
        var output = RequiredString("--output", "Output DOCX file");
        var mode = new Option<string>("--mode") { Description = "overlay or base-replace" };
        mode.DefaultValueFactory = _ => "overlay";
        var startMarker = new Option<string>("--start-marker") { Description = "Unique start paragraph for base-replace" };
        var endMarker = new Option<string>("--end-marker") { Description = "Unique end paragraph for base-replace" };
        var applyHeadersFooters = new Option<bool>("--apply-headers-footers")
        {
            Description = "Copy template headers/footers per mapped section (overlay only)"
        };

        var command = new Command("apply-template", "Apply a DOCX template with explicit structural safeguards")
        {
            input, template, output, mode, startMarker, endMarker, applyHeadersFooters
        };

        command.SetAction(parseResult =>
        {
            var inputPath = parseResult.GetValue(input)!;
            var templatePath = parseResult.GetValue(template)!;
            var outputPath = parseResult.GetValue(output)!;
            var selectedMode = parseResult.GetValue(mode)?.Trim().ToLowerInvariant();

            if (!File.Exists(inputPath))
            {
                Fail($"Input file not found: {inputPath}");
                return;
            }
            if (!File.Exists(templatePath))
            {
                Fail($"Template file not found: {templatePath}");
                return;
            }
            if (selectedMode is not ("overlay" or "base-replace"))
            {
                Fail("--mode must be overlay or base-replace.");
                return;
            }

            var resolvedInput = Path.GetFullPath(inputPath);
            var resolvedTemplate = Path.GetFullPath(templatePath);
            var resolvedOutput = Path.GetFullPath(outputPath);
            if (string.Equals(resolvedOutput, resolvedInput, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolvedOutput, resolvedTemplate, StringComparison.OrdinalIgnoreCase))
            {
                Fail("Output must be distinct from both source and template.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput) ?? Directory.GetCurrentDirectory());
            var temporaryPath = resolvedOutput + $".template-{Guid.NewGuid():N}.tmp";

            try
            {
                if (selectedMode == "overlay")
                {
                    File.Copy(resolvedInput, temporaryPath, true);
                    ApplyOverlay(temporaryPath, resolvedTemplate, parseResult.GetValue(applyHeadersFooters));
                }
                else
                {
                    var start = parseResult.GetValue(startMarker);
                    var end = parseResult.GetValue(endMarker);
                    if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                        throw new InvalidOperationException("base-replace requires --start-marker and --end-marker.");

                    File.Copy(resolvedTemplate, temporaryPath, true);
                    ApplyBaseReplace(temporaryPath, resolvedInput, start, end);
                }

                File.Move(temporaryPath, resolvedOutput, true);
                Console.WriteLine($"Template mode '{selectedMode}' completed: {resolvedOutput}");
            }
            catch (Exception ex)
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                Fail(ex.Message);
            }
        });

        return command;
    }

    private static void ApplyOverlay(string outputPath, string templatePath, bool applyHeadersFooters)
    {
        using var outputDocument = WordprocessingDocument.Open(outputPath, true);
        using var templateDocument = WordprocessingDocument.Open(templatePath, false);
        var output = outputDocument.MainDocumentPart
            ?? throw new InvalidOperationException("Source has no main document part.");
        var template = templateDocument.MainDocumentPart
            ?? throw new InvalidOperationException("Template has no main document part.");
        var outputRoot = output.Document
            ?? throw new InvalidOperationException("Source has no main document root.");

        var numberingMap = MergeNumbering(template, output);
        MergeStyles(template, output, numberingMap, incomingWins: true);
        CopyTheme(template, output);

        var sectionMap = MapSections(template, output);
        foreach (var (templateSection, outputSection) in sectionMap)
            CopySectionLayout(templateSection, outputSection);

        if (applyHeadersFooters)
            CopyHeadersAndFooters(template, output, sectionMap);

        outputRoot.Save();
        output.StyleDefinitionsPart?.Styles?.Save();
        output.NumberingDefinitionsPart?.Numbering?.Save();
    }

    private static void ApplyBaseReplace(string outputPath, string sourcePath, string startMarker, string endMarker)
    {
        using var outputDocument = WordprocessingDocument.Open(outputPath, true);
        using var sourceDocument = WordprocessingDocument.Open(sourcePath, false);
        var output = outputDocument.MainDocumentPart
            ?? throw new InvalidOperationException("Template has no main document part.");
        var source = sourceDocument.MainDocumentPart
            ?? throw new InvalidOperationException("Source has no main document part.");
        var outputRoot = output.Document
            ?? throw new InvalidOperationException("Template has no document root.");
        var sourceRoot = source.Document
            ?? throw new InvalidOperationException("Source has no document root.");
        var outputBody = outputRoot.Body
            ?? throw new InvalidOperationException("Template has no body.");
        var sourceBody = sourceRoot.Body
            ?? throw new InvalidOperationException("Source has no body.");

        var startMatches = outputBody.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Contains(startMarker, StringComparison.Ordinal))
            .ToList();
        var endMatches = outputBody.Elements<Paragraph>()
            .Where(paragraph => paragraph.InnerText.Contains(endMarker, StringComparison.Ordinal))
            .ToList();
        if (startMatches.Count != 1 || endMatches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Markers must each identify exactly one direct body paragraph; found start={startMatches.Count}, end={endMatches.Count}.");
        }

        var bodyChildren = outputBody.ChildElements.ToList();
        var startIndex = bodyChildren.IndexOf(startMatches[0]);
        var endIndex = bodyChildren.IndexOf(endMatches[0]);
        if (startIndex < 0 || endIndex <= startIndex)
            throw new InvalidOperationException("End marker must occur after start marker.");

        var replacementZone = bodyChildren.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
        if (replacementZone.SelectMany(element => element.Descendants<SectionProperties>()).Any())
        {
            throw new InvalidOperationException(
                "The marker-delimited zone crosses a section boundary. Use one zone per section or a custom Base-Replace script.");
        }

        var numberingMap = MergeNumbering(source, output);
        MergeStyles(source, output, numberingMap, incomingWins: false);
        var content = sourceBody.ChildElements
            .Where(element => element is not SectionProperties)
            .Select(element => element.CloneNode(true))
            .ToList();

        foreach (var paragraphProperties in content.SelectMany(element => element.Descendants<ParagraphProperties>()))
            paragraphProperties.GetFirstChild<SectionProperties>()?.Remove();
        RemapNumbering(content, numberingMap);
        CopyReferencedRelationships(source, output, content);

        var anchor = startMatches[0];
        foreach (var element in content)
            anchor.InsertBeforeSelf(element);
        foreach (var element in replacementZone)
            element.Remove();

        outputRoot.Save();
        output.StyleDefinitionsPart?.Styles?.Save();
        output.NumberingDefinitionsPart?.Numbering?.Save();
    }

    private static IReadOnlyList<(SectionProperties Template, SectionProperties Output)> MapSections(
        MainDocumentPart template,
        MainDocumentPart output)
    {
        var templateSections = template.Document?.Body?.Descendants<SectionProperties>().ToList() ?? [];
        var outputSections = output.Document?.Body?.Descendants<SectionProperties>().ToList() ?? [];
        if (templateSections.Count == 0 || outputSections.Count == 0)
            throw new InvalidOperationException("Both source and template must contain section properties.");

        if (templateSections.Count == 1)
            return outputSections.Select(section => (templateSections[0], section)).ToList();
        if (templateSections.Count == outputSections.Count)
            return templateSections.Zip(outputSections).Select(pair => (pair.First, pair.Second)).ToList();

        throw new InvalidOperationException(
            $"Section counts are incompatible for Overlay (template={templateSections.Count}, source={outputSections.Count}). "
            + "Use Base-Replace or a custom section mapping instead of silently applying the last section everywhere.");
    }

    private static Dictionary<int, int> MergeNumbering(MainDocumentPart incoming, MainDocumentPart output)
    {
        var map = new Dictionary<int, int>();
        var incomingNumbering = incoming.NumberingDefinitionsPart?.Numbering;
        if (incomingNumbering == null)
            return map;

        var outputPart = output.NumberingDefinitionsPart ?? output.AddNewPart<NumberingDefinitionsPart>();
        outputPart.Numbering ??= new Numbering();
        var outputNumbering = outputPart.Numbering;
        var nextAbstractId = outputNumbering.Elements<AbstractNum>()
            .Select(item => item.AbstractNumberId?.Value ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var nextNumberId = outputNumbering.Elements<NumberingInstance>()
            .Select(item => item.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var abstractMap = new Dictionary<int, int>();

        foreach (var abstractNumber in incomingNumbering.Elements<AbstractNum>())
        {
            var oldId = abstractNumber.AbstractNumberId?.Value
                ?? throw new InvalidOperationException("Template numbering contains an abstractNum without abstractNumId.");
            var clone = (AbstractNum)abstractNumber.CloneNode(true);
            clone.AbstractNumberId = nextAbstractId;
            abstractMap[oldId] = nextAbstractId++;
            var firstInstance = outputNumbering.Elements<NumberingInstance>().FirstOrDefault();
            if (firstInstance != null)
                firstInstance.InsertBeforeSelf(clone);
            else
                outputNumbering.Append(clone);
        }

        foreach (var instance in incomingNumbering.Elements<NumberingInstance>())
        {
            var oldId = instance.NumberID?.Value
                ?? throw new InvalidOperationException("Template numbering contains a num without numId.");
            var clone = (NumberingInstance)instance.CloneNode(true);
            clone.NumberID = nextNumberId;
            var abstractReference = clone.GetFirstChild<AbstractNumId>();
            if (abstractReference?.Val?.Value is int oldAbstractId
                && abstractMap.TryGetValue(oldAbstractId, out var newAbstractId))
            {
                abstractReference.Val = newAbstractId;
            }
            map[oldId] = nextNumberId++;
            outputNumbering.Append(clone);
        }

        return map;
    }

    private static void MergeStyles(
        MainDocumentPart incoming,
        MainDocumentPart output,
        IReadOnlyDictionary<int, int> numberingMap,
        bool incomingWins)
    {
        var incomingStyles = incoming.StyleDefinitionsPart?.Styles;
        if (incomingStyles == null)
            return;

        var outputPart = output.StyleDefinitionsPart ?? output.AddNewPart<StyleDefinitionsPart>();
        outputPart.Styles ??= new Styles();
        var outputStyles = outputPart.Styles;

        if (incomingWins)
        {
            foreach (var child in outputStyles.ChildElements.Where(element => element is DocDefaults or LatentStyles).ToList())
                child.Remove();
            foreach (var child in incomingStyles.ChildElements.Where(element => element is DocDefaults or LatentStyles).Reverse())
                outputStyles.PrependChild(child.CloneNode(true));
        }

        foreach (var incomingStyle in incomingStyles.Elements<Style>())
        {
            var id = incomingStyle.StyleId?.Value;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var existing = outputStyles.Elements<Style>()
                .FirstOrDefault(style => string.Equals(style.StyleId?.Value, id, StringComparison.Ordinal));
            if (existing != null && !incomingWins)
                continue;
            existing?.Remove();

            var clone = (Style)incomingStyle.CloneNode(true);
            RemapNumbering([clone], numberingMap);
            outputStyles.Append(clone);
        }
    }

    private static void RemapNumbering(IEnumerable<OpenXmlElement> elements, IReadOnlyDictionary<int, int> numberingMap)
    {
        foreach (var numberingId in elements.SelectMany(element => element.Descendants<NumberingId>()))
        {
            if (numberingId.Val?.Value is int oldId && numberingMap.TryGetValue(oldId, out var newId))
                numberingId.Val = newId;
        }
    }

    private static void CopyTheme(MainDocumentPart template, MainDocumentPart output)
    {
        var incoming = template.ThemePart;
        if (incoming == null)
            return;
        if (output.ThemePart != null)
            output.DeletePart(output.ThemePart);
        var target = output.AddNewPart<ThemePart>();
        using var stream = incoming.GetStream(FileMode.Open, FileAccess.Read);
        target.FeedData(stream);
    }

    private static void CopySectionLayout(SectionProperties template, SectionProperties output)
    {
        var change = output.GetFirstChild<SectionPropertiesChange>();
        foreach (var child in output.ChildElements
                     .Where(element => element is not HeaderReference and not FooterReference and not SectionPropertiesChange)
                     .ToList())
        {
            child.Remove();
        }

        foreach (var child in template.ChildElements
                     .Where(element => element is not HeaderReference and not FooterReference and not SectionPropertiesChange))
        {
            var clone = child.CloneNode(true);
            if (change != null)
                change.InsertBeforeSelf(clone);
            else
                output.Append(clone);
        }
    }

    private static void CopyHeadersAndFooters(
        MainDocumentPart template,
        MainDocumentPart output,
        IReadOnlyList<(SectionProperties Template, SectionProperties Output)> sectionMap)
    {
        var headerCache = new Dictionary<Uri, HeaderPart>();
        var footerCache = new Dictionary<Uri, FooterPart>();

        foreach (var (templateSection, outputSection) in sectionMap)
        {
            foreach (var reference in outputSection.Elements<HeaderReference>().Cast<OpenXmlElement>()
                         .Concat(outputSection.Elements<FooterReference>()).ToList())
            {
                reference.Remove();
            }

            var newReferences = new List<OpenXmlElement>();
            foreach (var reference in templateSection.Elements<HeaderReference>())
            {
                var incomingPart = template.GetPartById(reference.Id?.Value
                    ?? throw new InvalidOperationException("Template header reference has no relationship ID.")) as HeaderPart
                    ?? throw new InvalidOperationException("Template header relationship is invalid.");
                if (!headerCache.TryGetValue(incomingPart.Uri, out var targetPart))
                {
                    targetPart = output.AddNewPart<HeaderPart>();
                    CopyPartDataAndRelationships(incomingPart, targetPart);
                    headerCache[incomingPart.Uri] = targetPart;
                }
                newReferences.Add(new HeaderReference { Type = reference.Type, Id = output.GetIdOfPart(targetPart) });
            }

            foreach (var reference in templateSection.Elements<FooterReference>())
            {
                var incomingPart = template.GetPartById(reference.Id?.Value
                    ?? throw new InvalidOperationException("Template footer reference has no relationship ID.")) as FooterPart
                    ?? throw new InvalidOperationException("Template footer relationship is invalid.");
                if (!footerCache.TryGetValue(incomingPart.Uri, out var targetPart))
                {
                    targetPart = output.AddNewPart<FooterPart>();
                    CopyPartDataAndRelationships(incomingPart, targetPart);
                    footerCache[incomingPart.Uri] = targetPart;
                }
                newReferences.Add(new FooterReference { Type = reference.Type, Id = output.GetIdOfPart(targetPart) });
            }

            var firstLayoutChild = outputSection.ChildElements
                .FirstOrDefault(element => element is not HeaderReference and not FooterReference);
            foreach (var reference in newReferences)
            {
                if (firstLayoutChild != null)
                    firstLayoutChild.InsertBeforeSelf(reference);
                else
                    outputSection.Append(reference);
            }
        }
    }

    private static void CopyPartDataAndRelationships(OpenXmlPart incoming, OpenXmlPart output)
    {
        using (var stream = incoming.GetStream(FileMode.Open, FileAccess.Read))
            output.FeedData(stream);

        foreach (var relationship in incoming.ExternalRelationships)
            output.AddExternalRelationship(relationship.RelationshipType, relationship.Uri, relationship.Id);

        foreach (var pair in incoming.Parts)
        {
            if (pair.OpenXmlPart is not ImagePart image)
            {
                throw new InvalidOperationException(
                    $"Header/footer contains unsupported related part '{pair.OpenXmlPart.ContentType}'. Use a custom template script.");
            }

            var targetImage = output.AddNewPart<ImagePart>(image.ContentType, pair.RelationshipId);
            using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
            targetImage.FeedData(stream);
        }
    }

    private static void CopyReferencedRelationships(
        MainDocumentPart source,
        MainDocumentPart output,
        IEnumerable<OpenXmlElement> content)
    {
        var relationshipAttributes = content.SelectMany(element => new[] { element }.Concat(element.Descendants()))
            .SelectMany(element => element.GetAttributes().Select(attribute => (Element: element, Attribute: attribute)))
            .Where(item => item.Attribute.NamespaceUri == "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                           && item.Attribute.LocalName is "id" or "embed" or "link")
            .ToList();
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in relationshipAttributes.GroupBy(item => item.Attribute.Value, StringComparer.Ordinal))
        {
            var oldId = group.Key;
            if (string.IsNullOrWhiteSpace(oldId))
                continue;

            string newId;
            var external = source.ExternalRelationships.FirstOrDefault(item => item.Id == oldId);
            if (external != null)
            {
                newId = output.AddExternalRelationship(external.RelationshipType, external.Uri).Id;
            }
            else
            {
                OpenXmlPart part;
                try
                {
                    part = source.GetPartById(oldId);
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new InvalidOperationException($"Source content references missing relationship '{oldId}'.");
                }

                if (part is not ImagePart image)
                {
                    throw new InvalidOperationException(
                        $"Base-Replace content references unsupported part '{part.ContentType}'. Use a custom script for this document.");
                }
                var target = output.AddNewPart<ImagePart>(image.ContentType);
                using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
                target.FeedData(stream);
                newId = output.GetIdOfPart(target);
            }
            remap[oldId] = newId;
        }

        foreach (var item in relationshipAttributes)
        {
            var relationshipId = item.Attribute.Value;
            if (relationshipId != null && remap.TryGetValue(relationshipId, out var newId))
                item.Element.SetAttribute(new OpenXmlAttribute(item.Attribute.Prefix, item.Attribute.LocalName, item.Attribute.NamespaceUri, newId));
        }
    }

    private static Option<string> RequiredString(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Environment.ExitCode = 1;
    }
}
