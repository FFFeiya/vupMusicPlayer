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

    /// <summary>推荐歌单里显示的推荐理由（不来自数据库）。</summary>
    public string? Reason { get; set; }

    /// <summary>「B站新发现」里还没导入的视频：只有 BV 号，Id 为负数。</summary>
    public string? DiscoveryBvid { get; set; }
    public bool IsDiscovery => DiscoveryBvid is not null;

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
    /// <summary>谁选的这首：推荐器名，或 manual（手动点播）/ queue:模式（按队列顺序）。</summary>
    public string? Recommender { get; set; }
    public string? Reason { get; set; }
}

/// <summary>推荐器打分用的单曲特征（只含未隐藏的曲目）。</summary>
public sealed class TrackFeatures
{
    public long Id { get; set; }
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SongTitle { get; set; }
    public long? StreamerId { get; set; }
    public string? StreamerName { get; set; }
    public long UpMid { get; set; }
    public string UpName { get; set; } = "";
    public bool Favorite { get; set; }
    public long AddedAt { get; set; }
    public string? Tags { get; set; }
    /// <summary>播放次数（只算听了 3 秒以上的）。</summary>
    public int Plays { get; set; }
    /// <summary>评分之和：听完 +1，很快跳过 −1，其余按听的比例在 −1..1 之间。</summary>
    public double RatingSum { get; set; }
    public long? LastPlayedAt { get; set; }
}

/// <summary>最近的一次播放（用于连贯性、连续同主播判断）。</summary>
public sealed class RecentPlay
{
    public long TrackId { get; set; }
    public long? StreamerId { get; set; }
    public string? SongTitle { get; set; }
    public string? Tags { get; set; }
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public long StartedAt { get; set; }
}

/// <summary>B 站发现池里的一个候选。</summary>
public sealed class DiscoveryItem
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Uploader { get; set; }
    public long UploaderMid { get; set; }
    public int DurationSec { get; set; }
    public long PlayCount { get; set; }
    public long Pubdate { get; set; }
    public string? CoverUrl { get; set; }
    public string? Streamer { get; set; }
    public string? Query { get; set; }
    public long FoundAt { get; set; }
    public string State { get; set; } = "new";
}

public sealed class RecommenderStat
{
    public string Recommender { get; set; } = "";
    public int Plays { get; set; }
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public double AvgPlayedSec { get; set; }
    public double CompletionRate => Plays == 0 ? 0 : Math.Round((double)Completed / Plays, 3);
    public double SkipRate => Plays == 0 ? 0 : Math.Round((double)Skipped / Plays, 3);
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
