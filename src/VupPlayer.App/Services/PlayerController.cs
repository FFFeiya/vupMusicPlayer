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
    IRecommender _recommender;
    MpvPlayer? _mpv;

    int _generation;                 // 每次切歌 +1，用来丢弃过期的异步结果
    Task<Recommendation?>? _pendingPick;   // 推荐模式预先挑选（并导入）下一首的任务
    string? _currentPicker;          // 当前曲目是谁选的：推荐器名 / manual / queue:模式
    string? _queuePicker;            // 当前队列来自推荐歌单时：daily / rediscover / fresh
    IReadOnlyDictionary<long, string>? _queueReasons;
    bool _prefetched;
    int _consecutiveErrors;
    int _line;                       // 当前用第几条线路（CDN 镜像）
    int _lineSwitches;               // 这首已经换了几次线路
    ResolvedStream? _currentStream;
    DateTime _lastUiPosition;

    // 播放记录
    long _eventStartedAt;
    double _playedSec;
    double _lastPos;

    // 看门狗：网络偶发卡住时恢复
    readonly DispatcherTimer _watchdog;
    double _watchPos = -1;
    DateTime _watchSince = DateTime.UtcNow;
    DateTime? _bufferingSince;
    DateTime? _mpvLoadSince;         // 交给 mpv 加载的时间，开始播放后清空
    readonly LineHealth _health = new();
    readonly LineProber _prober = new();
    double _lastCache;               // 上次看到的 demuxer 缓存秒数，用来判断「在下载」还是「真卡住」
    int _waitExtensions;             // 这次加载因为缓存在涨而多等了几轮

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
    /// <summary>当前曲目是推荐器选的时，显示推荐理由。</summary>
    [ObservableProperty] public partial string? RecommendReason { get; private set; }

    /// <summary>当前曲目播放结束、切歌时触发（用于刷新播放次数）。</summary>
    public event Action? PlayRecorded;

    public PlayerController(LibraryRepository repo, CachingStreamResolver resolver, AppSettings settings, Dispatcher ui)
    {
        _repo = repo;
        _resolver = resolver;
        _settings = settings;
        _ui = ui;
        RecommendOptions.Temperature = settings.RecommendTemperature;
        RecommendOptions.DiscoveryRate = settings.DiscoveryRate;
        RecommendFromLibrary = settings.RecommendFromLibrary;
        _recommender = RecommenderRegistry.Create(settings.Recommender, repo, RecommendOptions);
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
        // 直播的判断规则不一样（没有曲尾、不能跳下一首），单独处理
        if (Live is not null)
        {
            LiveWatchdog();
            return;
        }
        // 线路连得上但一直不出数据：mpv 自己要等 network-timeout 才报错，这里提前换线路
        if (_mpv is not null && Current is not null && IsLoading && _mpvLoadSince is { } ls
            && DateTime.UtcNow - ls >= TimeSpan.FromSeconds(10))
        {
            // 数据其实在进来（只是慢），再给一轮，别换到另一条同样冷的线路
            if (CacheGrowing()) return;
            AppLog.Write("watchdog: 加载超过 10 秒还没开始播放");
            _mpvLoadSince = null;
            if (!TrySwitchLine("连接太慢"))
            {
                StatusText = "这首一直加载不出来，跳到下一首";
                _ = NextAsync(userInitiated: false);
            }
            return;
        }
        if (_mpv is null || Current is null || !IsPlaying || IsLoading)
        {
            _watchPos = -1;
            _bufferingSince = null;
            return;
        }
        // 一直在缓冲：进度可能还在一点点挪（音频先播、画面卡住），光看进度发现得太晚
        _bufferingSince = IsBuffering ? _bufferingSince ?? DateTime.UtcNow : null;
        var bufferingStall = _bufferingSince is { } bs && DateTime.UtcNow - bs >= TimeSpan.FromSeconds(6);
        if (!bufferingStall)
        {
            if (Math.Abs(_lastPos - _watchPos) > 0.05)
            {
                _watchPos = _lastPos;
                _watchSince = DateTime.UtcNow;
                return;
            }
            if (DateTime.UtcNow - _watchSince < TimeSpan.FromSeconds(8)) return;
        }

        // 还在缓冲但数据在进来：再等一轮，别换线路
        if (CacheGrowing()) return;

        var dur = EffectiveDuration();
        AppLog.Write(bufferingStall
            ? $"watchdog: 在 {_lastPos:0.0}/{dur:0.0} 缓冲超过 6 秒"
            : $"watchdog: 进度卡在 {_lastPos:0.0}/{dur:0.0} 超过 8 秒");
        _watchPos = -1;
        _bufferingSince = null;
        if (dur > 0 && dur - _lastPos < 4)
        {
            _ = NextAsync(userInitiated: false);
        }
        else if (!TrySwitchLine("进度卡住"))
        {
            StatusText = "这首一直加载不出来，跳到下一首";
            _ = NextAsync(userInitiated: false);
        }
    }

    /// <summary>因为缓存还在涨而推迟换线路，最多推迟几轮（每轮 2 秒），免得彻底卡死也不换。</summary>
    const int MaxWaitExtensions = 4;

    /// <summary>
    /// demuxer 缓存还在变多 = 数据确实在进来，只是慢。这种情况不要换线路：
    /// 换过去的多半是另一条同样冷的线路，DNS + 握手要从头再付一次。
    /// </summary>
    bool CacheGrowing()
    {
        var cache = _mpv?.GetDouble("demuxer-cache-duration") ?? 0;
        var growing = cache > _lastCache + 0.05;
        _lastCache = cache;
        if (!growing || _waitExtensions >= MaxWaitExtensions) return false;
        _waitExtensions++;
        AppLog.Write($"watchdog: 缓存在涨（{cache:0.0}s），再等一轮（{_waitExtensions}/{MaxWaitExtensions}）");
        _mpvLoadSince = DateTime.UtcNow;
        _watchSince = DateTime.UtcNow;
        return true;
    }

    /// <summary>每首最多换几次线路（包括最后一次重新获取地址）。</summary>
    const int MaxLineSwitches = 5;

    /// <summary>
    /// 当前线路卡住或出错：换下一条线路（另一个 CDN 镜像），从原位置继续。
    /// 线路都试过一轮后，重新获取一次播放地址（可能是直链过期）。还不行就返回 false。
    /// </summary>
    bool TrySwitchLine(string why)
    {
        if (Current is not { } cur || _lineSwitches >= MaxLineSwitches) return false;
        if (_currentStream is { } bad) _health.MarkBad(bad.HostAt(_line), DateTimeOffset.UtcNow);
        _lineSwitches++;
        var pos = _lastPos > 0 ? _lastPos + (cur.StartSec ?? 0) : (double?)null;
        var lines = _currentStream?.LineCount ?? 1;
        if (_line + 1 < lines && _lineSwitches < MaxLineSwitches)
        {
            _line++;
            AppLog.Write($"{why}：换到线路 {_line + 1}/{lines} {_currentStream?.HostAt(_line)}");
            StatusText = $"{why}，换一条线路（{_line + 1}/{lines}）…";
        }
        else
        {
            _line = 0;
            _resolver.Invalidate(new TrackRef(cur.Bvid, cur.Cid, cur.Page));
            AppLog.Write($"{why}：线路都试过了，重新获取播放地址");
            StatusText = $"{why}，重新获取播放地址…";
        }
        _ = LoadCurrentAsync(cur, pos, false, ++_generation);
        return true;
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
        // 直播的 HLS 流会报出分片时长（十几秒），对直播没有意义，强制归零
        mpv.DurationChanged += d => OnUi(() => Duration = Live is not null ? 0 : d);
        mpv.PauseChanged += paused => OnUi(() =>
        {
            IsPlaying = !paused && (Current is not null || Live is not null);
            AppLog.Write($"pause={paused} track={Current?.Id} pos={_lastPos:0.0}");
        });
        mpv.BufferingChanged += b => OnUi(() => IsBuffering = b);
        mpv.FileLoaded += () => OnUi(() =>
        {
            IsLoading = false;
            _mpvLoadSince = null;
            if (StatusText is not null && (StatusText.Contains("线路") || StatusText.Contains("重新获取")
                                           || StatusText.Contains("直播") && Live is not null)) StatusText = null;
            _consecutiveErrors = 0;
            if (Live is not null) _liveReconnects = 0;
            IsPlaying = !mpv.IsPaused;
        });
        mpv.EndFile += (reason, err) => OnUi(() => OnEndFile(reason, err));
    }

    void OnUi(Action a) => _ui.BeginInvoke(a, DispatcherPriority.Normal);

    partial void OnPlayModeChanged(PlayMode value)
    {
        Queue.Mode = value;
        _pendingPick = null;
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
    /// <param name="picker">推荐歌单（daily / rediscover / fresh）播放时传入，写进播放记录，便于统计效果。</param>
    /// <param name="reasons">推荐歌单里每首的推荐理由，播放时显示。</param>
    public Task PlayListAsync(IReadOnlyList<long> trackIds, long? startId, string context,
        string? picker = null, IReadOnlyDictionary<long, string>? reasons = null)
    {
        if (trackIds.Count == 0) return Task.CompletedTask;
        // 推荐歌单要按歌单放；智能推荐模式会无视队列，所以切成列表循环
        if (picker is not null && PlayMode == PlayMode.Random) PlayMode = PlayMode.RepeatAll;
        _queuePicker = picker;
        _queueReasons = reasons;
        Queue.Load(trackIds, startId, context);
        return Queue.Current is long id
            ? StartTrackAsync(id, userInitiated: true, picker ?? "manual", reasons?.GetValueOrDefault(id))
            : Task.CompletedTask;
    }

    /// <summary>私人电台：切到智能推荐模式开始播放。firstTrackId 不为空时先放这首。</summary>
    public async Task StartRadioAsync(long? firstTrackId = null)
    {
        _queuePicker = null;
        _queueReasons = null;
        PlayMode = PlayMode.Random;
        Queue.Load(firstTrackId is long f ? [f] : [], firstTrackId, "私人电台");
        if (firstTrackId is long first)
        {
            await StartTrackAsync(first, userInitiated: true, "manual", null);
            return;
        }
        var rec = await PickAsync(foreground: true);
        if (rec?.TrackId is long id)
        {
            Queue.Jump(id);
            await StartTrackAsync(id, userInitiated: true, rec.Recommender, rec.Reason);
        }
        else StatusText = "曲库是空的，先导入一些切片吧";
    }

    /// <summary>播放队列里（或追加进队列）的某一首。</summary>
    public Task PlayTrackAsync(long trackId)
    {
        Queue.Jump(trackId);
        return StartTrackAsync(trackId, userInitiated: true, "manual", _queueReasons?.GetValueOrDefault(trackId));
    }

    public async Task NextAsync(bool userInitiated = true)
    {
        // 看直播时按「下一首」= 离开直播，回到原来的队列
        StopLive(stopPlayback: false);
        long? next;
        string picker;
        string? reason = null;
        if (Queue.NeedsRecommender)
        {
            var pending = _pendingPick;
            _pendingPick = null;
            var rec = pending is not null ? await pending : await PickAsync(foreground: true);
            next = rec?.TrackId;
            picker = rec?.Recommender ?? _recommender.Name;
            reason = rec?.Reason;
            if (next is long n) Queue.Jump(n);
        }
        else
        {
            next = Queue.Next(auto: !userInitiated);
            picker = _queuePicker ?? $"queue:{PlayMode}";
            if (next is long nid) reason = _queueReasons?.GetValueOrDefault(nid);
        }
        if (next is long id) await StartTrackAsync(id, userInitiated, picker, reason);
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
        if (Live is not null)
        {
            StopLive(stopPlayback: false);
            // 回到离开直播前正在播的那首
            if (Queue.Current is long back) { await StartTrackAsync(back, true, "manual", null); return; }
        }
        if (Position > 5 && Current is not null)
        {
            _mpv?.Seek(Current.StartSec ?? 0);
            return;
        }
        if (Queue.Previous() is long id) await StartTrackAsync(id, userInitiated: true, "manual", null);
    }

    // ---------------- 推荐 ----------------

    /// <summary>导入 B 站发现池里的视频，返回要播放的曲目 id。由 App 提供（需要导入器）。</summary>
    public Func<string, Task<long?>>? DiscoveryImporter { get; set; }

    /// <summary>推荐范围：true = 整个曲库，false = 当前列表。</summary>
    public bool RecommendFromLibrary { get; set; } = true;

    public RecommendOptions RecommendOptions { get; } = new();

    public void SetRecommender(string name)
    {
        _recommender = RecommenderRegistry.Create(name, _repo, RecommendOptions);
        _pendingPick = null;
        _prefetched = false;
    }

    /// <summary>给预览和调试用：用当前设置给候选打分。</summary>
    public IReadOnlyList<ScoredCandidate> PreviewScores() =>
        _recommender is WeightedRecommender w ? w.ScoreTracks(BuildContext()) : [];

    /// <summary>让推荐器挑一首；挑中 B 站发现池里的视频时先导入，失败就换一首曲库里的。</summary>
    async Task<Recommendation?> PickAsync(bool foreground)
    {
        var ctx = BuildContext();
        Recommendation? rec;
        try
        {
            rec = await Task.Run(() => _recommender.NextAsync(ctx));
        }
        catch (Exception ex)
        {
            AppLog.Write("推荐器出错，改用纯随机：" + ex);
            rec = await new RandomRecommender().NextAsync(ctx);
        }
        if (rec?.Discovery is { } d)
        {
            long? id = null;
            if (DiscoveryImporter is not null)
            {
                try
                {
                    if (foreground) StatusText = "正在从 B 站导入新发现的切片…";
                    id = await DiscoveryImporter(d.Bvid);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"导入发现 {d.Bvid} 失败：{ex.Message}");
                }
                finally
                {
                    if (foreground) StatusText = null;
                }
            }
            if (id is long tid)
            {
                AppLog.Write($"发现 {d.Bvid} → 曲目 {tid}：{rec.Reason}");
                rec = rec with { TrackId = tid };
            }
            else
            {
                _repo.SetDiscoveryState(d.Bvid, "rejected");
                rec = await Task.Run(() => _recommender.NextAsync(ctx with { AllowDiscovery = false }));
            }
        }
        if (rec is not null) AppLog.Write($"推荐 {rec.Recommender} track={rec.TrackId} score={rec.Score:0.###} {rec.Reason}");
        return rec;
    }

    public void TogglePause()
    {
        if (_mpv is null) return;
        if (Current is null && Live is null)
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
        // 直播不能拖进度
        if (_mpv is null || Current is null || Live is not null) return;
        _mpv.Seek(seconds + (Current.StartSec ?? 0));
    }

    public void Stop()
    {
        FinishPlayEvent(userInitiated: true);
        FinishLiveEvent();
        Live = null;
        _generation++;
        _mpv?.Stop();
        Current = null;
        IsPlaying = false;
        Position = 0;
        Duration = 0;
        QualityText = null;
    }

    /// <summary>画面 + 声音 ⇄ 纯音频。正在播放时按原位置重新加载。</summary>
    public async Task SetStreamModeAsync(StreamMode mode)
    {
        if (StreamMode == mode) return;
        StreamMode = mode;
        _settings.StreamMode = mode;
        if (Live is not null && _mpv is not null)
        {
            // 直播是同一条流，换模式只是给 mpv 加不加 vid=no，不用重新取地址
            await ReloadLiveLineAsync(++_generation);
            return;
        }
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

    async Task StartTrackAsync(long trackId, bool userInitiated, string picker, string? reason)
    {
        FinishPlayEvent(userInitiated);
        // 从直播切回曲库播放
        StopLive(stopPlayback: false);
        _currentPicker = picker;
        RecommendReason = reason;
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
        _pendingPick = null;
        _line = 0;
        _lineSwitches = 0;
        _currentStream = null;
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
        if (_lineSwitches == 0) StatusText = "正在获取播放地址…";   // 换线路时保留「正在换线路」的提示
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
        _currentStream = stream;
        if (_lineSwitches == 0) _line = stream.FirstHealthyLine(_health, DateTimeOffset.UtcNow);

        // 先探一下：连不上的线路 3 秒就跳过，不用等看门狗那 10 秒；
        // 探通的顺便焐热系统 DNS 缓存，mpv 接手时不用再等一次解析
        var probe = await _prober.PickAsync(stream, _line, StreamMode == StreamMode.Audio, _health);
        if (gen != _generation) return;
        if (probe.Line != _line)
        {
            AppLog.Write($"  探测跳过 {probe.Probed - 1} 条线路（{probe.Error}），改用线路 {probe.Line + 1}/{stream.LineCount}");
            _line = probe.Line;
        }
        else if (probe.Error is not null && probe.Probed > 1)
        {
            AppLog.Write($"  线路都探不通（{probe.Error}），还是交给 mpv 试线路 {_line + 1}/{stream.LineCount}");
        }

        var lineText = stream.LineCount > 1 && _line > 0 ? $" · 线路 {_line + 1}" : "";
        QualityText = StreamMode == StreamMode.Audio
            ? $"纯音频 · {stream.Resolver}{lineText}"
            : $"{stream.Quality ?? "视频"} · {stream.Resolver}{lineText}";
        if (_lineSwitches == 0) StatusText = null;
        _lastLoadAt = DateTime.UtcNow;
        _mpvLoadSince = _lastLoadAt;
        _lastCache = 0;
        _waitExtensions = 0;
        AppLog.Write($"  line {_line + 1}/{stream.LineCount}: {stream.HostAt(_line)}");
        _mpv.Load(new MediaSource(
            stream.VideoAt(_line), stream.AudioAt(_line), stream.Headers,
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
            // 直播没有进度，进度条位置改成显示「已看多久」
            Position = Live is not null ? _playedSec : Math.Max(0, rel);
            _lastUiPosition = now;
        }

        // 直播不预取、也没有下一首
        if (Live is not null) return;

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
                _pendingPick ??= PickAsync(foreground: false);
                next = (await _pendingPick)?.TrackId;
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
        var candidates = RecommendFromLibrary || Queue.Count == 0 ? _repo.GetVisibleTrackIds() : Queue.Items;
        var discovery = RecommendFromLibrary && RecommendOptions.DiscoveryRate > 0 && DiscoveryImporter is not null;
        return new RecommendContext(candidates, recent, Current?.Id, discovery);
    }

    void OnEndFile(MpvEndReason reason, string? error)
    {
        AppLog.Write($"end-file reason={reason} error={error} track={Current?.Id} live={Live?.RoomId} pos={_lastPos:0.0}");
        if (Live is not null)
        {
            OnLiveEndFile(reason, error);
            return;
        }
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
                // 线路连不上或直链过期：换线路 / 重新获取地址，从原位置继续
                if (TrySwitchLine("播放出错")) return;
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
                Recommender = _currentPicker,
                Reason = RecommendReason,
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
        FinishLiveEvent();
        _prober.Dispose();
        _mpv?.Dispose();
        _mpv = null;
    }
}
