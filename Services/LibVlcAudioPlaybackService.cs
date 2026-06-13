using System;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using LibVLCSharp.Shared;
using Serilog;

namespace Athena.UI.Services;

public class LibVlcAudioPlaybackService : IAudioPlaybackService
{
    private readonly ILogger _logger;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private string? _attachmentId;

    public event EventHandler<AudioPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public bool IsAvailable { get; private set; }

    public LibVlcAudioPlaybackService(ILogger logger)
    {
        _logger = logger.ForContext<LibVlcAudioPlaybackService>();

        try
        {
            Core.Initialize();
            _libVlc = new LibVLC();
            _player = new MediaPlayer(_libVlc);
            _player.TimeChanged += OnTimeChanged;
            _player.EndReached += OnEndReached;
            _player.LengthChanged += OnLengthChanged;
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Audio playback unavailable");
            IsAvailable = false;
        }
    }

    public bool Play(ChatAttachment attachment)
    {
        if (!IsAvailable || _player == null || _libVlc == null || string.IsNullOrWhiteSpace(attachment.StoredPath))
        {
            return false;
        }

        try
        {
            if (_attachmentId != attachment.Id)
            {
                _media?.Dispose();
                _media = new Media(_libVlc, new Uri(attachment.StoredPath));
                _player.Media = _media;
                _attachmentId = attachment.Id;
            }

            var started = _player.Play();
            NotifyState(attachment.Id, true, TimeSpan.Zero, TimeSpan.Zero);
            return started;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to play audio attachment {AttachmentId}", attachment.Id);
            return false;
        }
    }

    public void Pause()
    {
        if (!IsAvailable || _player == null)
        {
            return;
        }

        _player.Pause();
        var length = Math.Max(_player.Length, 0L);
        var time = Math.Max(_player.Time, 0L);
        NotifyState(_attachmentId, false, TimeSpan.FromMilliseconds(time), TimeSpan.FromMilliseconds(length));
    }

    public void Seek(TimeSpan position)
    {
        if (!IsAvailable || _player == null)
        {
            return;
        }

        _player.Time = Math.Max((long)position.TotalMilliseconds, 0L);
    }

    public void Stop()
    {
        if (!IsAvailable || _player == null)
        {
            return;
        }

        _player.Stop();
        NotifyState(_attachmentId, false, TimeSpan.Zero, TimeSpan.FromMilliseconds(Math.Max(_player.Length, 0)));
    }

    public void Dispose()
    {
        if (_player != null)
        {
            _player.TimeChanged -= OnTimeChanged;
            _player.EndReached -= OnEndReached;
            _player.LengthChanged -= OnLengthChanged;
            _player.Dispose();
        }

        _media?.Dispose();
        _libVlc?.Dispose();
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Report the player's actual playing state — TimeChanged may fire on the
        // trailing edge of a play->pause transition, and we must not flip the
        // attachment back to IsPlaying=true after the user pressed Pause.
        var isPlaying = _player?.IsPlaying == true;
        var length = _player?.Length ?? 0L;
        var time = e.Time;
        NotifyState(_attachmentId, isPlaying, TimeSpan.FromMilliseconds(time), TimeSpan.FromMilliseconds(Math.Max(length, 0L)));
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        var length = _player?.Length ?? 0L;
        NotifyState(_attachmentId, false, TimeSpan.Zero, TimeSpan.FromMilliseconds(Math.Max(length, 0L)));
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        // Always publish duration as soon as the media is parsed, even before
        // the user has hit Play — otherwise the total-time label stays empty
        // for the lifetime of the attachment card.
        var isPlaying = _player?.IsPlaying == true;
        var time = _player?.Time ?? 0L;
        NotifyState(_attachmentId, isPlaying, TimeSpan.FromMilliseconds(Math.Max(time, 0L)), TimeSpan.FromMilliseconds(Math.Max(e.Length, 0L)));
    }

    private void NotifyState(string? attachmentId, bool isPlaying, TimeSpan position, TimeSpan duration)
    {
        PlaybackStateChanged?.Invoke(this, new AudioPlaybackStateChangedEventArgs
        {
            AttachmentId = attachmentId,
            IsPlaying = isPlaying,
            Position = position,
            Duration = duration
        });
    }
}
