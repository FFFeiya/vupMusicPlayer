using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using VupPlayer.Core;
using VupPlayer.Core.Control;
using VupPlayer.Core.Data;

namespace VupPlayer.Mcp;

public sealed record ProposedMetadata(
    [property: Description("曲目 id")] long TrackId,
    [property: Description("建议的歌名，可空")] string? SongTitle = null,
    [property: Description("建议的原唱主播名，可空")] string? Streamer = null,
    [property: Description("建议追加的标签，可空")] string[]? Tags = null,
    [property: Description("把握程度 0–1，可空")] double? Confidence = null);

/// <summary>曲库读写工具：直接访问 SQLite，不需要播放器运行。</summary>
[McpServerToolType]
public sealed class LibraryTools(LibraryRepository repo)
{
    const int ConfirmThreshold = 50;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Out(object o) => JsonSerializer.Serialize(o, Json);

    static object Row(TrackView t) => new
    {
        id = t.Id,
        song_title = t.SongTitle,
        streamer = t.StreamerName,
        uploader = t.UpName,
        uploader_mid = t.UpMid,
        video_title = t.VideoTitle,
        part_title = t.PageCount > 1 ? t.PartTitle : null,
        page = t.PageCount > 1 ? t.Page : (int?)null,
        bvid = t.Bvid,
        duration_sec = t.DurationSec,
        segment = t.StartSec is not null || t.EndSec is not null ? new { start = t.StartSec, end = t.EndSec } : null,
        tags = string.IsNullOrEmpty(t.Tags) ? null : t.Tags.Split(','),
        favorite = t.Favorite ? true : (bool?)null,
        hidden = t.Hidden ? true : (bool?)null,
        play_count = t.PlayCount,
        note = t.Note,
    };

    /// <summary>通知正在运行的播放器刷新界面（播放器没开就忽略）。</summary>
    static async Task NotifyAsync()
    {
        try { await ControlClient.SendAsync("library_changed", timeoutMs: 1500); } catch { }
    }

    [McpServerTool(Name = "search_tracks", ReadOnly = true)]
    [Description("搜索曲库。所有条件之间是 AND。返回总数和当前页的曲目。")]
    public string SearchTracks(
        [Description("关键词，匹配视频标题、分P标题、歌名、主播、UP主、标签、备注、BV号")] string? keyword = null,
        [Description("按原唱主播名精确筛选")] string? streamer = null,
        [Description("按切片 UP 主的 mid 筛选")] long? uploaderMid = null,
        [Description("按标签精确筛选")] string? tag = null,
        [Description("只看某个歌单")] long? playlistId = null,
        [Description("true 只看喜欢的")] bool? favorite = null,
        [Description("true 只看缺歌名的")] bool? missingSongTitle = null,
        [Description("true 只看缺主播的")] bool? missingStreamer = null,
        [Description("是否包含已隐藏的曲目")] bool includeHidden = false,
        [Description("排序：added_desc / added_asc / plays_desc / title / last_played_desc")] string orderBy = "added_desc",
        [Description("每页条数，最多 200")] int limit = 50,
        [Description("偏移量")] int offset = 0)
    {
        var q = new TrackQuery
        {
            Keyword = keyword, StreamerName = streamer, UpMid = uploaderMid, Tag = tag, PlaylistId = playlistId,
            Favorite = favorite, MissingSongTitle = missingSongTitle, MissingStreamer = missingStreamer,
            IncludeHidden = includeHidden, OrderBy = orderBy, Limit = Math.Clamp(limit, 1, 200), Offset = Math.Max(0, offset),
        };
        var items = repo.QueryTracks(q);
        return Out(new { total = repo.CountTracks(q), offset = q.Offset, items = items.Select(Row) });
    }

    [McpServerTool(Name = "get_tracks", ReadOnly = true)]
    [Description("按 id 获取曲目详情，包含视频简介（简介里常写着歌名和原唱）。")]
    public string GetTracks([Description("曲目 id 列表，最多 100 个")] long[] trackIds)
    {
        var list = repo.GetTracks(trackIds.Take(100));
        var descs = list.Select(t => t.Bvid).Distinct().ToDictionary(b => b, b => repo.GetVideo(b)?.Description);
        return Out(list.Select(t => new
        {
            track = Row(t),
            video_description = descs.TryGetValue(t.Bvid, out var d) && d is not null ? (d.Length > 600 ? d[..600] + "…" : d) : null,
            url = t.Url,
        }));
    }

