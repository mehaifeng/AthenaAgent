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
        "targetFormat", "operation", "type", "continuityMode", "taskId", "cronExpression", "workspaceId"
    };
    private static readonly HashSet<string> BodyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "text", "oldText", "newText", "findText", "replaceText", "code", "script", "prompt",
        "diffContent", "body", "data", "stdin", "headers", "inputs", "env", "json"
    };

    // Applies equally to intent, paths, argv, destinations, and the rationale returned to the caller.
    private static readonly Regex Secrets = new(
        """-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)|\b(?:Bearer|Basic)\s+[A-Za-z0-9+/.=_~-]+|\b(?:sk-[\w-]{8,}|gh[pousr]_[\w-]{12,}|github_pat_[\w-]+|xox[baprs]-[\w-]+|AKIA[A-Z0-9]{16})|(?:api[-_]?key|access[-_]?token|token|secret|passwd|password|authorization|cookie)\s*["']?\s*[=:]\s*(?:(?:Bearer|Basic)\s+[A-Za-z0-9+/.=_~-]+|"[^"]*"|'[^']*'|[^\s&,;]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    private static readonly Regex Url = new("""\b[A-Za-z][A-Za-z0-9+.-]*://[^\s"'<>]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(150));

    private int _remainingNodes = 100;
    private int _remainingChars = 12000;
    public static string CreatePayload(ToolApprovalRequest request)
    {
        var evidence = new ApprovalReviewEvidence();
        var effect = request.FunctionName switch
        {
            "write_system_file" => "Creates or completely replaces the target file.",
            "modify_system_file" => "Changes the contents of an existing file.",
            "delete_system_file" => "Removes the specified file or directory.",
            "copy_system_file" => "Copies the source to the destination and may replace an existing file.",
            "move_system_file" => "Moves the source to the destination, removing its old entry.",
            "execute_terminal_command" => "Runs the specified executable with these arguments and working directory.",
            "fetch_url_to_file" => "Downloads a public resource and writes it at the destination.",
            "execute_program" => "Starts a bounded in-process JavaScript interpreter with a grant for read-only filesystem tools. Each child call is reviewed before execution.",
            "run_computer_task" => "A computer-use agent can see the current desktop and send foreground keyboard and pointer input across applications, or navigate and interact with HTTP(S) pages in an isolated browser session.",
            "dispatch_subagents" => "Delegates these instructions to agents with their own tools.",
            "mcp_call_tool" => "Invokes the named tool on an external MCP server.",
            "generate_image" => "Sends a generation request to the configured image service.",
            "create_new_memory" => "Creates a persistent entry in the local knowledge base.",
            "create_document" or "edit_document" or "convert_document" or "create_spreadsheet" or "edit_spreadsheet"
                or "convert_spreadsheet" or "modify_spreadsheet_structure" or "create_presentation" or "edit_presentation"
                => "Writes an Office document at the requested output path.",
            _ => request.ToolDescription
        };
        var input = new JsonObject
        {
            ["user_requests"] = evidence.Text(ToolApprovalContext.CurrentDelegatedTask),
            ["workspace_directory"] = evidence.Text(ToolApprovalContext.CurrentWorkspaceDirectory),
            ["process_directory"] = evidence.Text(Environment.CurrentDirectory),
            ["execution_mode"] = ToolApprovalContext.CurrentMode.ToString(),
            ["agent_instruction"] = evidence.Text(ToolApprovalContext.CurrentAgentInstruction),
            ["tool"] = evidence.Text(request.FunctionName),
            ["effect"] = evidence.Text(effect)
        };
        var resolvedPaths = new JsonObject();
        input["arguments"] = evidence.ProjectArguments(request, resolvedPaths);
        input["resolved_paths"] = resolvedPaths;
        return input.ToJsonString();
    }

    private JsonObject? ProjectArguments(ToolApprovalRequest request, JsonObject resolvedPaths)
    {
        var source = request.ArgumentsJson ?? request.PrettyArguments;
        if (source.Length > 1024 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(source, StrictJson);
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return null;
            string[] pathFields = request.FunctionName switch
            {
                "read_system_file" or "get_file_info" or "search_in_file" or "search_in_directory" or "list_system_directory"
                    or "write_system_file" or "modify_system_file" or "delete_system_file" or "create_directory" or "get_document_outline" => ["path"],
                "copy_system_file" or "move_system_file" => ["sourcePath", "destinationPath"],
                "fetch_url_to_file" => ["outputPath"],
                _ => []
            };
            foreach (var name in pathFields)
            {
                if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
                var path = value.GetString()!;
                var target = request.ResolveFilePath != null
                    ? request.ResolveFilePath(path)
                    : FileSystemService.ResolveApprovalPath(path, null);
                if (target != null) resolvedPaths[name] = Text(target);
            }
            var projected = ProjectObject(args, request.FunctionName == "execute_terminal_command");
            if (request.FunctionName == "modify_self_configuration" && args.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
            {
                projected["key"] = Text(key.GetString());
                if (string.Equals(key.GetString(), "Security.ToolApprovalMode", StringComparison.OrdinalIgnoreCase)
                    && args.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    && Enum.TryParse<ToolApprovalMode>(value.GetString(), true, out var mode) && Enum.IsDefined(mode))
                    projected["value"] = mode.ToString();
            }
            return projected;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private JsonObject ProjectObject(JsonElement obj, bool terminal)
    {
        var result = new JsonObject();
        foreach (var property in obj.EnumerateObject())
        {
            if (--_remainingNodes < 0) break;
            var name = property.Name;
            var value = property.Value;
            if (BodyFields.Contains(name))
                result[name] = "[body omitted]";
            else if (terminal && name == "arguments" && value.ValueKind == JsonValueKind.Array)
            {
                var command = obj.TryGetProperty("command", out var executable) && executable.ValueKind == JsonValueKind.String
                    ? Path.GetFileNameWithoutExtension(executable.GetString())?.ToLowerInvariant() : null;
                if (command is "echo" or "printf" or "write-output")
                    result[name] = "[arguments contain text omitted from review]";
                else result[name] = ProjectCommandArguments(value);
            }
            else if (terminal && name == "command" && value.ValueKind == JsonValueKind.String
                && !IsExecutableName(value.GetString()!))
                result[name] = "[compound command omitted]";
            else if (EffectFields.Contains(name) && value.ValueKind == JsonValueKind.String)
                result[name] = Text(value.GetString());
            else if (name == "tasks" && value.ValueKind == JsonValueKind.Array)
            {
                var tasks = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (--_remainingNodes < 0 || item.ValueKind != JsonValueKind.Object) break;
                    tasks.Add(ProjectObject(item, false));
                }
                result[name] = tasks;
            }
            else if (name == "arguments" && value.ValueKind == JsonValueKind.Object)
                result[name] = ProjectObject(value, false);
            else if (name == "arguments" && value.ValueKind == JsonValueKind.String)
            {
                // The MCP dispatcher accepts both object arguments and a JSON-encoded object.
                using var nested = JsonDocument.Parse(value.GetString()!, StrictJson);
                if (nested.RootElement.ValueKind == JsonValueKind.Object)
                    result[name] = ProjectObject(nested.RootElement, false);
            }
            else if (name is "overwrite" or "recursive" or "waitForExit" or "timeoutSeconds" or "createDirectories" or "fuzzyMatch"
                or "replaceAll" or "maxSteps" or "somMaxElements" or "includeTextStatistics"
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
                result[name] = JsonNode.Parse(value.GetRawText());
        }
        return result;
    }

    private static bool IsExecutableName(string value) =>
        value.Length > 0 && !value.Any(char.IsControl) &&
        value.IndexOfAny([';', '|', '&', '`', '$', '<', '>']) < 0 && (!value.Contains(' ') || Path.IsPathFullyQualified(value));

    private JsonArray ProjectCommandArguments(JsonElement args)
    {
        var result = new JsonArray();
        bool omitNext = false;
        foreach (var item in args.EnumerateArray())
        {
            if (--_remainingNodes < 0 || item.ValueKind != JsonValueKind.String) break;
            var arg = item.GetString()!;
            if (omitNext || arg.Contains('\n'))
            {
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
            return "[text omitted]";
        _remainingChars -= value.Length;
        return Scrub(value);
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
