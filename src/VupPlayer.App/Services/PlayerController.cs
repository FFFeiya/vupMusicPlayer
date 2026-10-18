using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VupPlayer.Core;
using VupPlayer.Core.Data;
using VupPlayer.Core.Playback;
using VupPlayer.Core.Recommend;
using VupPlayer.Core.Streams;
using VupPlayer.Playback;

namespace VupPlayer.App.Services;

/// <summary>
/// 播放控制：队列 → 推荐器 → 取流 → mpv，同时负责播放记录、预取和错误恢复。
/// 所有公开方法都在 UI 线程调用。
/// </summary>
public sealed partial class PlayerController : ObservableObject, IDisposable
{
    readonly LibraryRepository _repo;
    readonly CachingStreamResolver _resolver;
    readonly AppSettings _settings;
    readonly Dispatcher _ui;
    readonly IRecommender _recommender;
    MpvPlayer? _mpv;

    int _generation;                 // 每次切歌 +1，用来丢弃过期的异步结果
    long? _pendingRandomNext;        // Random 模式预先挑好的下一首
    bool _prefetched;
    int _consecutiveErrors;
    bool _retriedCurrent;
    DateTime _lastUiPosition;

    // 播放记录
    long _eventStartedAt;
    double _playedSec;
    double _lastPos;

    // 看门狗：网络偶发卡住时恢复
    readonly DispatcherTimer _watchdog;
    double _watchPos = -1;
    DateTime _watchSince = DateTime.UtcNow;
    bool _watchRecovered;
    DateTime _lastLoadAt;

    public PlayQueue Queue { get; } = new();

    [ObservableProperty] public partial TrackView? Current { get; private set; }
    [ObservableProperty] public partial double Position { get; private set; }
    [ObservableProperty] public partial double Duration { get; private set; }
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial bool IsBuffering { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string? StatusText { get; private set; }
    [ObservableProperty] public partial string? QualityText { get; private set; }
    [ObservableProperty] public partial PlayMode PlayMode { get; set; }
    [ObservableProperty] public partial StreamMode StreamMode { get; private set; }
    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool Normalize { get; set; }

    /// <summary>当前曲目播放结束、切歌时触发（用于刷新播放次数）。</summary>
    public event Action? PlayRecorded;

    public PlayerController(LibraryRepository repo, CachingStreamResolver resolver, AppSettings settings, Dispatcher ui)
    {
        _repo = repo;
        _resolver = resolver;
        _settings = settings;
        _ui = ui;
        _recommender = RecommenderRegistry.CreateDefault();
        PlayMode = settings.PlayMode;
        StreamMode = settings.StreamMode;
        Volume = settings.Volume;
        Normalize = settings.Normalize;
        Queue.Mode = PlayMode;
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Watchdog(), ui);
        _watchdog.Start();
    }

    /// <summary>
    /// 播放中进度长时间不动（CDN 偶发卡住）：接近曲尾就直接切下一首，否则从当前位置重新加载一次。
    /// </summary>
    void Watchdog()
    {
        if (_mpv is null || Current is null || !IsPlaying || IsLoading)
        {
            _watchPos = -1;
            return;
        }
        if (Math.Abs(_lastPos - _watchPos) > 0.05)
        {
            _watchPos = _lastPos;
            _watchSince = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _watchSince < TimeSpan.FromSeconds(8)) return;

        var dur = EffectiveDuration();
        AppLog.Write($"watchdog: 进度卡在 {_lastPos:0.0}/{dur:0.0} 超过 8 秒");
        _watchPos = -1;
        if (dur > 0 && dur - _lastPos < 4)
        {
            _ = NextAsync(userInitiated: false);
        }
        else if (!_watchRecovered)
        {
            _watchRecovered = true;
            var cur = Current;
            _resolver.Invalidate(new TrackRef(cur.Bvid, cur.Cid, cur.Page));
            _ = LoadCurrentAsync(cur, _lastPos + (cur.StartSec ?? 0), false, ++_generation);
        }
    }

