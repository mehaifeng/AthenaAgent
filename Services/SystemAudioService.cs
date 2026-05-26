using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                var resolvedVoice = await ResolveWindowsVoiceAsync(voice, cancellationToken);
                result = await _cliService.ExecuteAsync("powershell", BuildWindowsSpeechArguments(outputPath, resolvedVoice, text), ct: cancellationToken);
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

    public async Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CliResult result;

            if (OperatingSystem.IsMacOS())
            {
                result = await _cliService.ExecuteAsync("say", ["-v", "?"], ct: cancellationToken);
                return ParseMacOsVoices(result);
            }

            if (OperatingSystem.IsLinux())
            {
                result = await _cliService.ExecuteAsync("espeak-ng", ["--voices"], ct: cancellationToken);
                return ParseLinuxVoices(result);
            }

            if (OperatingSystem.IsWindows())
            {
                result = await _cliService.ExecuteAsync("powershell", BuildWindowsListVoicesArguments(), ct: cancellationToken);
                return ParseWindowsVoices(result);
            }

            return [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to enumerate system voices");
            return [];
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
                result = await _cliService.ExecuteAsync("aplay", [filePath], ct: cancellationToken);
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

    private static IEnumerable<string> BuildWindowsListVoicesArguments()
    {
        const string script = "$ErrorActionPreference='Stop';" +
                              "Add-Type -AssemblyName System.Speech;" +
                              "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                              "$s.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name };" +
                              "$s.Dispose();";
        return ["-NoProfile", "-Command", script];
    }

    private static IEnumerable<string> BuildWindowsPlayArguments(string filePath)
    {
        var escapedPath = EscapePowerShellString(filePath);
        var script = "$ErrorActionPreference='Stop';" +
                     $"$player = New-Object Media.SoundPlayer '{escapedPath}';" +
                     "$player.PlaySync();";
        return ["-NoProfile", "-Command", script];
    }

    private static string EscapePowerShellString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private async Task<string> ResolveWindowsVoiceAsync(string voice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return string.Empty;
        }

        var voices = await GetAvailableVoicesAsync(cancellationToken);
        if (voices.Count == 0)
        {
            return string.Empty;
        }

        return voices.Contains(voice, StringComparer.OrdinalIgnoreCase)
            ? voices.First(v => string.Equals(v, voice, StringComparison.OrdinalIgnoreCase))
            : string.Empty;
    }

    private static IReadOnlyList<string> ParseWindowsVoices(CliResult result)
    {
        return result.IsSuccess
            ? ParseNonEmptyLines(result.StandardOutput)
            : [];
    }

    private static IReadOnlyList<string> ParseMacOsVoices(CliResult result)
    {
        if (!result.IsSuccess)
        {
            return [];
        }

        var voices = new List<string>();
        foreach (var line in ParseNonEmptyLines(result.StandardOutput))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                voices.Add(parts[0]);
            }
        }

        return voices;
    }

    private static IReadOnlyList<string> ParseLinuxVoices(CliResult result)
    {
        if (!result.IsSuccess)
        {
            return [];
        }

        var voices = new List<string>();
        foreach (var line in ParseNonEmptyLines(result.StandardOutput))
        {
            if (line.StartsWith("Pty", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                voices.Add(parts[3]);
            }
        }

        return voices;
    }

    private static IReadOnlyList<string> ParseNonEmptyLines(string value)
    {
        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
