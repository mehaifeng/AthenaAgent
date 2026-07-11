using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ICliService
{
    /// <param name="timeoutSeconds">
    /// waitForExit=true 时的最长等待秒数。未指定时使用默认值；超过该时间进程会被强制终止，
    /// 返回结果中以 ExitCode=-2 标识超时（而非用户主动取消）。
    /// </param>
    Task<CliResult> ExecuteAsync(string command, IEnumerable<string> arguments, string? workingDirectory = null, IDictionary<string, string>? environmentVariables = null, bool waitForExit = true, int? timeoutSeconds = null, CancellationToken ct = default);
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