    public bool IsAttached => _mpv is not null;

    /// <summary>调试用：读取 mpv 属性。</summary>
    public string? DebugProperty(string name) => _mpv?.GetString(name);
    public string RecommenderName => _recommender.Name;

    public void Attach(MpvPlayer mpv)
    {
        _mpv = mpv;
        mpv.SetVolume(Volume);
        mpv.SetNormalize(Normalize);
        mpv.PositionChanged += p => OnUi(() => OnPosition(p));
        mpv.DurationChanged += d => OnUi(() => Duration = d);
        mpv.PauseChanged += paused => OnUi(() => IsPlaying = !paused && Current is not null);
        mpv.BufferingChanged += b => OnUi(() => IsBuffering = b);
        mpv.FileLoaded += () => OnUi(() =>
        {
            IsLoading = false;
            _consecutiveErrors = 0;
            IsPlaying = !mpv.IsPaused;
        });
        mpv.EndFile += (reason, err) => OnUi(() => OnEndFile(reason, err));
    }

    void OnUi(Action a) => _ui.BeginInvoke(a, DispatcherPriority.Normal);

    partial void OnPlayModeChanged(PlayMode value)
    {
        Queue.Mode = value;
        _pendingRandomNext = null;
        _prefetched = false;
        _settings.PlayMode = value;
    }

    partial void OnVolumeChanged(double value)
    {
        _mpv?.SetVolume(value);
        _settings.Volume = value;
    }

    partial void OnNormalizeChanged(bool value)
    {
        _mpv?.SetNormalize(value);
        _settings.Normalize = value;
    }

    // ---------------- 播放入口 ----------------

    /// <summary>用一个列表替换队列并从 startId 开始播放。</summary>
    public Task PlayListAsync(IReadOnlyList<long> trackIds, long? startId, string context)
    {
        if (trackIds.Count == 0) return Task.CompletedTask;
        Queue.Load(trackIds, startId, context);
        return Queue.Current is long id ? StartTrackAsync(id, userInitiated: true) : Task.CompletedTask;
    }

    /// <summary>播放队列里（或追加进队列）的某一首。</summary>
    public Task PlayTrackAsync(long trackId)
    {
        Queue.Jump(trackId);
        return StartTrackAsync(trackId, userInitiated: true);
    }

    public async Task NextAsync(bool userInitiated = true)
    {
        long? next;
        if (Queue.NeedsRecommender)
        {
            next = _pendingRandomNext ?? await _recommender.NextAsync(BuildContext());
            _pendingRandomNext = null;
            if (next is long n) Queue.Jump(n);
        }
        else
        {
            next = Queue.Next(auto: !userInitiated);
        }
        if (next is long id) await StartTrackAsync(id, userInitiated);
        else
        {
            FinishPlayEvent(userInitiated);
            _mpv?.Stop();
            IsPlaying = false;
            StatusText = "播放列表已结束";
        }
    }

    public async Task PreviousAsync()
    {
        if (Position > 5 && Current is not null)
        {
            _mpv?.Seek(Current.StartSec ?? 0);
            return;
        }
        if (Queue.Previous() is long id) await StartTrackAsync(id, userInitiated: true);
    }

    public void TogglePause()
    {
        if (_mpv is null) return;
        if (Current is null)
        {
            // 没有在播：尝试恢复上次的曲目
            if (_settings.LastTrackId is long last && _repo.GetTrack(last) is not null)
                _ = PlayListAsync([last], last, "恢复播放");
            return;
        }
        if (IsLoading) return;
        _mpv.TogglePause();
    }

    public void SetPaused(bool paused) => _mpv?.SetPause(paused);

    public void Seek(double seconds)
    {
        if (_mpv is null || Current is null) return;
        _mpv.Seek(seconds + (Current.StartSec ?? 0));
    }

    public void Stop()
    {
        FinishPlayEvent(userInitiated: true);
        _generation++;
        _mpv?.Stop();
        Current = null;
        IsPlaying = false;
        Position = 0;
        Duration = 0;
    }

