using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>A bounded projection of one proposed operation. Only fields with known semantics leave the gate.</summary>
internal sealed class ApprovalReviewEvidence
{
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private static readonly HashSet<string> EffectFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "filePath", "directory", "directoryPath", "workingDirectory", "sourcePath", "destinationPath",
        "inputPath", "outputPath", "targetPath", "name", "url", "startUrl", "server", "serverName", "tool", "toolName",
        "command", "task", "goal", "instruction", "title", "agent_type", "method", "mode", "format",
        "targetFormat", "operation", "type", "continuityMode"
    };
    private static readonly HashSet<string> BodyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "text", "oldText", "newText", "findText", "replaceText", "code", "script", "prompt",
        "diffContent", "body", "data", "stdin", "headers"
    };

    // Applies equally to intent, paths, argv, destinations, and the rationale returned to the caller.
    private static readonly Regex Secrets = new(
        """-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)|\b(?:Bearer|Basic)\s+[A-Za-z0-9+/.=_~-]+|\b(?:sk-[\w-]{8,}|gh[pousr]_[\w-]{12,}|github_pat_[\w-]+|xox[baprs]-[\w-]+|AKIA[A-Z0-9]{16})|(?:api[-_]?key|access[-_]?token|token|secret|passwd|password|authorization|cookie)\s*["']?\s*[=:]\s*(?:(?:Bearer|Basic)\s+[A-Za-z0-9+/.=_~-]+|"[^"]*"|'[^']*'|[^\s&,;]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    private static readonly Regex Url = new("""\b[A-Za-z][A-Za-z0-9+.-]*://[^\s"'<>]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));

    private int _remainingNodes = 100;
    private int _remainingChars = 12000;
    public bool Complete { get; private set; } = true;
    public string Payload { get; private set; } = string.Empty;

    public static ApprovalReviewEvidence From(ToolApprovalRequest request)
    {
        var evidence = new ApprovalReviewEvidence();
        var task = ToolApprovalContext.CurrentDelegatedTask;
        if (string.IsNullOrWhiteSpace(task) || task.EndsWith('…') || task.Contains("…\nPrevious user message:", StringComparison.Ordinal))
            evidence.Complete = false;
        var fields = new JsonObject();
        var resolvedPaths = new JsonObject();
        try
        {
            var source = request.ArgumentsJson ?? request.PrettyArguments;
            if (source.Length > 1024 * 1024) evidence.Complete = false;
            else
            {
                using var doc = JsonDocument.Parse(source, StrictJson);
                var args = doc.RootElement;
                if (args.ValueKind != JsonValueKind.Object) evidence.Complete = false;
                else
                {
                    string[] pathFields = request.FunctionName switch
                    {
                        "write_system_file" or "modify_system_file" or "delete_system_file" => ["path"],
                        "copy_system_file" or "move_system_file" => ["sourcePath", "destinationPath"],
                        "fetch_url_to_file" => ["outputPath"],
                        _ => []
                    };
                    var plainDocument = false;
                    foreach (var name in pathFields)
                    {
                        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                        {
                            evidence.Complete = false;
                            continue;
                        }
                        var path = value.GetString()!;
                        var target = request.ResolveFilePath != null ? request.ResolveFilePath(path) : FileSystemService.ResolveApprovalPath(path, null);
                        if (target == null) evidence.Complete = false;
                        else
                        {
                            resolvedPaths[name] = evidence.Text(target);
                            if (name == "path" && request.FunctionName is "write_system_file" or "modify_system_file")
                                plainDocument = IsPlainDocument(path) && IsPlainDocument(target);
                        }
                    }
                    fields = evidence.ProjectObject(args, request.FunctionName == "execute_terminal_command", plainDocument);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { evidence.Complete = false; }
        var effect = request.FunctionName switch
        {
            "write_system_file" => "Creates or completely replaces the target file.",
            "modify_system_file" => "Changes the contents of an existing file.",
            "delete_system_file" => "Removes the specified file or directory.",
            "copy_system_file" => "Copies the source to the destination and may replace an existing file.",
            "move_system_file" => "Moves the source to the destination, removing its old entry.",
            "execute_terminal_command" => "Runs the specified executable with these arguments and working directory.",
            "fetch_url_to_file" => "Downloads a public resource and writes it at the destination.",
            "get_document_outline" or "parse_office_document" when request.Risk == ToolRisk.Sensitive =>
                "May upload the named document to the configured parsing service.",
            "execute_program" => "Runs JavaScript with the granted read-only filesystem tools.",
            "run_browser_task" => "A browser agent may navigate, submit forms or upload files.",
            "dispatch_subagents" => "Delegates these instructions to agents with their own tools.",
            "mcp_call_tool" => "Invokes the named tool on an external MCP server.",
            "generate_image" => "Sends a generation request to the configured image service.",
            "create_new_memory" => "Creates a persistent entry in the local knowledge base.",
            "create_document" or "edit_document" or "convert_document" or "create_spreadsheet" or "edit_spreadsheet"
                or "convert_spreadsheet" or "modify_spreadsheet_structure" or "create_presentation" or "edit_presentation"
                => "Writes an Office document at the requested output path.",
            _ => null
        };
        if (effect == null) evidence.Complete = false;
        var input = new JsonObject
        {
            ["user_requests"] = evidence.Text(task),
            ["workspace_directory"] = evidence.Text(ToolApprovalContext.CurrentWorkspaceDirectory),
            ["process_directory"] = evidence.Text(Environment.CurrentDirectory),
            ["execution_mode"] = ToolApprovalContext.CurrentMode.ToString(),
            ["agent_instruction"] = evidence.Text(ToolApprovalContext.CurrentAgentInstruction),
            ["tool"] = evidence.Text(request.FunctionName),
            ["effect"] = effect,
            ["local_risk"] = request.Risk.ToString(),
            ["local_risk_reason"] = evidence.Text(request.RiskReason),
            ["arguments"] = fields,
            ["resolved_paths"] = resolvedPaths,
            ["evidence_complete"] = evidence.Complete
        };
        evidence.Payload = input.ToJsonString();
        return evidence;
    }

    private JsonObject ProjectObject(JsonElement obj, bool terminal, bool plainDocument)
    {
        var result = new JsonObject();
        foreach (var property in obj.EnumerateObject())
        {
            if (--_remainingNodes < 0) { Complete = false; break; }
            var name = property.Name;
            var value = property.Value;
            if (BodyFields.Contains(name))
            {
                // An inert local document's body is unnecessary to identify its destination and overwrite effect.
                if (!(plainDocument && name is "content" or "oldText" or "newText" or "findText" or "replaceText" or "diffContent"))
                    Complete = false;
                result[name] = "[body omitted]";
            }
            else if (terminal && name == "arguments" && value.ValueKind == JsonValueKind.Array)
            {
                var command = obj.TryGetProperty("command", out var executable) && executable.ValueKind == JsonValueKind.String
                    ? Path.GetFileNameWithoutExtension(executable.GetString())?.ToLowerInvariant() : null;
                if (command is "echo" or "printf" or "write-output")
                {
                    result[name] = "[arguments contain text omitted from review]";
                    Complete = false;
                }
                else result[name] = ProjectArguments(value);
            }
            else if (terminal && name == "command" && value.ValueKind == JsonValueKind.String
                && !IsExecutableName(value.GetString()!))
            {
                result[name] = "[compound command omitted]";
                Complete = false;
            }
            else if (EffectFields.Contains(name) && value.ValueKind == JsonValueKind.String)
                result[name] = Text(value.GetString());
            else if (name == "tasks" && value.ValueKind == JsonValueKind.Array)
            {
                var tasks = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (--_remainingNodes < 0 || item.ValueKind != JsonValueKind.Object) { Complete = false; break; }
                    tasks.Add(ProjectObject(item, false, false));
                }
                result[name] = tasks;
            }
            else if (name == "arguments" && value.ValueKind == JsonValueKind.Object)
                result[name] = ProjectObject(value, false, false);
            else if (name == "arguments" && value.ValueKind == JsonValueKind.String)
            {
                // The MCP dispatcher accepts both object arguments and a JSON-encoded object.
                using var nested = JsonDocument.Parse(value.GetString()!, StrictJson);
                if (nested.RootElement.ValueKind == JsonValueKind.Object)
                    result[name] = ProjectObject(nested.RootElement, false, false);
                else Complete = false;
            }
            else if (name is "overwrite" or "recursive" or "waitForExit" or "timeoutSeconds" or "createDirectories" or "fuzzyMatch" or "replaceAll" or "maxSteps" or "somMaxElements"
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
                result[name] = JsonNode.Parse(value.GetRawText());
            else Complete = false; // Unknown fields may change the operation, including at an MCP server.
        }
        return result;
    }

    internal static bool IsPlainDocument(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        return Path.GetExtension(segments[^1]).ToLowerInvariant() is ".txt" or ".md"
            && !segments.Any(segment => segment.Length > 0 && segment[0] == '.' && segment != ".")
            && segments[^1].ToLowerInvariant() is not ("agents.md" or "claude.md" or "skill.md" or "gemini.md" or "copilot-instructions.md");
    }

    private static bool IsExecutableName(string value) =>
        value.Length > 0 && !value.Any(char.IsControl) &&
        value.IndexOfAny([';', '|', '&', '`', '$', '<', '>']) < 0 && (!value.Contains(' ') || Path.IsPathFullyQualified(value));

    private JsonArray ProjectArguments(JsonElement args)
    {
        var result = new JsonArray();
        bool omitNext = false;
        foreach (var item in args.EnumerateArray())
        {
            if (--_remainingNodes < 0 || item.ValueKind != JsonValueKind.String) { Complete = false; break; }
            var arg = item.GetString()!;
            if (omitNext || arg.Contains('\n'))
            {
                Complete = false;
                result.Add("[argument omitted]");
                omitNext = false;
                continue;
            }
            var equals = arg.IndexOf('=');
            var flag = (equals < 0 ? arg : arg[..equals]).ToLowerInvariant();
            var takesPayload = flag is "-c" or "-e" or "-command" or "-encodedcommand" or "--command" or "--eval"
                or "--data" or "--data-raw" or "--data-binary" or "-d" or "-h" or "--header" or "-u" or "--user"
                or "-m" or "--message" or "/c" or "--password" or "--token" or "--access-token" or "--api-key"
                or "--client-secret" or "--secret" or "--authorization" or "--cookie";
            if (takesPayload && equals >= 0 || arg.Length > 2 && arg[0] == '-' && "ceHu".Contains(arg[1]))
            {
                result.Add("[argument omitted]");
                Complete = false;
            }
            else
            {
                result.Add(Text(arg));
                omitNext = takesPayload;
            }
        }
        return result;
    }

    private string? Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length > 4000 || value.Length > _remainingChars)
        {
            Complete = false;
            return "[text omitted]";
        }
        _remainingChars -= value.Length;
        var safe = Scrub(value);
        if (!string.Equals(safe, value, StringComparison.Ordinal)) Complete = false;
        return safe;
    }

    internal static string Scrub(string value)
    {
        try
        {
            // Strip query, fragment and user-info even when a URL occurs inside a command or a user message.
            var urls = Url.Replace(value, match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[URL omitted]";
                if (uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0) return match.Value;
                return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
            });
            return Secrets.Replace(urls, "[REDACTED]");
        }
        catch (RegexMatchTimeoutException) { return "[text omitted]"; }
        catch (UriFormatException) { return "[URL omitted]"; }
    }
}
