namespace VupPlayer.Core;

/// <summary>B 站直播间状态。2（轮播）是在放录像，不是真的开播。</summary>
public enum LiveStatus
{
    Offline = 0,
    Live = 1,
    Replay = 2,
}

/// <summary>一次轮询拿到的直播间快照（来自 B 站批量状态接口）。</summary>
public sealed record LiveSnapshot(
    long Mid,
    long RoomId,
    string Uname,
    string? Face,
    LiveStatus Status,
    string Title,
    string? Cover,
    string? AreaName,
    long Online,
    long LiveTime);

/// <summary>订阅的直播间（live_rooms 表一行，带上曲库里对应主播的信息）。</summary>
public sealed class LiveRoom
{
    public long Mid { get; set; }
    public long RoomId { get; set; }
    public string? Uname { get; set; }
    public long? StreamerId { get; set; }
    /// <summary>开播是否推送通知。</summary>
    public bool Notify { get; set; } = true;
    public LiveStatus LastStatus { get; set; }
    public string? LastTitle { get; set; }
    public string? LastCover { get; set; }
    public string? LastArea { get; set; }
    public long? Online { get; set; }
    /// <summary>本场开播时间。</summary>
    public long? LiveSince { get; set; }
    public long? CheckedAt { get; set; }
    public long? NotifiedAt { get; set; }
    public long AddedAt { get; set; }

    /// <summary>曲库里对应主播的名字（不来自 live_rooms 表）。</summary>
    public string? StreamerName { get; set; }
    /// <summary>曲库里这位主播的切片数（不来自 live_rooms 表）。</summary>
    public int TrackCount { get; set; }

    public bool IsLive => LastStatus == LiveStatus.Live;

    /// <summary>界面上显示的名字：优先曲库里的主播名（和曲库分组一致），否则 B 站昵称。</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(StreamerName) ? StreamerName! :
        !string.IsNullOrWhiteSpace(Uname) ? Uname! : $"房间 {RoomId}";

    public string Url => $"https://live.bilibili.com/{RoomId}";

    /// <summary>已经播了多久。</summary>
    public TimeSpan? LiveDuration => LiveSince is > 0
        ? TimeSpan.FromSeconds(Math.Max(0, Clock.Now() - LiveSince.Value))
        : null;

    public string StatusText => LastStatus switch
    {
        LiveStatus.Live => LiveDuration is { } d ? $"直播中 · 已播 {Format(d)}" : "直播中",
        LiveStatus.Replay => "轮播中",
        _ => CheckedAt is null ? "还没查过" : "未开播",
    };

    static string Format(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} 小时 {d.Minutes} 分" : $"{d.Minutes} 分钟";
}

/// <summary>一次看直播的记录。</summary>
public sealed class LiveWatchEvent
{
    public long RoomId { get; set; }
    public long? Mid { get; set; }
    public string? Title { get; set; }
    public long StartedAt { get; set; }
    public double WatchedSec { get; set; }
    public string? Mode { get; set; }
}