    [McpServerTool(Name = "update_tracks", Destructive = false, Idempotent = true)]
    [Description("直接修改曲目元数据。未提供的字段不修改；字符串传空串表示清空。超过 50 首时需要 confirm=true。")]
    public async Task<string> UpdateTracks(
        [Description("要修改的曲目 id")] long[] trackIds,
        [Description("歌名")] string? songTitle = null,
        [Description("原唱主播名（不存在会自动创建；会匹配别名）")] string? streamer = null,
        [Description("追加的标签")] string[]? addTags = null,
        [Description("移除的标签")] string[]? removeTags = null,
        [Description("喜欢")] bool? favorite = null,
        [Description("隐藏（不再出现在列表和推荐里）")] bool? hidden = null,
        [Description("备注")] string? note = null,
        [Description("批量超过 50 首时必须为 true")] bool confirm = false)
    {
        var ids = trackIds.Distinct().ToList();
        if (ids.Count > ConfirmThreshold && !confirm)
        {
            var preview = repo.GetTracks(ids.Take(10)).Select(Row);
            return Out(new { applied = false, message = $"将修改 {ids.Count} 首曲目，请确认后带 confirm=true 再调用一次。", preview });
        }
        var n = repo.UpdateTracks(ids, new TrackUpdate
        {
            SongTitle = songTitle, StreamerName = streamer, AddTags = addTags, RemoveTags = removeTags,
            Favorite = favorite, Hidden = hidden, Note = note,
        });
        await NotifyAsync();
        return Out(new { applied = true, updated = n });
    }

    [McpServerTool(Name = "propose_metadata")]
    [Description("提交「待确认」的元数据修改建议，用户在播放器的「待确认的修改」里采纳或拒绝。不确定时优先用它而不是 update_tracks。")]
    public async Task<string> ProposeMetadata([Description("建议列表")] ProposedMetadata[] items)
    {
        var count = 0;
        foreach (var i in items)
        {
            if (repo.GetTrack(i.TrackId) is null) continue;
            if (!string.IsNullOrWhiteSpace(i.SongTitle)) { repo.AddSuggestion(i.TrackId, "song_title", i.SongTitle.Trim(), "claude", i.Confidence); count++; }
            if (!string.IsNullOrWhiteSpace(i.Streamer)) { repo.AddSuggestion(i.TrackId, "streamer", i.Streamer.Trim(), "claude", i.Confidence); count++; }
            foreach (var t in i.Tags ?? []) if (!string.IsNullOrWhiteSpace(t)) { repo.AddSuggestion(i.TrackId, "tag", t.Trim(), "claude", i.Confidence); count++; }
        }
        await NotifyAsync();
        return Out(new { suggestions_created = count });
    }

    [McpServerTool(Name = "list_pending_suggestions", ReadOnly = true)]
    [Description("列出还没被用户处理的修改建议。")]
    public string ListPendingSuggestions() =>
        Out(repo.ListPendingSuggestions().Select(s => new { s.Id, s.TrackId, track = s.TrackTitle, s.Field, s.Value, s.Source, s.Confidence }));

    [McpServerTool(Name = "list_facets", ReadOnly = true)]
    [Description("列出曲库里的主播、切片 UP 主、标签、歌单、订阅源及各自的曲目数，用于了解曲库全貌。")]
    public string ListFacets() => Out(new
    {
        total_tracks = repo.CountTracks(new TrackQuery()),
        streamers = repo.ListStreamers().Select(s => new { s.Id, s.Name, tracks = s.Count, aliases = repo.GetStreamers().FirstOrDefault(x => x.Id == s.Id)?.Aliases }),
        uploaders = repo.ListUploaders().Select(u => new { mid = u.Id, u.Name, tracks = u.Count }),
        tags = repo.ListTags().Select(t => new { t.Name, tracks = t.Count }),
        playlists = repo.ListPlaylists().Select(p => new { p.Id, p.Name, tracks = p.ItemCount }),
        sources = repo.ListSources().Select(s => new { s.Id, s.Kind, s.Title, videos = s.VideoCount, last_synced = s.LastSynced is long l ? DateTimeOffset.FromUnixTimeSeconds(l).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null }),
    });

    [McpServerTool(Name = "merge_streamers")]
    [Description("把若干主播合并到一个主播下（处理同一个人的不同写法），被合并的名字自动成为别名。")]
    public async Task<string> MergeStreamers(
        [Description("保留的主播 id")] long intoId,
        [Description("要并入的主播 id")] long[] fromIds)
    {
        var moved = repo.MergeStreamers(intoId, fromIds);
        await NotifyAsync();
        return Out(new { tracks_moved = moved });
    }

    [McpServerTool(Name = "set_streamer_aliases", Idempotent = true)]
    [Description("设置主播别名（整体替换）。导入新切片时，标题里出现别名也会识别为这个主播。")]
    public async Task<string> SetStreamerAliases(long streamerId, string[] aliases)
    {
        repo.SetStreamerAliases(streamerId, aliases);
        await NotifyAsync();
        return Out(new { ok = true });
    }

    [McpServerTool(Name = "find_duplicates", ReadOnly = true)]
    [Description("找出同一主播同一歌名的多个切片。")]
    public string FindDuplicates() => Out(repo.FindDuplicates());

    [McpServerTool(Name = "create_playlist")]
    [Description("新建歌单，可同时放入曲目。")]
    public async Task<string> CreatePlaylist(string name, [Description("可选，初始曲目 id（按顺序）")] long[]? trackIds = null)
    {
        var id = repo.CreatePlaylist(name);
        var n = trackIds is null ? 0 : repo.AddToPlaylist(id, trackIds);
        await NotifyAsync();
        return Out(new { playlist_id = id, added = n });
    }

