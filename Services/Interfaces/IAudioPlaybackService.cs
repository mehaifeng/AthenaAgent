using System;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IAudioPlaybackService : IDisposable
{
    event EventHandler<AudioPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    bool IsAvailable { get; }

    bool Play(ChatAttachment attachment);

    void Pause();

    void Seek(TimeSpan position);

    void Stop();
}

public sealed class AudioPlaybackStateChangedEventArgs : EventArgs
{
    public string? AttachmentId { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
}
