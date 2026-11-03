using System.ComponentModel;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace VupPlayer.App.Services;

/// <summary>
/// 接入 Windows 系统媒体控件（SMTC）：键盘媒体键、音量浮窗和锁屏上的播放控制。
/// 借用一个不播放任何内容的 MediaPlayer 来拿到 SMTC。
/// </summary>
public sealed class MediaControls : IDisposable
{
    readonly MediaPlayer _carrier = new();
    readonly SystemMediaTransportControls _smtc;
    readonly PlayerController _player;
    readonly Dispatcher _ui;

    public MediaControls(PlayerController player, Dispatcher ui)
    {
        _player = player;
        _ui = ui;
        _carrier.CommandManager.IsEnabled = false;
        _smtc = _carrier.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButton;
        player.PropertyChanged += OnPlayerChanged;
    }

    void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        _ui.BeginInvoke(async () =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (_player.Current is null) _player.TogglePause(); else _player.SetPaused(false);
                    break;
                case SystemMediaTransportControlsButton.Pause: _player.SetPaused(true); break;
                case SystemMediaTransportControlsButton.Next: await _player.NextAsync(); break;
                case SystemMediaTransportControlsButton.Previous: await _player.PreviousAsync(); break;
                case SystemMediaTransportControlsButton.Stop: _player.Stop(); break;
            }
        });
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerController.Current):
                case nameof(PlayerController.Live):
                    UpdateDisplay();
                    break;
                case nameof(PlayerController.IsPlaying):
                    _smtc.PlaybackStatus = _player.Current is null && _player.Live is null ? MediaPlaybackStatus.Stopped
                        : _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("SMTC 更新失败：" + ex.Message);
        }
    }

    void UpdateDisplay()
    {
        var u = _smtc.DisplayUpdater;
        u.ClearAll();
        // 看直播时媒体浮窗显示直播间
        if (_player.Live is { } live)
        {
            u.Type = MediaPlaybackType.Music;
            u.MusicProperties.Title = live.Title ?? "直播";
            u.MusicProperties.Artist = live.Name;
            u.MusicProperties.AlbumTitle = "B 站直播" + (live.AreaName is { Length: > 0 } a ? " · " + a : "");
            if (!string.IsNullOrEmpty(live.Cover) && Uri.TryCreate(live.Cover, UriKind.Absolute, out var cu))
                u.Thumbnail = RandomAccessStreamReference.CreateFromUri(cu);
            u.Update();
            return;
        }
        var t = _player.Current;
        if (t is null)
        {
            u.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            return;
        }
        u.Type = MediaPlaybackType.Music;
        u.MusicProperties.Title = t.DisplayTitle;
        u.MusicProperties.Artist = t.StreamerName ?? t.UpName;
        u.MusicProperties.AlbumTitle = $"切片：{t.UpName}";
        if (!string.IsNullOrEmpty(t.CoverUrl) && Uri.TryCreate(t.CoverUrl, UriKind.Absolute, out var uri))
            u.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        u.Update();
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerChanged;
        _smtc.ButtonPressed -= OnButton;
        _smtc.IsEnabled = false;
        _carrier.Dispose();
    }
}
