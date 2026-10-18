namespace VupPlayer.Core;

/// <summary>一个 B 站视频（一个 BV 号）。</summary>
public sealed class Video
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public long UpMid { get; set; }
    public string UpName { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string? Description { get; set; }
    public long? Pubdate { get; set; }
    public long AddedAt { get; set; }
    public string Status { get; set; } = "ok";
}

/// <summary>曲目：一个分P，或分P中的一段。</summary>
public sealed class Track
{
    public long Id { get; set; }
    public string Bvid { get; set; } = "";
    public long Cid { get; set; }
    public int Page { get; set; } = 1;
    public string? PartTitle { get; set; }
    public int? DurationSec { get; set; }
    public double? StartSec { get; set; }
    public double? EndSec { get; set; }
    public string? SongTitle { get; set; }
    public long? StreamerId { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }
    public string? Note { get; set; }
}

/// <summary>列表展示与 MCP 查询用的扁平视图（tracks 连接 videos、streamers 和播放统计）。</summary>
public sealed class TrackView
{
    public long Id { get; set; }
    public string Bvid { get; set; } = "";
    public long Cid { get; set; }
    public int Page { get; set; }
    public int PageCount { get; set; }
    public string? PartTitle { get; set; }
    public int? DurationSec { get; set; }
    public double? StartSec { get; set; }
    public double? EndSec { get; set; }
    public string? SongTitle { get; set; }
    public long? StreamerId { get; set; }
    public string? StreamerName { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }
    public string? Note { get; set; }
    public string VideoTitle { get; set; } = "";
    public long UpMid { get; set; }
    public string UpName { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string VideoStatus { get; set; } = "ok";
    public long AddedAt { get; set; }
    public int PlayCount { get; set; }
    public long? LastPlayedAt { get; set; }
    public string? Tags { get; set; }   // 逗号分隔

    /// <summary>界面上显示的主标题：有歌名用歌名，否则用视频标题（多P时附分P标题）。</summary>
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(SongTitle) ? SongTitle! :
        PageCount > 1 && !string.IsNullOrWhiteSpace(PartTitle) ? $"{VideoTitle} · {PartTitle}" : VideoTitle;

    public string DurationText => DurationSec is int s ? $"{s / 60}:{s % 60:00}" : "";
    public string Url => $"https://www.bilibili.com/video/{Bvid}" + (PageCount > 1 ? $"?p={Page}" : "");
}

public sealed class Streamer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Aliases { get; set; }
    public long? Mid { get; set; }
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long CreatedAt { get; set; }
    public int ItemCount { get; set; }
}

public sealed class Source
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";       // fav / space / season / url
    public string RemoteId { get; set; } = "";
    public string? Title { get; set; }
    public bool AutoSync { get; set; } = true;
    public long? LastSynced { get; set; }
    public int VideoCount { get; set; }
}

public sealed class PlayEvent
{
    public long TrackId { get; set; }
    public long StartedAt { get; set; }
    public double PlayedSec { get; set; }
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public string? Mode { get; set; }
    public string? Context { get; set; }
}

/// <summary>查询曲目的筛选条件。所有条件之间是 AND 关系。</summary>
public sealed class TrackQuery
{
    public string? Keyword { get; set; }        // 匹配视频标题、分P标题、歌名、主播、UP 主、标签
    public long? StreamerId { get; set; }
    public string? StreamerName { get; set; }
    public long? UpMid { get; set; }
    public string? Tag { get; set; }
    public long? PlaylistId { get; set; }
    public bool? Favorite { get; set; }
    public bool IncludeHidden { get; set; }
    public bool? MissingSongTitle { get; set; }
    public bool? MissingStreamer { get; set; }
    public string OrderBy { get; set; } = "added_desc";   // added_desc / added_asc / plays_desc / title / last_played_desc
    public int Limit { get; set; } = 10000;
    public int Offset { get; set; }
}

public static class Clock
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
