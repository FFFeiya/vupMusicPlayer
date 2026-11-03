using CommunityToolkit.Mvvm.ComponentModel;
using VupPlayer.Bilibili;
using VupPlayer.Core;
using VupPlayer.Core.Streams;
using VupPlayer.Playback;

namespace VupPlayer.App.Services;

/// <summary>正在看的一场直播。</summary>
public sealed partial class LiveSession : ObservableObject
{
    public required long RoomId { get; init; }
    public long Mid { get; init; }
    /// <summary>主播名（优先用曲库里的名字，和曲库分组一致）。</summary>
    public required string Name { get; init; }
    public string? Cover { get; init; }
    /// <summary>本场开播时间。</summary>
    public long? LiveSince { get; init; }
    /// <summary>开始看的时间。</summary>
    public long StartedAt { get; init; }

    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial string? AreaName { get; set; }
    [ObservableProperty] public partial long Online { get; set; }
    /// <summary>向 B 站请求的画质。不要用实际拿到的画质覆盖它，否则一次降级之后就再也回不到原画。</summary>
    [ObservableProperty] public partial int Qn { get; set; }
    /// <summary>实际拿到的画质描述。</summary>
    [ObservableProperty] public partial string? QualityName { get; set; }

    /// <summary>这个房间可选的画质。</summary>
    public IReadOnlyList<(int Qn, string Desc)> Qualities { get; set; } = [];

    public string Url => $"https://live.bilibili.com/{RoomId}";

    public string OnlineText => Online >= 10000 ? $"{Online / 10000.0:0.#} 万人气" : Online > 0 ? $"{Online} 人气" : "";
}

/// <summary>
/// 直播会话。和曲库播放共用同一个 mpv 实例（它依附在主窗口的子窗口上），所以直播没有
/// 独立的控制器，而是 PlayerController 的一个显式状态：Live 不为空时 Current 为空。
/// 直播没有时长也没有「下一首」，看门狗和 EOF 处理都走这里的分支，绝不会调 NextAsync。
/// </summary>
public sealed partial class PlayerController
{
    /// <summary>直播取流与状态查询。由 App 注入。（属性名不叫 LiveService，免得遮蔽同名类型）</summary>
    public LiveService? LiveApi { get; set; }

    [ObservableProperty] public partial LiveSession? Live { get; private set; }

    /// <summary>主播下播、或直播流彻底连不上时触发（App 用它撤回通知、给出提示）。</summary>
    public event Action<LiveSession>? LiveEnded;

    /// <summary>直播断流后最多自动重连几次。</summary>
    const int MaxLiveReconnects = 5;
    int _liveReconnects;

    /// <summary>正在看直播（给界面绑定用，省得写 Live != null 的转换器）。</summary>
    public bool IsWatchingLive => Live is not null;

    partial void OnLiveChanged(LiveSession? value)
    {
        OnPropertyChanged(nameof(IsWatchingLive));
    }

    // ---------------- 入口 ----------------

    /// <summary>开始看一个直播间。会先给正在播的曲目收尾。</summary>
    public async Task StartLiveAsync(long roomId, string name, long mid = 0, string? cover = null,
        string? title = null, string? area = null, long? liveSince = null, int? qn = null)
    {
        if (LiveApi is null)
        {
            StatusText = "直播功能还没初始化";
            return;
        }
        FinishPlayEvent(userInitiated: true);
        FinishLiveEvent();

        var gen = ++_generation;
        Current = null;
        RecommendReason = null;
        _currentPicker = null;
        _pendingPick = null;
        _prefetched = false;
        _line = 0;
        _lineSwitches = 0;
        _liveReconnects = 0;
        _currentStream = null;
        Position = 0;
        Duration = 0;
        _lastPos = 0;
        _playedSec = 0;
        Live = new LiveSession
        {
            RoomId = roomId,
            Mid = mid,
            Name = name,
            Cover = cover,
            LiveSince = liveSince,
            StartedAt = Clock.Now(),
            Title = title,
            AreaName = area,
            Qn = qn ?? BiliApi.QnOriginal,
        };
        AppLog.Write($"开始看直播 room={roomId} {name} qn={Live.Qn} mode={StreamMode}");
        await LoadLiveAsync(gen);
    }

    /// <summary>离开直播。stopPlayback 为 false 时留给调用方接着播曲库（例如按了「下一首」）。</summary>
    public void StopLive(bool stopPlayback = true)
    {
        if (Live is null) return;
        var live = Live;
        FinishLiveEvent();
        Live = null;
        QualityText = null;
        AppLog.Write($"离开直播 room={live.RoomId}");
        if (!stopPlayback) return;
        _generation++;
        _mpv?.Stop();
        IsPlaying = false;
        Position = 0;
        Duration = 0;
    }

    /// <summary>切换直播画质。</summary>
    public async Task SetLiveQualityAsync(int qn)
    {
        if (Live is not { } live || live.Qn == qn) return;
        live.Qn = qn;
        _line = 0;
        _lineSwitches = 0;
        StatusText = "正在切换画质…";
        await LoadLiveAsync(++_generation, keepStatus: true);
    }

