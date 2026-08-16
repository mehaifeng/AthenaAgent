using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Athena.UI.Services.Presentations;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "athena-presentation-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var service = new PptxPackageService();
    var source = Path.Combine(root, "source.pptx");
    var output = Path.Combine(root, "edited.pptx");
    var image = Path.Combine(root, "pixel.png");
    File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z6kAAAAAASUVORK5CYII="));

    var specification = JsonSerializer.Serialize(new
    {
        layout = "wide",
        theme = new { background = "FFFFFF", primary = "203864", accent = "F05A28", font = "Arial", eastAsiaFont = "Microsoft YaHei" },
        slides = new object[]
        {
            new { layout = "title", title = "Native PPTX", subtitle = "Round-trip test" },
            new
            {
                layout = "titleAndContent",
                title = "Cross-run edit",
                bullets = new object[] { "CrossRunToken", new { text = "Nested bullet", level = 1 } },
                elements = new object[]
                {
                    new { type = "image", path = image, x = 9.8, y = 4.8, width = 1.0, height = 1.0, altText = "Test pixel" },
                    new { type = "table", rows = new object[] { new object[] { "Metric", "Value" }, new object[] { "Growth", "18%" } }, x = 7.0, y = 1.7, width = 5.2, height = 2.0, header = true }
                }
            },
            new
            {
                layout = "blank",
                elements = new object[]
                {
                    new { type = "shape", shape = "roundRect", text = "Decision", x = 0.7, y = 0.5, width = 2.4, height = 0.7, fill = "E9EFF7", line = "203864", font = new { size = 20, bold = true } },
                    new { type = "text", name = "Overflow Probe", text = "This deliberately very long line must not fit inside the extremely short text box and should be reported by the native layout estimator.", x = 0.8, y = 2.0, width = 3.0, height = 0.12, font = new { size = 24 } }
                }
            }
        }
    });

    service.Create(source, specification, overwrite: false);
    AssertValid(service, source, "freshly created deck", failures);
    AssertGeneratedPackageSchema(source, failures);
    var createdInspection = ToJson(service.Inspect(source, 1, 40, includeShapes: true, includeNotes: true));
    Assert(createdInspection.GetProperty("slideCount").GetInt32() == 3, "created deck has three slides", failures);
    Assert(createdInspection.GetProperty("slides")[1].GetProperty("pictureCount").GetInt32() == 1, "image is inspected", failures);
    Assert(createdInspection.GetProperty("slides")[1].GetProperty("tableCount").GetInt32() == 1, "table is inspected", failures);
    Assert(ToJson(service.Validate(source)).GetProperty("overflowWarnings").GetArrayLength() > 0,
        "a generated deck is text-fit checked as written, without hand-editing autofit", failures);

    SplitRunAcrossTwoRuns(source);
    var sourceHash = Hash(source);
    var operations = JsonSerializer.Serialize(new object[]
    {
        new { duplicateSlide = 2, after = 3 },
        new { moveSlide = 4, to = 2 },
        new { deleteSlide = 1 },
        new { find = "CrossRunToken", replace = "JoinedToken", all = true },
        new { slide = 1, setTitle = "Duplicated slide" },
        new { slide = 3, addTextBox = new { text = "Added natively", x = 7.0, y = 6.5, width = 2.5, height = 0.4, font = new { size = 12, color = "666666" } } }
    });
    service.Edit(source, output, operations, overwrite: false);
    Assert(Hash(source) == sourceHash, "editing leaves source bytes unchanged", failures);
    AssertValid(service, output, "edited deck", failures);

    var inspection = ToJson(service.Inspect(output, 1, 40, includeShapes: true, includeNotes: true));
    Assert(inspection.GetProperty("slideCount").GetInt32() == 3, "duplicate/move/delete keeps expected count", failures);
    Assert(inspection.GetProperty("slides")[0].GetProperty("title").GetString() == "Duplicated slide", "setTitle targets reordered duplicated slide", failures);
    var allText = string.Join("\n", inspection.GetProperty("slides").EnumerateArray()
        .SelectMany(slide => slide.GetProperty("text").EnumerateArray()).Select(value => value.GetString()));
    Assert(allText.Contains("JoinedToken", StringComparison.Ordinal), "cross-run replacement succeeds", failures);
    Assert(!allText.Contains("CrossRunToken", StringComparison.Ordinal), "old cross-run text is gone", failures);
    Assert(allText.Contains("Added natively", StringComparison.Ordinal), "addTextBox succeeds", failures);

    var validation = ToJson(service.Validate(output));
    Assert(validation.GetProperty("overflowWarnings").GetArrayLength() > 0, "obvious no-autofit overflow is reported", failures);

    var corrupt = Path.Combine(root, "corrupt.pptx");
    File.Copy(output, corrupt);
    CorruptRelationshipAndShapeId(corrupt);
    var corruptValidation = ToJson(service.Validate(corrupt));
    Assert(!corruptValidation.GetProperty("valid").GetBoolean(), "broken relationship and duplicate shape id invalidate deck", failures);
    var errors = string.Join("\n", corruptValidation.GetProperty("errors").EnumerateArray().Select(item => item.GetString()));
    Assert(errors.Contains("Broken relationship", StringComparison.Ordinal), "broken relationship is localized", failures);
    Assert(errors.Contains("duplicate shape id", StringComparison.OrdinalIgnoreCase), "duplicate shape id is localized", failures);

    ExpectFailure(() => service.Create(Path.Combine(root, "bad.ppt"), specification, false), "legacy .ppt create is rejected", failures);
    ExpectFailure(() => service.Edit(source, source, "[{}]", false), "same-path edit is rejected", failures);
}
catch (Exception ex)
{
    failures.Add("Unexpected test exception: " + ex);
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

if (failures.Count == 0)
{
    Console.WriteLine("[ALL PRESENTATION TESTS PASSED]");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine("[FAIL] " + failure);
return 1;

static void AssertValid(PptxPackageService service, string path, string label, List<string> failures)
{
    var result = ToJson(service.Validate(path));
    if (result.GetProperty("valid").GetBoolean()) return;
    var errors = string.Join(" | ", result.GetProperty("errors").EnumerateArray().Select(item => item.GetString()));
    failures.Add($"{label} should validate: {errors}");
}

// PowerPoint splits visible words across arbitrary runs. Nothing Athena writes does that, so the
// cross-run replacement path is only reachable once a run is split the way a real deck would have it.
static void SplitRunAcrossTwoRuns(string path)
{
    XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
    using var archive = ZipFile.Open(path, ZipArchiveMode.Update);

    var slide2 = Load(archive, "ppt/slides/slide2.xml");
    var token = slide2.Descendants(a + "t").First(element => element.Value == "CrossRunToken");
    var run = token.Parent!;
    var first = new XElement(run);
    first.Element(a + "t")!.Value = "Cross";
    var second = new XElement(run);
    second.Element(a + "t")!.Value = "RunToken";
    run.ReplaceWith(first, second);
    Replace(archive, "ppt/slides/slide2.xml", slide2);
}

// Two schema rules the generator can break silently: Athena's own static validation does not cover
// them, so the first symptom would be a repair prompt on the user's machine.
static void AssertGeneratedPackageSchema(string path, List<string> failures)
{
    XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
    XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
    using var archive = ZipFile.OpenRead(path);

    var layoutId = (string?)Load(archive, "ppt/slideMasters/slideMaster1.xml")
        .Descendants(p + "sldLayoutId").First().Attribute("id");
    Assert(uint.TryParse(layoutId, out var parsed) && parsed >= 2_147_483_648,
        $"p:sldLayoutId must satisfy ST_SlideLayoutId minInclusive 2147483648 (was '{layoutId}')", failures);

    var order = Load(archive, "ppt/slides/slide2.xml").Descendants(a + "tcPr").First()
        .Elements().Select(element => element.Name.LocalName).ToArray();
    var lastLine = Array.FindLastIndex(order, name => name.StartsWith("ln", StringComparison.Ordinal));
    var fill = Array.FindIndex(order, name => name.EndsWith("Fill", StringComparison.Ordinal));
    Assert(lastLine >= 0 && fill > lastLine,
        "a:tcPr fill must follow every line child per CT_TableCellProperties (was " + string.Join(",", order) + ")", failures);
}

static void CorruptRelationshipAndShapeId(string path)
{
    XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
    XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
    var relationships = Load(archive, "ppt/slides/_rels/slide3.xml.rels");
    relationships.Root!.Elements(rel + "Relationship").First().SetAttributeValue("Target", "../slideLayouts/missing.xml");
    Replace(archive, "ppt/slides/_rels/slide3.xml.rels", relationships);

    var slide = Load(archive, "ppt/slides/slide3.xml");
    var ids = slide.Descendants(p + "cNvPr").Take(2).ToArray();
    ids[1].SetAttributeValue("id", (string?)ids[0].Attribute("id"));
    Replace(archive, "ppt/slides/slide3.xml", slide);
}

static XDocument Load(ZipArchive archive, string part)
{
    using var stream = archive.GetEntry(part)!.Open();
    return XDocument.Load(stream);
}

static void Replace(ZipArchive archive, string part, XDocument document)
{
    archive.GetEntry(part)?.Delete();
    var entry = archive.CreateEntry(part, CompressionLevel.Optimal);
    using var stream = entry.Open();
    document.Save(stream);
}

static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static void Assert(bool condition, string message, List<string> failures)
{
    if (!condition) failures.Add(message);
}

static void ExpectFailure(Action action, string message, List<string> failures)
{
    try { action(); failures.Add(message); }
    catch { }
}
