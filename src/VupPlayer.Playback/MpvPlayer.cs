using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VupPlayer.Playback;

public sealed class MpvException(string message) : Exception(message);

/// <summary>要播放的一项：视频流（可空）+ 音频流，加上请求头和起止位置。</summary>
public sealed record MediaSource(
    string? VideoUrl,
    string AudioUrl,
    IReadOnlyDictionary<string, string> Headers,
    double? Start = null,
    double? End = null,
    bool AudioOnly = false,
    bool StartPaused = false);

/// <summary>
/// libmpv 的托管封装。事件在后台线程触发，UI 需要自己切回调度线程。
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    nint _ctx;
    readonly Thread _eventThread;
    volatile bool _disposed;

    const ulong ObsTimePos = 1, ObsDuration = 2, ObsPause = 3, ObsVolume = 4, ObsPausedForCache = 5, ObsCacheTime = 6;

    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? BufferingChanged;
    public event Action? FileLoaded;
    public event Action<MpvEndReason, string?>? EndFile;
    public event Action<string, string>? Log;

    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>创建并初始化 mpv。wid 为要渲染到的窗口句柄；纯音频场景可以传 0。</summary>
    public MpvPlayer(nint wid, string? logLevel = "warn")
    {
        _ctx = MpvNative.mpv_create();
        if (_ctx == 0) throw new MpvException("mpv_create 失败，请确认 libmpv-2.dll 存在");

        if (wid != 0) SetOption("wid", ((long)wid).ToString(CultureInfo.InvariantCulture));
        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("idle", "yes");
        SetOption("keep-open", "no");
        SetOption("force-window", "no");
        SetOption("osc", "no");
        SetOption("osd-level", "0");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("input-cursor", "no");
        SetOption("cursor-autohide", "no");
        SetOption("ytdl", "no");
        SetOption("vo", "gpu");
        SetOption("hwdec", "auto-safe");
        SetOption("audio-display", "no");
        SetOption("volume-max", "100");
        // 控制缓存，保持内存占用小
        SetOption("cache", "yes");
        SetOption("demuxer-max-bytes", "48MiB");
        SetOption("demuxer-max-back-bytes", "16MiB");
        SetOption("network-timeout", "15");
        SetOption("audio-client-name", "VupPlayer");

        var err = MpvNative.mpv_initialize(_ctx);
        if (err < 0) throw new MpvException("mpv_initialize 失败：" + MpvNative.ErrorString(err));
        if (logLevel is not null) MpvNative.mpv_request_log_messages(_ctx, logLevel);

        MpvNative.mpv_observe_property(_ctx, ObsTimePos, "time-pos", MpvFormat.Double);
        MpvNative.mpv_observe_property(_ctx, ObsDuration, "duration", MpvFormat.Double);
        MpvNative.mpv_observe_property(_ctx, ObsPause, "pause", MpvFormat.Flag);
        MpvNative.mpv_observe_property(_ctx, ObsVolume, "volume", MpvFormat.Double);
        MpvNative.mpv_observe_property(_ctx, ObsPausedForCache, "paused-for-cache", MpvFormat.Flag);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    // ---------------- 控制 ----------------

    public void Load(MediaSource src)
    {
        var opts = new List<string>();
        foreach (var h in src.Headers)
        {
            if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) opts.Add(Kv("user-agent", h.Value));
            else if (h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) opts.Add(Kv("referrer", h.Value));
        }
        if (src.Start is double s && s > 0) opts.Add(Kv("start", s.ToString("0.###", CultureInfo.InvariantCulture)));
        opts.Add(Kv("end", src.End is double e && e > 0 ? e.ToString("0.###", CultureInfo.InvariantCulture) : "none"));
        opts.Add(Kv("pause", src.StartPaused ? "yes" : "no"));

        string main;
        if (src.AudioOnly || src.VideoUrl is null)
        {
            // loadfile 的选项只对这一个文件生效，不用担心残留
            main = src.AudioUrl;
            opts.Add(Kv("vid", "no"));
        }
        else if (src.VideoUrl == src.AudioUrl)
        {
            main = src.VideoUrl;   // 音视频合一的老格式
            opts.Add(Kv("vid", "auto"));
        }
        else
        {
            main = src.VideoUrl;
            opts.Add(Kv("vid", "auto"));
            opts.Add(Kv("audio-files", src.AudioUrl));
        }
        // mpv 0.38+：loadfile <url> <flags> <index> <options>
        Command("loadfile", main, "replace", "-1", string.Join(",", opts));
    }

    /// <summary>mpv 的 key=value 列表里用 %长度% 语法转义值，URL 中的逗号、等号都安全。</summary>
    static string Kv(string key, string value) => $"{key}=%{Encoding.UTF8.GetByteCount(value)}%{value}";

    public void Stop() => Command("stop");
    public void SetPause(bool pause) => SetProperty("pause", pause ? "yes" : "no");
    public void TogglePause() => SetPause(!IsPaused);
    public void Seek(double seconds) => CommandAsync("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes");
    public void SetVolume(double volume) => SetProperty("volume", Math.Clamp(volume, 0, 100).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>音量均衡：切片之间音量差异常常很大。</summary>
    public void SetNormalize(bool on) =>
        SetProperty("af", on ? "lavfi=[dynaudnorm=f=250:g=15:p=0.9]" : "");

    public double? GetDouble(string name)
    {
        if (_disposed) return null;
        return MpvNative.mpv_get_property(_ctx, name, MpvFormat.Double, out var v) >= 0 ? v : null;
    }

    public string? GetString(string name)
    {
        if (_disposed) return null;
        var p = MpvNative.mpv_get_property_string(_ctx, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public void SetProperty(string name, string value)
    {
        if (_disposed) return;
        var err = MpvNative.mpv_set_property_string(_ctx, name, value);
        if (err < 0) Log?.Invoke("error", $"设置 {name} 失败：{MpvNative.ErrorString(err)}");
    }

    void SetOption(string name, string value)
    {
        var err = MpvNative.mpv_set_option_string(_ctx, name, value);
        if (err < 0) throw new MpvException($"设置选项 {name}={value} 失败：{MpvNative.ErrorString(err)}");
    }

    public void Command(params string[] args) => RunCommand(args, async: false);
    public void CommandAsync(params string[] args) => RunCommand(args, async: true);

    void RunCommand(string[] args, bool async)
    {
        if (_disposed) return;
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            var err = async ? MpvNative.mpv_command_async(_ctx, 0, ptrs) : MpvNative.mpv_command(_ctx, ptrs);
            if (err < 0) throw new MpvException($"命令 {args[0]} 失败：{MpvNative.ErrorString(err)}");
        }
        finally
        {
            foreach (var p in ptrs) if (p != 0) Marshal.FreeCoTaskMem(p);
        }
    }

    // ---------------- 事件循环 ----------------

    void EventLoop()
    {
        var ctx = _ctx;
        while (!_disposed)
        {
            var evPtr = MpvNative.mpv_wait_event(ctx, -1);
            if (evPtr == 0) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            try
            {
                switch (ev.EventId)
                {
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.None:
                        continue;
                    case MpvEventId.PropertyChange:
                        OnProperty(ev);
                        break;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        EndFile?.Invoke(ef.Reason, ef.Reason == MpvEndReason.Error ? MpvNative.ErrorString(ef.Error) : null);
                        break;
                    case MpvEventId.LogMessage:
                        var lm = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                        Log?.Invoke(Marshal.PtrToStringUTF8(lm.Level) ?? "", $"[{Marshal.PtrToStringUTF8(lm.Prefix)}] {Marshal.PtrToStringUTF8(lm.Text)?.TrimEnd()}");
                        break;
                }
            }
            catch (Exception ex)
            {
                // 事件处理里的异常不能杀掉事件线程
                try { Log?.Invoke("error", "事件处理异常：" + ex.Message); } catch { }
            }
        }
    }

    void OnProperty(MpvEvent ev)
    {
        var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
        if (prop.Format == MpvFormat.None || prop.Data == 0)
        {
            if (ev.ReplyUserdata == ObsDuration) { Duration = 0; DurationChanged?.Invoke(0); }
            return;
        }
        switch (ev.ReplyUserdata)
        {
            case ObsTimePos:
                Position = Marshal.PtrToStructure<double>(prop.Data);
                PositionChanged?.Invoke(Position);
                break;
            case ObsDuration:
                Duration = Marshal.PtrToStructure<double>(prop.Data);
                DurationChanged?.Invoke(Duration);
                break;
            case ObsPause:
                IsPaused = Marshal.ReadInt32(prop.Data) != 0;
                PauseChanged?.Invoke(IsPaused);
                break;
            case ObsVolume:
                VolumeChanged?.Invoke(Marshal.PtrToStructure<double>(prop.Data));
                break;
            case ObsPausedForCache:
                BufferingChanged?.Invoke(Marshal.ReadInt32(prop.Data) != 0);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ctx = _ctx;
        MpvNative.mpv_wakeup(ctx);
        _eventThread.Join(1000);
        MpvNative.mpv_terminate_destroy(ctx);
        _ctx = 0;
    }
}