    // ---------------- 加载 ----------------

    /// <summary>重新取地址并播放。</summary>
    async Task LoadLiveAsync(int gen, bool keepStatus = false)
    {
        if (_mpv is null) { StatusText = "播放器尚未初始化"; return; }
        if (Live is not { } live || LiveApi is null) return;
        IsLoading = true;
        if (!keepStatus) StatusText = "正在连接直播…";

        ResolvedLive resolved;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            resolved = await LiveApi.ResolveAsync(live.RoomId, live.Qn);
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            IsLoading = false;
            OnLiveError(ex.Message);
            return;
        }
        if (gen != _generation) return;

        live.Qualities = resolved.Qualities;
        // 只更新「实际画质」，不动 live.Qn（请求画质）：B 站临时给了蓝光不代表以后都只能要蓝光
        live.QualityName = resolved.QualityName;
        _currentStream = resolved.Stream;
        if (_lineSwitches == 0) _line = resolved.Stream.FirstHealthyLine(_health, DateTimeOffset.UtcNow);
        AppLog.Write($"直播取流 room={live.RoomId} {sw.ElapsedMilliseconds}ms {resolved.Lines.Count} 条线路 请求={live.Qn} 实际={resolved.QualityName}");
        await ProbeLiveLineAsync(resolved.Stream, gen);
        if (gen != _generation) return;
        PlayLiveLine(gen, resolved.Stream, keepStatus);
    }

    /// <summary>
    /// 直播线路先探后播。实测同一个海外节点第一次连接要 6 秒以上、第二次只要 0.6 秒，
    /// 不先探的话看门狗会在建连期间就判「加载太慢」，换到另一条同样冷的线路，没完没了。
    /// </summary>
    async Task ProbeLiveLineAsync(ResolvedStream stream, int gen)
    {
        var probe = await _prober.PickAsync(stream, _line, StreamMode == StreamMode.Audio, _health);
        if (gen != _generation) return;
        if (probe.Line != _line)
        {
            AppLog.Write($"  直播探测跳过 {probe.Probed - 1} 条线路（{probe.Error}），改用线路 {probe.Line + 1}/{stream.LineCount}");
            _line = probe.Line;
        }
        else if (probe.Error is not null && probe.Probed > 1)
        {
            AppLog.Write($"  直播线路都探不通（{probe.Error}），还是交给 mpv 试线路 {_line + 1}/{stream.LineCount}");
        }
    }

    /// <summary>不重新取地址，只用当前地址换一条线路或换画面/纯音频。</summary>
    async Task ReloadLiveLineAsync(int gen, bool keepStatus = false)
    {
        if (_currentStream is { } stream && Live is not null && _mpv is not null)
        {
            IsLoading = true;
            await ProbeLiveLineAsync(stream, gen);
            if (gen != _generation) return;
            PlayLiveLine(gen, stream, keepStatus);
            return;
        }
        await LoadLiveAsync(gen, keepStatus);
    }

    void PlayLiveLine(int gen, ResolvedStream stream, bool keepStatus)
    {
        if (_mpv is null || Live is not { } live || gen != _generation) return;
        var lineText = stream.LineCount > 1 ? $" · 线路 {_line + 1}/{stream.LineCount}" : "";
        QualityText = StreamMode == StreamMode.Audio
            ? $"直播 · 纯音频{lineText}"
            : $"直播 · {live.QualityName ?? "原画"}{lineText}";
        if (!keepStatus) StatusText = null;
        _lastLoadAt = DateTime.UtcNow;
        _mpvLoadSince = _lastLoadAt;
        _lastCache = 0;
        _waitExtensions = 0;
        AppLog.Write($"  直播线路 {_line + 1}/{stream.LineCount}: {stream.HostAt(_line)}");
        _mpv.Load(new MediaSource(
            stream.VideoAt(_line), stream.AudioAt(_line), stream.Headers,
            AudioOnly: StreamMode == StreamMode.Audio));
    }

    // ---------------- 看门狗 ----------------

    /// <summary>直播最多换几次线路。比曲库宽松：一场直播可能看几小时，中途换线路很正常。</summary>
    const int MaxLiveSwitches = 12;

    void LiveWatchdog()
    {
        if (_mpv is null || Live is null) return;

        // 连上了但一直不出数据。直播首帧比点播慢一些，给到 15 秒
        if (IsLoading && _mpvLoadSince is { } ls && DateTime.UtcNow - ls >= TimeSpan.FromSeconds(15))
        {
            // 数据在进来，只是慢：再等一轮，别换到另一条冷线路
            if (CacheGrowing()) return;
            AppLog.Write("watchdog: 直播加载超过 15 秒");
            _mpvLoadSince = null;
            if (!TrySwitchLiveLine("连接太慢")) OnLiveError("直播一直连不上");
            return;
        }
        if (!IsPlaying || IsLoading)
        {
            _watchPos = -1;
            _bufferingSince = null;
            return;
        }

        _bufferingSince = IsBuffering ? _bufferingSince ?? DateTime.UtcNow : null;
        var bufferingStall = _bufferingSince is { } bs && DateTime.UtcNow - bs >= TimeSpan.FromSeconds(8);
        if (!bufferingStall)
        {
            if (Math.Abs(_lastPos - _watchPos) > 0.05)
            {
                // 播得好好的：把换线路的次数清零，这样长时间看播也不会攒满上限
                _watchPos = _lastPos;
                _watchSince = DateTime.UtcNow;
                _lineSwitches = 0;
                return;
            }
            if (DateTime.UtcNow - _watchSince < TimeSpan.FromSeconds(10)) return;
        }

        if (CacheGrowing()) return;
        AppLog.Write(bufferingStall ? "watchdog: 直播缓冲超过 8 秒" : "watchdog: 直播进度卡住超过 10 秒");
        _watchPos = -1;
        _bufferingSince = null;
        // 直播绝不能跳「下一首」：只能换线路或重新取地址
        if (!TrySwitchLiveLine("卡住了")) OnLiveError("直播一直卡住");
    }

    /// <summary>换下一条线路；线路都试过一轮就重新取地址（直播地址会因为主播切流而失效）。</summary>
    bool TrySwitchLiveLine(string why)
    {
        if (Live is null || _lineSwitches >= MaxLiveSwitches) return false;
        if (_currentStream is { } bad) _health.MarkBad(bad.HostAt(_line), DateTimeOffset.UtcNow);
        _lineSwitches++;
        var lines = _currentStream?.LineCount ?? 1;
        var gen = ++_generation;
        if (_line + 1 < lines)
        {
            _line++;
            AppLog.Write($"直播{why}：换到线路 {_line + 1}/{lines} {_currentStream?.HostAt(_line)}");
            StatusText = $"{why}，换一条线路（{_line + 1}/{lines}）…";
            _ = ReloadLiveLineAsync(gen, keepStatus: true);
        }
        else
        {
            _line = 0;
            AppLog.Write($"直播{why}：线路都试过了，重新取地址");
            StatusText = $"{why}，重新获取直播地址…";
            _ = LoadLiveAsync(gen, keepStatus: true);
        }
        return true;
    }

    // ---------------- 流结束 ----------------

    void OnLiveEndFile(MpvEndReason reason, string? error)
    {
        // 刚加载就收到的结束事件属于上一个文件
        if (DateTime.UtcNow - _lastLoadAt < TimeSpan.FromSeconds(1.5)) return;
        IsLoading = false;
        IsPlaying = false;
        if (reason is not (MpvEndReason.Eof or MpvEndReason.Error)) return;
        // 直播流断了有两种可能：主播下播，或者主播切了清晰度 / 网络抖动导致断流重连。
        // 查一次房间状态再决定是重连还是收摊。
        _ = ConfirmLiveEndedAsync(++_generation, error);
    }

    async Task ConfirmLiveEndedAsync(int gen, string? error)
    {
        if (Live is not { } live || LiveApi is null) return;
        if (_liveReconnects >= MaxLiveReconnects)
        {
            OnLiveError(error is null ? "直播流反复中断" : "直播流反复中断：" + error);
            return;
        }
        _liveReconnects++;
        StatusText = "直播流中断，正在确认…";
        AppLog.Write($"直播流中断（第 {_liveReconnects} 次）room={live.RoomId} error={error}");
        await Task.Delay(3000);
        if (gen != _generation || Live is null) return;

        var snap = await LiveApi.GetRoomAsync(live.RoomId);
        if (gen != _generation || Live is null) return;

        if (snap?.Status == LiveStatus.Live)
        {
            StatusText = "直播流中断，正在重连…";
            _line = 0;
            _lineSwitches = 0;
            await LoadLiveAsync(gen, keepStatus: true);
            return;
        }
        // 真的下播了
        AppLog.Write($"主播已下播 room={live.RoomId}");
        StatusText = $"{live.Name} 已下播";
        var ended = live;
        StopLive();
        LiveEnded?.Invoke(ended);
    }

    void OnLiveError(string message)
    {
        if (Live is not { } live) return;
        AppLog.Write($"直播出错 room={live.RoomId}：{message}");
        StatusText = message;
        IsPlaying = false;
        var ended = live;
        StopLive();
        LiveEnded?.Invoke(ended);
    }

    // ---------------- 看播记录 ----------------

    /// <summary>看了 10 秒以上才记一笔。</summary>
    void FinishLiveEvent()
    {
        if (Live is not { } live) return;
        var watched = _playedSec;
        _playedSec = 0;
        if (watched < 10) return;
        try
        {
            _repo.AddLiveEvent(new LiveWatchEvent
            {
                RoomId = live.RoomId,
                Mid = live.Mid == 0 ? null : live.Mid,
                Title = live.Title,
                StartedAt = live.StartedAt,
                WatchedSec = Math.Round(watched, 1),
                Mode = StreamMode == StreamMode.Audio ? "audio" : "video",
            });
        }
        catch
        {
            // 写看播记录失败不影响播放
        }
    }
}
