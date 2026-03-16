using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ICliService
{
    Task<CliResult> ExecuteAsync(string command, IEnumerable<string> arguments, string? workingDirectory = null, IDictionary<string, string>? environmentVariables = null, bool waitForExit = true, CancellationToken ct = default);
}

public class CliResult
{
    public int ExitCode { get; set; }
    public int? ProcessId { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public TimeSpan RunTime { get; set; }
    public bool IsSuccess => ExitCode == 0;
}