    /// <summary>画面 + 声音 ⇄ 纯音频。正在播放时按原位置重新加载。</summary>
    public async Task SetStreamModeAsync(StreamMode mode)
    {
        if (StreamMode == mode) return;
        StreamMode = mode;
        _settings.StreamMode = mode;
        if (Current is { } cur && _mpv is not null)
        {
            // 不能用 mpv 的 Position：刚切歌、新文件还没加载完时，它还是上一首的位置
            double? pos = IsLoading ? null : _lastPos + (cur.StartSec ?? 0);
            var paused = !IsLoading && _mpv.IsPaused;
            await LoadCurrentAsync(cur, pos, paused, ++_generation);
        }
    }

    public Task ToggleStreamModeAsync() =>
        SetStreamModeAsync(StreamMode == StreamMode.Video ? StreamMode.Audio : StreamMode.Video);

    public void CyclePlayMode() => PlayMode = PlayMode.NextMode();

    /// <summary>曲库里的元数据变了（比如 MCP 改了歌名），刷新当前曲目显示。</summary>
    public void RefreshCurrent()
    {
        if (Current is { } c) Current = _repo.GetTrack(c.Id) ?? c;
    }

    // ---------------- 内部 ----------------

    async Task StartTrackAsync(long trackId, bool userInitiated)
    {
        FinishPlayEvent(userInitiated);
        var gen = ++_generation;
        var track = _repo.GetTrack(trackId);
        if (track is null)
        {
            Queue.Remove(trackId);
            StatusText = "曲目已不在曲库中";
            return;
        }
        Current = track;
        _settings.LastTrackId = trackId;
        _prefetched = false;
        _pendingRandomNext = null;
        _retriedCurrent = false;
        _watchRecovered = false;
        Position = 0;
        Duration = track.DurationSec ?? 0;
        _eventStartedAt = Clock.Now();
        _playedSec = 0;
        _lastPos = 0;
        await LoadCurrentAsync(track, null, false, gen);
    }

