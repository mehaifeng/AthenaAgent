using System.IO.Compression;
using System.Xml;
using System.Xml.Schema;

namespace MiniMaxAIDocx.Core.Validation;

public class XsdValidator
{
    public ValidationResult Validate(string docxPath, string xsdPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("DOCX does not contain word/document.xml");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var xmlContent = reader.ReadToEnd();

        return ValidateXml(xmlContent, xsdPath);
    }

    public ValidationResult ValidateXml(string xmlContent, string xsdPath)
    {
        var result = new ValidationResult();
        var settings = new XmlReaderSettings();

        var schemaSet = new XmlSchemaSet();
        try
        {
            // XmlSchemaSet does not reliably resolve an imported schema when its resolver is
            // disabled. Preload the bundled relationship-attribute schema explicitly, then
            // add the requested schema. This remains local and deterministic.
            var schemaDirectory = Path.GetDirectoryName(Path.GetFullPath(xsdPath));
            var localImports = new[]
            {
                (Namespace: "http://schemas.openxmlformats.org/officeDocument/2006/relationships", File: "relationships.xsd"),
                (Namespace: "http://www.w3.org/XML/1998/namespace", File: "xml.xsd")
            };
            foreach (var import in localImports)
            {
                var schemaPath = schemaDirectory == null ? null : Path.Combine(schemaDirectory, import.File);
                if (schemaPath != null && File.Exists(schemaPath))
                    schemaSet.Add(import.Namespace, schemaPath);
            }
            schemaSet.Add(null, xsdPath);
            schemaSet.Compile();
        }
        catch (XmlSchemaException ex)
        {
            result.Errors.Add(new ValidationError
            {
                LineNumber = ex.LineNumber,
                LinePosition = ex.LinePosition,
                Message = $"Schema compilation error: {ex.Message}",
                Severity = "Error"
            });
            return result;
        }
        settings.Schemas = schemaSet;
        settings.ValidationType = ValidationType.Schema;
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;

        settings.ValidationEventHandler += (sender, e) =>
        {
            var error = new ValidationError
            {
                LineNumber = e.Exception?.LineNumber ?? 0,
                LinePosition = e.Exception?.LinePosition ?? 0,
                Message = e.Message,
                Severity = e.Severity == XmlSeverityType.Warning ? "Warning" : "Error"
            };

            if (e.Severity == XmlSeverityType.Warning)
                result.Warnings.Add(error);
            else
                result.Errors.Add(error);
        };

        try
        {
            using var stringReader = new StringReader(xmlContent);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            while (xmlReader.Read()) { }
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaValidationException)
        {
            result.Errors.Add(new ValidationError
            {
                LineNumber = ex is XmlException xmlException ? xmlException.LineNumber : 0,
                LinePosition = ex is XmlException xmlException2 ? xmlException2.LinePosition : 0,
                Message = $"XML validation error: {ex.Message}",
                Severity = "Error"
            });
        }

        return result;
    }
}
