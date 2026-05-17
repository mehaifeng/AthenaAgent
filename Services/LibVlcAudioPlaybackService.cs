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
            NotifyState(attachment.Id, true, attachment.Position, attachment.Duration);
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
        NotifyState(_attachmentId, false, TimeSpan.FromMilliseconds(Math.Max(_player.Time, 0)), TimeSpan.FromMilliseconds(Math.Max(_player.Length, 0)));
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
        NotifyState(_attachmentId, true, TimeSpan.FromMilliseconds(e.Time), TimeSpan.FromMilliseconds(Math.Max(_player?.Length ?? 0L, 0L)));
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        NotifyState(_attachmentId, false, TimeSpan.Zero, TimeSpan.FromMilliseconds(Math.Max(_player?.Length ?? 0L, 0L)));
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        NotifyState(_attachmentId, _player?.IsPlaying == true, TimeSpan.FromMilliseconds(Math.Max(_player?.Time ?? 0L, 0L)), TimeSpan.FromMilliseconds(e.Length));
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
