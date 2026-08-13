using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Athena.UI.Services.Ooxml;

/// <summary>
/// Shared plumbing for every OOXML package Athena edits in-process: bounded ZIP access,
/// part/relationship resolution, schema-ordered element insertion and atomic output.
/// Format-specific services (workbooks, documents, presentations) derive from this and add
/// only the vocabulary of their own markup.
/// </summary>
public abstract class OoxmlPackageService
{
    protected static readonly XNamespace RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    protected static readonly XNamespace PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    protected static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    protected const int MaxEntries = 20_000;
    protected const long MaxEntryBytes = 128L * 1024 * 1024;
    protected const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Opens a package with the guards every untrusted OOXML file needs: entry count, part size,
    /// total uncompressed size, compression ratio and canonical entry names.
    /// </summary>
    protected static ZipArchive OpenPackage(string path, ZipArchiveMode mode)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("OOXML package not found.", path);
        var archive = ZipFile.Open(path, mode);
        try
        {
            if (archive.Entries.Count > MaxEntries) throw new InvalidDataException($"Package contains more than {MaxEntries} ZIP entries.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntryName(entry.FullName);
                if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"OOXML part is too large: {entry.FullName}");
                total = checked(total + entry.Length);
                if (total > MaxPackageBytes) throw new InvalidDataException($"Uncompressed package exceeds {MaxPackageBytes} bytes.");
                if (entry.CompressedLength > 0 && entry.Length / Math.Max(1d, entry.CompressedLength) > 200d)
                    throw new InvalidDataException($"Suspicious compression ratio in OOXML part: {entry.FullName}");
            }
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    protected static XDocument ReadXml(ZipArchiveEntry entry, bool preserveWhitespace = false)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxEntryBytes,
            IgnoreWhitespace = !preserveWhitespace
        });
        return XDocument.Load(reader, preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None);
    }

    protected static void ReplaceXmlEntry(ZipArchive archive, string name, XDocument document)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false });
        document.Save(writer);
    }

    protected static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    protected static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException($"Missing required OOXML part: {name}");

    /// <summary>Maps relationship ids declared for <paramref name="partPath"/> to resolved part paths.</summary>
    protected static Dictionary<string, string> ReadRelationshipTargets(ZipArchive archive, string partPath)
    {
        var relationshipsEntry = archive.GetEntry(RelationshipsPathFor(partPath));
        if (relationshipsEntry is null) return new Dictionary<string, string>(StringComparer.Ordinal);

        var relationships = ReadXml(relationshipsEntry);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relationship in relationships.Root?.Elements(PackageRelationshipNs + "Relationship") ?? [])
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id is null || target is null) continue;
            if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
            targets[id] = ResolvePartPath(partPath, target);
        }
        return targets;
    }

    protected static string RelationshipsPathFor(string partPath)
    {
        var separator = partPath.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : partPath[..(separator + 1)];
        var fileName = separator < 0 ? partPath : partPath[(separator + 1)..];
        return $"{directory}_rels/{fileName}.rels";
    }

    protected static string SourcePartForRelationships(string relationshipsPath)
    {
        if (relationshipsPath.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        const string marker = "/_rels/";
        var index = relationshipsPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || !relationshipsPath.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return relationshipsPath[..index] + "/" + relationshipsPath[(index + marker.Length)..^5];
    }

    protected static string ResolvePartPath(string sourcePart, string target)
    {
        if (target.StartsWith('/')) return NormalizePartPath(target[1..]);
        var directory = sourcePart.Contains('/') ? sourcePart[..(sourcePart.LastIndexOf('/') + 1)] : string.Empty;
        return NormalizePartPath(directory + target);
    }

    protected static string NormalizePartPath(string path)
    {
        var stack = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0) throw new InvalidDataException($"OOXML relationship escapes package root: {path}");
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(segment);
        }
        return string.Join('/', stack);
    }

    protected static void ValidateEntryName(string name)
    {
        var raw = name.Replace('\\', '/');
        if (raw.StartsWith('/') || raw.Contains(':'))
            throw new InvalidDataException($"Unsafe ZIP entry name: {name}");
        var normalized = NormalizePartPath(name);
        if (!normalized.Equals(raw.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe or non-canonical ZIP entry name: {name}");
    }

    /// <summary>
    /// Returns a child element, creating it at the position its schema demands. OOXML content
    /// models are ordered sequences, so appending a legal element in the wrong place is exactly
    /// what makes Word/Excel offer to "repair" a file.
    /// </summary>
    protected static XElement GetOrCreateOrderedChild(XElement parent, XNamespace ns, IReadOnlyList<string> childOrder, string name)
    {
        var existing = parent.Element(ns + name);
        if (existing is not null) return existing;

        var created = new XElement(ns + name);
        var position = -1;
        for (var index = 0; index < childOrder.Count; index++)
        {
            if (!string.Equals(childOrder[index], name, StringComparison.Ordinal)) continue;
            position = index;
            break;
        }
        if (position < 0) throw new ArgumentException($"'{name}' is not part of the declared child order.");

        XElement? following = null;
        for (var index = position + 1; index < childOrder.Count && following is null; index++)
            following = parent.Element(ns + childOrder[index]);

        if (following is null) parent.Add(created); else following.AddBeforeSelf(created);
        return created;
    }

    protected static void EnsureCanWrite(string outputPath, bool overwrite)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Output path must include a valid parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath) && !overwrite) throw new IOException("Output file already exists. Set overwrite=true only when replacement is intended.");
    }

    /// <summary>
    /// Editing tools never touch their input: the caller writes a new package beside the target
    /// and the move only happens once it is complete.
    /// </summary>
    protected static void EnsureDistinctPaths(string inputPath, string outputPath)
    {
        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("outputPath must differ from inputPath so the original file remains recoverable.");
        if (!Path.GetExtension(inputPath).Equals(Path.GetExtension(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("inputPath and outputPath must use the same extension so macro-enabled packages are not silently downgraded.");
    }

    protected static void AtomicWrite(string outputPath, bool overwrite, Action<string> writer)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            writer(temporaryPath);
            File.Move(temporaryPath, fullOutput, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    protected static string CorePropertiesXml()
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:creator>AthenaAgent</dc:creator><cp:lastModifiedBy>AthenaAgent</cp:lastModifiedBy><dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>";
    }

    protected static string RootRelationshipsXml(string documentPartPath, string documentRelationshipType) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        $"<Relationships xmlns=\"{PackageRelationshipNs}\">" +
        $"<Relationship Id=\"rId1\" Type=\"{documentRelationshipType}\" Target=\"{documentPartPath}\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
        "</Relationships>";

    protected static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