    [McpServerTool(Name = "add_to_playlist")]
    [Description("追加曲目到歌单末尾（已存在的跳过）。")]
    public async Task<string> AddToPlaylist(long playlistId, long[] trackIds)
    {
        var n = repo.AddToPlaylist(playlistId, trackIds);
        await NotifyAsync();
        return Out(new { added = n });
    }

    [McpServerTool(Name = "remove_from_playlist")]
    [Description("从歌单移除曲目（曲目本身不受影响）。")]
    public async Task<string> RemoveFromPlaylist(long playlistId, long[] trackIds)
    {
        var n = repo.RemoveFromPlaylist(playlistId, trackIds);
        await NotifyAsync();
        return Out(new { removed = n });
    }

    [McpServerTool(Name = "reorder_playlist", Idempotent = true)]
    [Description("按给定顺序重排歌单。必须包含歌单里的全部曲目，否则拒绝执行。")]
    public async Task<string> ReorderPlaylist(long playlistId, long[] trackIds)
    {
        var current = repo.GetPlaylistTrackIds(playlistId).ToHashSet();
        if (!current.SetEquals(trackIds))
            return Out(new { applied = false, message = $"track_ids 必须正好是歌单里的 {current.Count} 首曲目" });
        repo.SetPlaylistOrder(playlistId, trackIds);
        await NotifyAsync();
        return Out(new { applied = true });
    }

    [McpServerTool(Name = "get_play_stats", ReadOnly = true)]
    [Description("播放统计：每首的播放次数、完播次数、跳过次数、累计分钟数。")]
    public string GetPlayStats([Description("最近多少天，空表示全部")] int? days = null, int limit = 30)
    {
        long? since = days is int d ? DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() : null;
        return Out(repo.GetPlayStats(since, Math.Clamp(limit, 1, 500)));
    }
}

/// <summary>播放控制工具：通过命名管道发给正在运行的播放器。</summary>
[McpServerToolType]
public sealed class PlayerTools
{
    static async Task<string> SendAsync(string cmd, JsonObject? args = null, int timeoutMs = 8000)
    {
        var resp = await ControlClient.SendAsync(cmd, args, timeoutMs);
        if (resp is null) return LibraryTools.Out(new { ok = false, error = "播放器没有运行。请先打开 VupPlayer。" });
        return resp.Ok
            ? (resp.Data?.ToJsonString(ControlProtocol.Json) ?? "{\"ok\":true}")
            : LibraryTools.Out(new { ok = false, error = resp.Error });
    }

    static JsonArray Arr(IEnumerable<long> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());

    [McpServerTool(Name = "now_playing", ReadOnly = true)]
    [Description("当前播放状态：曲目、进度、播放模式、画面/纯音频。")]
    public Task<string> NowPlaying() => SendAsync("now_playing");

    [McpServerTool(Name = "get_queue", ReadOnly = true)]
    [Description("当前播放队列（从正在播放的曲目开始）。")]
    public Task<string> GetQueue(int limit = 30) => SendAsync("queue", new JsonObject { ["limit"] = limit });

    [McpServerTool(Name = "play")]
    [Description("用这些曲目替换播放队列并开始播放。")]
    public Task<string> Play(
        [Description("曲目 id，按播放顺序")] long[] trackIds,
        [Description("从哪一首开始，默认第一首（洗牌/随机模式下随机）")] long? startId = null,
        [Description("可选：Sequential / RepeatAll / RepeatOne / Shuffle / Random")] string? mode = null,
        [Description("可选：队列来源描述，会写进播放记录")] string? context = null)
    {
        var a = new JsonObject { ["track_ids"] = Arr(trackIds), ["context"] = context ?? "Claude" };
        if (startId is long s) a["start_id"] = s;
        if (mode is not null) a["mode"] = mode;
        return SendAsync("play", a);
    }

    [McpServerTool(Name = "enqueue")]
    [Description("把曲目加入播放队列。next=true 插到当前曲目之后，否则追加到队尾。")]
    public Task<string> Enqueue(long[] trackIds, bool next = false) =>
        SendAsync("enqueue", new JsonObject { ["track_ids"] = Arr(trackIds), ["next"] = next });

    [McpServerTool(Name = "player_control")]
    [Description("播放控制。action：pause / resume / toggle / next / previous / stop / seek（value=秒）/ volume（value=0–100）/ mode（value=Sequential|RepeatAll|RepeatOne|Shuffle|Random）/ stream_mode（value=Video|Audio）")]
    public Task<string> PlayerControl(string action, [Description("seek、volume、mode、stream_mode 需要")] string? value = null)
    {
        var a = new JsonObject { ["action"] = action };
        if (value is not null)
            a["value"] = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : value;
        return SendAsync("control", a);
    }

    [McpServerTool(Name = "import_from_bilibili")]
    [Description("让播放器导入 B 站内容：BV 号、视频链接、收藏夹、合集或 UP 主空间链接。使用播放器里的登录状态。")]
    public Task<string> ImportFromBilibili(string input) =>
        SendAsync("import", new JsonObject { ["input"] = input }, timeoutMs: 600_000);
}
