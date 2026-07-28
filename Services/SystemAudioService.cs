using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// Cross-platform playback for generated audio attachments.
/// Speech synthesis belongs to the selected TTS provider and is intentionally
/// not implemented here.
/// </summary>
public sealed class SystemAudioService : ISystemAudioService
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public SystemAudioService(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger.ForContext<SystemAudioService>();
    }

    public async Task<SystemAudioResult> PlayFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CliResult result;
            if (OperatingSystem.IsMacOS())
            {
                result = await _cliService.ExecuteAsync("afplay", [filePath], ct: cancellationToken);
            }
            else if (OperatingSystem.IsLinux())
            {
                return await PlayFileLinuxAsync(filePath, cancellationToken);
            }
            else if (OperatingSystem.IsWindows())
            {
                result = await _cliService.ExecuteAsync(
                    "powershell",
                    BuildWindowsPlayArguments(filePath),
                    ct: cancellationToken);
            }
            else
            {
                return new SystemAudioResult
                {
                    Success = false,
                    Message = "Audio playback is not supported on this operating system."
                };
            }

            return result.IsSuccess
                ? new SystemAudioResult { Success = true }
                : new SystemAudioResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError
                };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Audio playback failed");
            return new SystemAudioResult { Success = false, Message = ex.Message };
        }
    }

    private async Task<SystemAudioResult> PlayFileLinuxAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var isWav = filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        (string Command, string[] Args)[] candidates = isWav
            ?
            [
                ("aplay", [filePath]),
                ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "error", filePath])
            ]
            :
            [
                ("mpg123", ["-q", filePath]),
                ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "error", filePath]),
                ("mplayer", ["-really-quiet", filePath])
            ];

        var lastError = "No suitable audio player found (tried: "
            + string.Join(", ", candidates.Select(candidate => candidate.Command))
            + ").";
        foreach (var (command, arguments) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _cliService.ExecuteAsync(command, arguments, ct: cancellationToken);
                if (result.IsSuccess) return new SystemAudioResult { Success = true };
                lastError = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }
        return new SystemAudioResult { Success = false, Message = lastError };
    }

    private static string[] BuildWindowsPlayArguments(string filePath)
    {
        var escapedPath = filePath.Replace("'", "''", StringComparison.Ordinal);
        if (filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wavScript = "$ErrorActionPreference='Stop';"
                + $"$player = New-Object Media.SoundPlayer '{escapedPath}';"
                + "$player.PlaySync();";
            return ["-NoProfile", "-Command", wavScript];
        }

        var script = "$ErrorActionPreference='Stop';"
            + "Add-Type -AssemblyName PresentationCore;"
            + "$p = New-Object System.Windows.Media.MediaPlayer;"
            + $"$p.Open([Uri]::new('{escapedPath}'));"
            + "$t = 0; while (-not $p.NaturalDuration.HasTimeSpan -and $t -lt 100) { Start-Sleep -Milliseconds 100; $t++ };"
            + "if (-not $p.NaturalDuration.HasTimeSpan) { throw 'Cannot open media file.' };"
            + "$p.Play();"
            + "Start-Sleep -Milliseconds ([int]$p.NaturalDuration.TimeSpan.TotalMilliseconds + 200);"
            + "$p.Stop(); $p.Close();";
        return ["-NoProfile", "-STA", "-Command", script];
    }
}