    async Task LoadCurrentAsync(TrackView track, double? absolutePos, bool paused, int gen)
    {
        if (_mpv is null) { StatusText = "播放器尚未初始化"; return; }
        IsLoading = true;
        StatusText = "正在获取播放地址…";
        ResolvedStream stream;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            stream = await _resolver.ResolveAsync(new TrackRef(track.Bvid, track.Cid, track.Page));
            AppLog.Write($"load track={track.Id} {track.Bvid} p{track.Page} mode={StreamMode} resolve={sw.ElapsedMilliseconds}ms via {stream.Resolver} start={absolutePos ?? track.StartSec}");
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            IsLoading = false;
            OnPlaybackError("取流失败：" + ex.Message);
            return;
        }
        if (gen != _generation) return;   // 用户已经切到别的歌了
        QualityText = StreamMode == StreamMode.Audio
            ? $"纯音频 · {stream.Resolver}"
            : $"{stream.Quality ?? "视频"} · {stream.Resolver}";
        StatusText = null;
        _lastLoadAt = DateTime.UtcNow;
        _mpv.Load(new MediaSource(
            stream.VideoUrl, stream.AudioUrl, stream.Headers,
            Start: absolutePos ?? track.StartSec,
            End: track.EndSec,
            AudioOnly: StreamMode == StreamMode.Audio,
            StartPaused: paused));
    }

    void OnPosition(double absolute)
    {
        // 加载新文件期间收到的进度属于上一个文件
        if (IsLoading) return;
        var rel = absolute - (Current?.StartSec ?? 0);
        var delta = rel - _lastPos;
        if (delta is > 0 and < 2) _playedSec += delta;
        _lastPos = rel;

        // 限制界面刷新频率
        var now = DateTime.UtcNow;
        if (now - _lastUiPosition > TimeSpan.FromMilliseconds(250))
        {
            Position = Math.Max(0, rel);
            _lastUiPosition = now;
        }

        // 播到一定进度时预取下一首的播放地址，切歌时就不用等
        var dur = EffectiveDuration();
        if (!_prefetched && dur > 0 && rel > Math.Min(30, dur * 0.5))
        {
            _prefetched = true;
            _ = PrefetchAsync();
        }
    }

    double EffectiveDuration()
    {
        if (Current is null) return 0;
        if (Current.EndSec is double e) return e - (Current.StartSec ?? 0);
        return Duration > 0 ? Duration : Current.DurationSec ?? 0;
    }

    async Task PrefetchAsync()
    {
        try
        {
            long? next;
            if (Queue.NeedsRecommender)
            {
                _pendingRandomNext ??= await _recommender.NextAsync(BuildContext());
                next = _pendingRandomNext;
            }
            else next = Queue.PeekNext(auto: true);
            if (next is not long id || id == Current?.Id) return;
            if (_repo.GetTrack(id) is { } t)
                await _resolver.ResolveAsync(new TrackRef(t.Bvid, t.Cid, t.Page));
        }
        catch
        {
            // 预取失败不影响当前播放
        }
    }

    RecommendContext BuildContext()
    {
        var recent = _repo.RecentlyPlayed(50).Reverse().ToList();
        recent.AddRange(Queue.History.TakeLast(50));
        return new RecommendContext(Queue.Items, recent, Current?.Id);
    }

    void OnEndFile(MpvEndReason reason, string? error)
    {
        AppLog.Write($"end-file reason={reason} error={error} track={Current?.Id} pos={_lastPos:0.0}");
        switch (reason)
        {
            case MpvEndReason.Eof:
                // 刚加载新文件就收到的 EOF 属于上一个文件（例如看门狗已经切走了）
                if (DateTime.UtcNow - _lastLoadAt < TimeSpan.FromSeconds(1.5)) return;
                IsPlaying = false;
                _ = NextAsync(userInitiated: false);
                break;
            case MpvEndReason.Error:
                IsLoading = false;
                if (Current is { } cur && !_retriedCurrent)
                {
                    // 多半是直链过期：强制重新解析，从原位置继续
                    _retriedCurrent = true;
                    _resolver.Invalidate(new TrackRef(cur.Bvid, cur.Cid, cur.Page));
                    StatusText = "播放地址失效，正在重试…";
                    var pos = _lastPos > 0 ? _lastPos + (cur.StartSec ?? 0) : (double?)null;
                    _ = LoadCurrentAsync(cur, pos, false, ++_generation);
                    return;
                }
                OnPlaybackError("播放失败：" + error);
                break;
        }
    }

    void OnPlaybackError(string message)
    {
        StatusText = message;
        IsPlaying = false;
        if (++_consecutiveErrors >= 3)
        {
            StatusText = message + "（连续失败，已停止）";
            _consecutiveErrors = 0;
            return;
        }
        // 跳到下一首
        var gen = _generation;
        _ = Task.Delay(1500).ContinueWith(_ => OnUi(() =>
        {
            if (gen == _generation) _ = NextAsync(userInitiated: false);
        }));
    }

    void FinishPlayEvent(bool userInitiated)
    {
        if (Current is null || _playedSec < 3) return;
        var dur = EffectiveDuration();
        var completed = dur > 0 && _playedSec >= dur * 0.9;
        try
        {
            _repo.AddPlayEvent(new PlayEvent
            {
                TrackId = Current.Id,
                StartedAt = _eventStartedAt,
                PlayedSec = Math.Round(_playedSec, 1),
                Completed = completed,
                Skipped = userInitiated && !completed && _playedSec < 30,
                Mode = StreamMode == StreamMode.Audio ? "audio" : "video",
                Context = Queue.Context,
            });
        }
        catch
        {
            // 写播放记录失败不影响播放
        }
        _playedSec = 0;
        PlayRecorded?.Invoke();
    }

    public void Dispose()
    {
        _watchdog.Stop();
        FinishPlayEvent(userInitiated: true);
        _mpv?.Dispose();
        _mpv = null;
    }
}
