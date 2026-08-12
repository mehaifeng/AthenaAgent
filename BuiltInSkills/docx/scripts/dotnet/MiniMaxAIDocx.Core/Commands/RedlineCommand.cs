using System.CommandLine;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MiniMaxAIDocx.Core.Commands;

/// <summary>
/// Pipeline D: deterministic, minimal tracked-text replacements that preserve
/// unchanged run markup (including formatting and RSIDs).
/// </summary>
public static class RedlineCommand
{
    public static Command Create()
    {
        var command = new Command("redline", "Create and verify precise tracked changes");
        command.Add(CreateReplaceCommand());
        command.Add(CreateApplyPlanCommand());
        command.Add(CreateVerifyCommand());
        return command;
    }

    private static Command CreateReplaceCommand()
    {
        var input = RequiredString("--input", "Original DOCX file");
        var output = RequiredString("--output", "New redlined DOCX file");
        var search = RequiredString("--search", "Exact text to replace");
        var replace = RequiredString("--replace", "Replacement text");
        var author = RequiredString("--author", "Revision author");
        var expectedCount = new Option<int>("--expected-count")
        {
            Description = "Required number of exact matches; protects against ambiguous edits"
        };
        expectedCount.DefaultValueFactory = _ => 1;

        var command = new Command("replace", "Replace exact text with minimal w:del/w:ins markup")
        {
            input, output, search, replace, author, expectedCount
        };

        command.SetAction(parseResult =>
        {
            var change = new RedlinePlanItem(
                parseResult.GetValue(search)!,
                parseResult.GetValue(replace)!,
                parseResult.GetValue(expectedCount));

            ApplyPlan(
                parseResult.GetValue(input)!,
                parseResult.GetValue(output)!,
                parseResult.GetValue(author)!,
                [change]);
        });

        return command;
    }

