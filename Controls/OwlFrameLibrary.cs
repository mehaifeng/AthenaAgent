using Athena.UI.Models;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Concurrent;

namespace Athena.UI.Controls;

/// <summary>Shared, lazily decoded independent frames. No atlas neighbours can enter the sampler.</summary>
public static class OwlFrameLibrary
{
    private static readonly ConcurrentDictionary<OwlAction, Lazy<Bitmap[]>> Frames = new();
    public static Bitmap Get(OwlAction action, int index)
    {
        var frames = Frames.GetOrAdd(action, a => new Lazy<Bitmap[]>(() => Load(a))).Value;
        return frames[Math.Clamp(index, 0, frames.Length - 1)];
    }

    private static Bitmap[] Load(OwlAction action)
    {
        var frames = new Bitmap[OwlAnimationClips.Get(action).Durations.Count];
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                using var stream = AssetLoader.Open(new Uri(
                    $"avares://Athena.UI/Assets/SubAgents/V2/{action.ToString().ToLowerInvariant()}/{i + 1:D2}.png"));
                frames[i] = new Bitmap(stream);
                if (frames[i].PixelSize.Width != 256 || frames[i].PixelSize.Height != 256)
                    throw new InvalidOperationException($"Owl frame {action}/{i + 1} must be 256×256.");
            }
            return frames;
        }
        catch
        {
            // Publish only a complete clip. Failed lazy loads must not leak the earlier frames.
            foreach (var frame in frames) frame?.Dispose();
            throw;
        }
    }
}
