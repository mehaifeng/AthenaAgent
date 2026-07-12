using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public class SystemAudioService : ISystemAudioService
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public SystemAudioService(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger.ForContext<SystemAudioService>();
    }

    public async Task<SystemAudioResult> SynthesizeToFileAsync(string text, string voice, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            CliResult result;

            if (OperatingSystem.IsMacOS())
            {
                result = await _cliService.ExecuteAsync("say", BuildMacOsSayArguments(outputPath, voice, text), ct: cancellationToken);
            }
            else if (OperatingSystem.IsLinux())
            {
                result = await _cliService.ExecuteAsync("espeak-ng", BuildLinuxEspeakArguments(outputPath, voice, text), ct: cancellationToken);
            }
            else if (OperatingSystem.IsWindows())
            {
                result = await _cliService.ExecuteAsync("powershell", BuildWindowsSpeechArguments(outputPath, voice, text), ct: cancellationToken);
            }
            else
            {
                return new SystemAudioResult { Success = false, Message = "System audio output is not supported on this operating system." };
            }

            return result.IsSuccess
                ? new SystemAudioResult { Success = true, Message = string.Empty }
                : new SystemAudioResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError
                };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "System TTS synthesis failed");
            return new SystemAudioResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<SystemAudioResult> PlayFileAsync(string filePath, CancellationToken cancellationToken = default)
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
                result = await _cliService.ExecuteAsync("powershell", BuildWindowsPlayArguments(filePath), ct: cancellationToken);
            }
            else
            {
                return new SystemAudioResult { Success = false, Message = "System audio playback is not supported on this operating system." };
            }

            return result.IsSuccess
                ? new SystemAudioResult { Success = true, Message = string.Empty }
                : new SystemAudioResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError
                };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "System audio playback failed");
            return new SystemAudioResult { Success = false, Message = ex.Message };
        }
    }

    // Linux 没有百分百预装且通吃 mp3/wav 的播放命令：wav 优先 aplay，
    // 其余格式依次尝试常见播放器，直到某个命令既存在又成功。
    private async Task<SystemAudioResult> PlayFileLinuxAsync(string filePath, CancellationToken cancellationToken)
    {
        var isWav = filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        (string Command, string[] Args)[] candidates = isWav
            ?
            [
                ("aplay", new[] { filePath }),
                ("ffplay", new[] { "-nodisp", "-autoexit", "-loglevel", "error", filePath })
            ]
            :
            [
                ("mpg123", new[] { "-q", filePath }),
                ("ffplay", new[] { "-nodisp", "-autoexit", "-loglevel", "error", filePath }),
                ("mplayer", new[] { "-really-quiet", filePath })
            ];

        string lastError = "No suitable audio player found (tried: " + string.Join(", ", candidates.Select(c => c.Command)) + ").";
        foreach (var (command, args) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _cliService.ExecuteAsync(command, args, ct: cancellationToken);
                if (result.IsSuccess)
                {
                    return new SystemAudioResult { Success = true, Message = string.Empty };
                }

                lastError = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 命令不存在等启动失败——换下一个候选。
                lastError = ex.Message;
            }
        }

        return new SystemAudioResult { Success = false, Message = lastError };
    }

    private static IEnumerable<string> BuildMacOsSayArguments(string outputPath, string voice, string text)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return ["-o", outputPath, text];
        }

        return ["-v", voice, "-o", outputPath, text];
    }

    private static IEnumerable<string> BuildLinuxEspeakArguments(string outputPath, string voice, string text)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return ["-w", outputPath, text];
        }

        return ["-v", voice, "-w", outputPath, text];
    }

    private static IEnumerable<string> BuildWindowsSpeechArguments(string outputPath, string voice, string text)
    {
        var escapedPath = EscapePowerShellString(outputPath);
        var escapedText = EscapePowerShellString(text);
        var escapedVoice = EscapePowerShellString(voice);
        var script = "$ErrorActionPreference='Stop';" +
                     "Add-Type -AssemblyName System.Speech;" +
                     "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                     (string.IsNullOrWhiteSpace(voice) ? string.Empty : $"$s.SelectVoice('{escapedVoice}');") +
                     $"$s.SetOutputToWaveFile('{escapedPath}');" +
                     $"$s.Speak('{escapedText}');" +
                     "$s.Dispose();";

        return ["-NoProfile", "-Command", script];
    }

    private static IEnumerable<string> BuildWindowsPlayArguments(string filePath)
    {
        var escapedPath = EscapePowerShellString(filePath);

        // SoundPlayer 只认 WAV；mp3 等其他格式用系统自带的 WPF MediaPlayer（PresentationCore）。
        if (filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wavScript = "$ErrorActionPreference='Stop';" +
                            $"$player = New-Object Media.SoundPlayer '{escapedPath}';" +
                            "$player.PlaySync();";
            return ["-NoProfile", "-Command", wavScript];
        }

        var script = "$ErrorActionPreference='Stop';" +
                     "Add-Type -AssemblyName PresentationCore;" +
                     "$p = New-Object System.Windows.Media.MediaPlayer;" +
                     $"$p.Open([Uri]::new('{escapedPath}'));" +
                     "$t = 0; while (-not $p.NaturalDuration.HasTimeSpan -and $t -lt 100) { Start-Sleep -Milliseconds 100; $t++ };" +
                     "if (-not $p.NaturalDuration.HasTimeSpan) { throw 'Cannot open media file.' };" +
                     "$p.Play();" +
                     "Start-Sleep -Milliseconds ([int]$p.NaturalDuration.TimeSpan.TotalMilliseconds + 200);" +
                     "$p.Stop(); $p.Close();";
        return ["-NoProfile", "-STA", "-Command", script];
    }

    private static string EscapePowerShellString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<string>> GetInstalledVoicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CliResult result;
            IEnumerable<string> voices;

            if (OperatingSystem.IsWindows())
            {
                const string script = "Add-Type -AssemblyName System.Speech;" +
                                      "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                                      "$s.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name };" +
                                      "$s.Dispose();";
                result = await _cliService.ExecuteAsync("powershell", ["-NoProfile", "-Command", script], ct: cancellationToken);
                voices = ParseLines(result.StandardOutput);
            }
            else if (OperatingSystem.IsMacOS())
            {
                result = await _cliService.ExecuteAsync("say", ["-v", "?"], ct: cancellationToken);
                voices = ParseMacSayVoices(result.StandardOutput);
            }
            else if (OperatingSystem.IsLinux())
            {
                result = await _cliService.ExecuteAsync("espeak-ng", ["--voices"], ct: cancellationToken);
                voices = ParseEspeakVoices(result.StandardOutput);
            }
            else
            {
                return Array.Empty<string>();
            }

            if (!result.IsSuccess)
            {
                _logger.Warning("Failed to enumerate system voices: {Error}", result.StandardError);
                return Array.Empty<string>();
            }

            return voices
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to enumerate installed system voices");
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> ParseLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) yield break;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    // macOS `say -v ?` output: `Alex                en_US    # Most people recognize me by my voice.`
    // 名字可能含空格（如 "Bad News"），取第一段直到出现 2+ 空格再跟着 locale。
    private static readonly Regex MacSayLineRegex = new(@"^(.+?)\s{2,}[A-Za-z_\-]+\s", RegexOptions.Compiled);

    private static IEnumerable<string> ParseMacSayVoices(string output)
    {
        foreach (var line in ParseLines(output))
        {
            var m = MacSayLineRegex.Match(line);
            if (m.Success) yield return m.Groups[1].Value.Trim();
        }
    }

    // espeak-ng `--voices` output columns: `Pty Language Age/Gender VoiceName File Other`
    // 表头首个词是 "Pty"，跳过；VoiceName 是第 4 列。
    private static IEnumerable<string> ParseEspeakVoices(string output)
    {
        foreach (var line in ParseLines(output))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (string.Equals(parts[0], "Pty", StringComparison.OrdinalIgnoreCase)) continue;
            yield return parts[3];
        }
    }
}