    private static Command CreateApplyPlanCommand()
    {
        var input = RequiredString("--input", "Original DOCX file");
        var output = RequiredString("--output", "New redlined DOCX file");
        var plan = RequiredString("--plan", "JSON array of search/replace/expectedCount objects");
        var author = RequiredString("--author", "Revision author");

        var command = new Command("apply-plan", "Apply a checked batch of exact tracked replacements")
        {
            input, output, plan, author
        };

        command.SetAction(parseResult =>
        {
            var planPath = parseResult.GetValue(plan)!;
            if (!File.Exists(planPath))
            {
                Fail($"Plan file not found: {planPath}");
                return;
            }

            List<RedlinePlanItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<RedlinePlanItem>>(
                    File.ReadAllText(planPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                Fail($"Invalid redline plan JSON: {ex.Message}");
                return;
            }

            if (items == null || items.Count == 0)
            {
                Fail("Redline plan must contain at least one change.");
                return;
            }

            ApplyPlan(
                parseResult.GetValue(input)!,
                parseResult.GetValue(output)!,
                parseResult.GetValue(author)!,
                items);
        });

        return command;
    }

    private static Command CreateVerifyCommand()
    {
        var input = RequiredString("--input", "Redlined DOCX file");
        var author = new Option<string>("--author") { Description = "Require this revision author" };
        var json = new Option<bool>("--json") { Description = "Output JSON" };
        var requireChanges = new Option<bool>("--require-changes") { Description = "Fail if no insertions or deletions exist" };

        var command = new Command("verify", "Validate tracked-change wrappers and revision IDs")
        {
            input, author, json, requireChanges
        };

        command.SetAction(parseResult =>
        {
            var inputPath = parseResult.GetValue(input)!;
            if (!File.Exists(inputPath))
            {
                Fail($"File not found: {inputPath}");
                return;
            }

            using var document = WordprocessingDocument.Open(inputPath, false);
            var root = document.MainDocumentPart?.Document;
            if (root == null)
            {
                Fail("DOCX has no main document part.");
                return;
            }

            var insertions = root.Descendants<InsertedRun>().ToList();
            var deletions = root.Descendants<DeletedRun>().ToList();
            var errors = new List<string>();
            var requiredAuthor = parseResult.GetValue(author);

            if (deletions.Any(item => item.Descendants<Text>().Any()))
                errors.Add("A deletion contains w:t; deletion text must use w:delText.");
            if (insertions.Any(item => item.Descendants<DeletedText>().Any()))
                errors.Add("An insertion contains w:delText; insertion text must use w:t.");

            var revisionIds = insertions.Select(item => item.Id?.Value)
                .Concat(deletions.Select(item => item.Id?.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (revisionIds.Count != revisionIds.Distinct(StringComparer.Ordinal).Count())
                errors.Add("Duplicate insertion/deletion revision IDs were found.");

            if (!string.IsNullOrWhiteSpace(requiredAuthor))
            {
                var wrongAuthors = insertions.Cast<OpenXmlElement>().Concat(deletions)
                    .Select(item => item is InsertedRun inserted ? inserted.Author?.Value : ((DeletedRun)item).Author?.Value)
                    .Where(value => !string.Equals(value, requiredAuthor, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (wrongAuthors.Count > 0)
                    errors.Add($"Unexpected revision author(s): {string.Join(", ", wrongAuthors)}");
            }

            if (parseResult.GetValue(requireChanges) && insertions.Count + deletions.Count == 0)
                errors.Add("No tracked insertions or deletions were found.");

            var result = new
            {
                valid = errors.Count == 0,
                insertionCount = insertions.Count,
                deletionCount = deletions.Count,
                errors
            };

            if (parseResult.GetValue(json))
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                Console.WriteLine($"Insertions: {insertions.Count}");
                Console.WriteLine($"Deletions:  {deletions.Count}");
                foreach (var error in errors)
                    Console.Error.WriteLine($"ERROR: {error}");
                Console.WriteLine(errors.Count == 0 ? "Redline verification: PASSED" : "Redline verification: FAILED");
            }

            if (errors.Count > 0)
                Environment.ExitCode = 1;
        });

        return command;
    }

    private static void ApplyPlan(string inputPath, string outputPath, string author, IReadOnlyList<RedlinePlanItem> plan)
    {
        if (!File.Exists(inputPath))
        {
            Fail($"Input file not found: {inputPath}");
            return;
        }
        if (string.IsNullOrWhiteSpace(author))
        {
            Fail("Revision author cannot be empty.");
            return;
        }

        var resolvedInput = Path.GetFullPath(inputPath);
        var resolvedOutput = Path.GetFullPath(outputPath);
        if (string.Equals(resolvedInput, resolvedOutput, StringComparison.OrdinalIgnoreCase))
        {
            Fail("Redlining requires a distinct output path so the original remains untouched.");
            return;
        }

        foreach (var item in plan)
        {
            if (string.IsNullOrEmpty(item.Search))
            {
                Fail("Every plan item requires non-empty search text.");
                return;
            }
            if (item.ExpectedCount < 1)
            {
                Fail("expectedCount must be at least 1 for every plan item.");
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput) ?? Directory.GetCurrentDirectory());
        var temporaryPath = resolvedOutput + $".redline-{Guid.NewGuid():N}.tmp";
        File.Copy(resolvedInput, temporaryPath, true);

        try
        {
            using (var document = WordprocessingDocument.Open(temporaryPath, true))
            {
                var mainPart = document.MainDocumentPart
                    ?? throw new InvalidOperationException("DOCX has no main document part.");
                var mainDocument = mainPart.Document
                    ?? throw new InvalidOperationException("DOCX has no main document root.");
                var body = mainDocument.Body
                    ?? throw new InvalidOperationException("DOCX has no main document body.");
                var nextRevisionId = GetNextRevisionId(document);
                var timestamp = DateTime.UtcNow;
                var total = 0;

                foreach (var item in plan)
                {
                    var matches = FindMatches(body, item.Search);
                    if (matches.Count != item.ExpectedCount)
                    {
                        throw new InvalidOperationException(
                            $"Expected {item.ExpectedCount} exact match(es) for '{item.Search}', found {matches.Count}. No output was written.");
                    }

                    foreach (var paragraphGroup in matches.GroupBy(match => match.Group).ToList())
                    {
                        nextRevisionId = RewriteGroup(
                            paragraphGroup.Key,
                            paragraphGroup.OrderBy(match => match.Start).ToList(),
                            item.Replace,
                            author,
                            timestamp,
                            nextRevisionId);
                    }

                    total += matches.Count;
                }

                mainDocument.Save();
                Console.WriteLine($"Created {total} precise tracked replacement(s).");
            }

            File.Move(temporaryPath, resolvedOutput, true);
            Console.WriteLine($"Redlined document: {resolvedOutput}");
        }
        catch (Exception ex)
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            Fail(ex.Message);
        }
    }

    private static List<MatchLocation> FindMatches(Body body, string search)
    {
        var result = new List<MatchLocation>();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            foreach (var group in GetPlainRunGroups(paragraph))
            {
                var text = string.Concat(group.Select(slice => slice.Text));
                var index = 0;
                while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
                {
                    result.Add(new MatchLocation(group, index, search.Length));
                    index += search.Length;
                }
            }
        }
        return result;
    }

    private static List<List<RunSlice>> GetPlainRunGroups(Paragraph paragraph)
    {
        var groups = new List<List<RunSlice>>();
        List<RunSlice>? current = null;
        var offset = 0;

        foreach (var child in paragraph.ChildElements)
        {
            if (child is Run run && IsPlainTextRun(run))
            {
                current ??= [];
                var text = string.Concat(run.Elements<Text>().Select(item => item.Text));
                current.Add(new RunSlice(run, text, offset, offset + text.Length));
                offset += text.Length;
                continue;
            }

            if (current is { Count: > 0 })
                groups.Add(current);
            current = null;
            offset = 0;
        }

        if (current is { Count: > 0 })
            groups.Add(current);
        return groups;
    }

    private static bool IsPlainTextRun(Run run) =>
        run.Elements<Text>().Any()
        && run.ChildElements.All(child => child is RunProperties or Text);

    private static int RewriteGroup(
        IReadOnlyList<RunSlice> group,
        IReadOnlyList<MatchLocation> matches,
        string replacement,
        string author,
        DateTime timestamp,
        int nextRevisionId)
    {
        var firstRun = group[0].Run;
        var nodes = new List<OpenXmlElement>();
        var cursor = 0;
        var totalLength = group[^1].End;

        foreach (var match in matches)
        {
            nodes.AddRange(CloneRange(group, cursor, match.Start, deleted: false));

            var deletedRuns = CloneRange(group, match.Start, match.Start + match.Length, deleted: true);
            var deletion = new DeletedRun
            {
                Id = (nextRevisionId++).ToString(),
                Author = author,
                Date = timestamp
            };
            deletion.Append(deletedRuns);
            nodes.Add(deletion);

            if (!string.IsNullOrEmpty(replacement))
            {
                var sourceRun = group.First(slice => slice.End > match.Start).Run;
                var insertedRun = new Run();
                if (sourceRun.RunProperties != null)
                    insertedRun.Append(sourceRun.RunProperties.CloneNode(true));
                insertedRun.Append(CreateText(replacement, deleted: false));

                var insertion = new InsertedRun
                {
                    Id = (nextRevisionId++).ToString(),
                    Author = author,
                    Date = timestamp
                };
                insertion.Append(insertedRun);
                nodes.Add(insertion);
            }

            cursor = match.Start + match.Length;
        }

        nodes.AddRange(CloneRange(group, cursor, totalLength, deleted: false));
        foreach (var node in nodes)
            firstRun.InsertBeforeSelf(node);
        foreach (var slice in group)
            slice.Run.Remove();
        return nextRevisionId;
    }

    private static IEnumerable<OpenXmlElement> CloneRange(
        IReadOnlyList<RunSlice> group,
        int start,
        int end,
        bool deleted)
    {
        if (start >= end)
            yield break;

        foreach (var slice in group)
        {
            var overlapStart = Math.Max(start, slice.Start);
            var overlapEnd = Math.Min(end, slice.End);
            if (overlapStart >= overlapEnd)
                continue;

            if (!deleted && overlapStart == slice.Start && overlapEnd == slice.End)
            {
                yield return slice.Run.CloneNode(true);
                continue;
            }

            var clone = (Run)slice.Run.CloneNode(true);
            foreach (var child in clone.ChildElements.Where(child => child is not RunProperties).ToList())
                child.Remove();

            var fragment = slice.Text.Substring(overlapStart - slice.Start, overlapEnd - overlapStart);
            clone.Append(CreateText(fragment, deleted));
            yield return clone;
        }
    }

    private static OpenXmlElement CreateText(string value, bool deleted)
    {
        if (deleted)
        {
            return new DeletedText(value)
            {
                Space = NeedsPreservedWhitespace(value) ? SpaceProcessingModeValues.Preserve : null
            };
        }

        return new Text(value)
        {
            Space = NeedsPreservedWhitespace(value) ? SpaceProcessingModeValues.Preserve : null
        };
    }

    private static bool NeedsPreservedWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static int GetNextRevisionId(WordprocessingDocument document)
    {
        var root = document.MainDocumentPart?.Document;
        if (root == null)
            return 1;

        var ids = root.Descendants<InsertedRun>().Select(item => item.Id?.Value)
            .Concat(root.Descendants<DeletedRun>().Select(item => item.Id?.Value));
        var max = 0;
        foreach (var value in ids)
        {
            if (int.TryParse(value, out var parsed) && parsed > max)
                max = parsed;
        }
        return max + 1;
    }

    private static Option<string> RequiredString(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Environment.ExitCode = 1;
    }

    private sealed record RunSlice(Run Run, string Text, int Start, int End);
    private sealed record MatchLocation(IReadOnlyList<RunSlice> Group, int Start, int Length);
    private sealed record RedlinePlanItem(string Search, string Replace, int ExpectedCount = 1);
}
