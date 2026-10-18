using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace VupPlayer.Core.Data;

/// <summary>对曲目的批量修改。为 null 的字段表示不修改；字符串传空串表示清空。</summary>
public sealed class TrackUpdate
{
    public string? SongTitle { get; set; }
    public string? StreamerName { get; set; }
    public bool? Favorite { get; set; }
    public bool? Hidden { get; set; }
    public string? Note { get; set; }
    public IReadOnlyList<string>? AddTags { get; set; }
    public IReadOnlyList<string>? RemoveTags { get; set; }
    public double? StartSec { get; set; }
    public double? EndSec { get; set; }
}

public sealed class NameCount
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class PlayStat
{
    public long TrackId { get; set; }
    public string Title { get; set; } = "";
    public int Plays { get; set; }
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public double PlayedMinutes { get; set; }
}

public sealed record DuplicateGroup(string SongTitle, string? StreamerName, IReadOnlyList<long> TrackIds);

public sealed class Suggestion
{
    public long Id { get; set; }
    public long TrackId { get; set; }
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Confidence { get; set; }
    public long CreatedAt { get; set; }
    public string State { get; set; } = "pending";
    public string? TrackTitle { get; set; }
}

/// <summary>曲库的全部读写操作。每个方法自己开关连接，线程安全。</summary>
public sealed class LibraryRepository(Database db)
{
    public Database Db { get; } = db;

    const string TrackViewSelect = """
        SELECT t.id, t.bvid, t.cid, t.page, t.part_title, t.duration_sec, t.start_sec, t.end_sec,
               t.song_title, t.streamer_id, s.name AS streamer_name, t.favorite, t.hidden, t.note,
               v.title AS video_title, v.up_mid, v.up_name, v.cover_url, v.status AS video_status, v.added_at,
               (SELECT COUNT(*) FROM tracks t2 WHERE t2.bvid = t.bvid) AS page_count,
               (SELECT COUNT(*) FROM play_events e WHERE e.track_id = t.id) AS play_count,
               (SELECT MAX(e.started_at) FROM play_events e WHERE e.track_id = t.id) AS last_played_at,
               (SELECT GROUP_CONCAT(g.name, ',') FROM track_tags tt JOIN tags g ON g.id = tt.tag_id
                 WHERE tt.track_id = t.id) AS tags
        FROM tracks t
        JOIN videos v ON v.bvid = t.bvid
        LEFT JOIN streamers s ON s.id = t.streamer_id
        """;

    // ---------------- 视频与曲目 ----------------

    public bool HasVideo(string bvid)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM videos WHERE bvid = @bvid", new { bvid }) > 0;
    }

    /// <summary>插入或更新视频基本信息（不会覆盖 added_at）。</summary>
    public void UpsertVideo(Video v)
    {
        using var c = Db.Open();
        UpsertVideo(c, null, v);
    }

    static void UpsertVideo(SqliteConnection c, SqliteTransaction? tx, Video v)
    {
        if (v.AddedAt == 0) v.AddedAt = Clock.Now();
        c.Execute("""
            INSERT INTO videos (bvid, title, up_mid, up_name, cover_url, description, pubdate, added_at, status)
            VALUES (@Bvid, @Title, @UpMid, @UpName, @CoverUrl, @Description, @Pubdate, @AddedAt, @Status)
            ON CONFLICT(bvid) DO UPDATE SET
              title = excluded.title, up_mid = excluded.up_mid, up_name = excluded.up_name,
              cover_url = excluded.cover_url, description = excluded.description,
              pubdate = excluded.pubdate, status = excluded.status
            """, v, tx);
    }

    /// <summary>
    /// 导入一个视频及其分P。已存在的分P只更新分P标题和时长，保留用户编辑过的元数据。
    /// 返回新建的曲目 id 列表。
    /// </summary>
    public IReadOnlyList<long> ImportVideo(Video v, IEnumerable<Track> pages, Func<Track, (string? song, string? streamer)>? guess = null)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        UpsertVideo(c, tx, v);
        var created = new List<long>();
        foreach (var p in pages)
        {
            var existing = c.ExecuteScalar<long?>(
                "SELECT id FROM tracks WHERE bvid = @Bvid AND cid = @Cid AND start_sec IS NULL", p, tx);
            if (existing is long id)
            {
                c.Execute("UPDATE tracks SET part_title = @PartTitle, duration_sec = @DurationSec, page = @Page WHERE id = @id",
                    new { p.PartTitle, p.DurationSec, p.Page, id }, tx);
                continue;
            }
            if (guess is not null && p.SongTitle is null && p.StreamerId is null)
            {
                var (song, streamer) = guess(p);
                p.SongTitle = song;
                if (!string.IsNullOrWhiteSpace(streamer)) p.StreamerId = GetOrCreateStreamer(c, tx, streamer!);
            }
            var newId = c.ExecuteScalar<long>("""
                INSERT INTO tracks (bvid, cid, page, part_title, duration_sec, song_title, streamer_id)
                VALUES (@Bvid, @Cid, @Page, @PartTitle, @DurationSec, @SongTitle, @StreamerId);
                SELECT last_insert_rowid();
                """, p, tx);
            created.Add(newId);
        }
        tx.Commit();
        return created;
    }

    public Video? GetVideo(string bvid)
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<Video>("SELECT * FROM videos WHERE bvid = @bvid", new { bvid });
    }

    public void SetVideoStatus(string bvid, string status)
    {
        using var c = Db.Open();
        c.Execute("UPDATE videos SET status = @status WHERE bvid = @bvid", new { bvid, status });
    }

    /// <summary>某个视频的全部曲目（按分P顺序）。</summary>
    public IReadOnlyList<TrackView> GetTracksByBvid(string bvid, bool includeHidden = false)
    {
        using var c = Db.Open();
        return c.Query<TrackView>(TrackViewSelect + " WHERE t.bvid = @bvid AND (@includeHidden OR t.hidden = 0) ORDER BY t.page, t.start_sec",
            new { bvid, includeHidden }).ToList();
    }

    /// <summary>曲库里出现过的切片 UP 主 mid。</summary>
    public IReadOnlySet<long> GetUploaderMids()
    {
        using var c = Db.Open();
        return c.Query<long>("SELECT DISTINCT up_mid FROM videos").ToHashSet();
    }

    public TrackView? GetTrack(long id)
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<TrackView>(TrackViewSelect + " WHERE t.id = @id", new { id });
    }

    public IReadOnlyList<TrackView> GetTracks(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return [];
        using var c = Db.Open();
        var rows = c.Query<TrackView>(TrackViewSelect + " WHERE t.id IN @list", new { list }).ToDictionary(r => r.Id);
        return list.Where(rows.ContainsKey).Select(i => rows[i]).ToList();
    }

    public IReadOnlyList<TrackView> QueryTracks(TrackQuery q)
    {
        var (where, args) = BuildWhere(q);
        var order = q.PlaylistId is not null ? "pi.position" : q.OrderBy switch
        {
            "added_asc" => "v.added_at, t.bvid, t.page",
            "plays_desc" => "play_count DESC, v.added_at DESC",
            "title" => "COALESCE(t.song_title, v.title) COLLATE NOCASE, t.page",
            "last_played_desc" => "last_played_at DESC NULLS LAST",
            _ => "v.added_at DESC, t.bvid, t.page",
        };
        var join = q.PlaylistId is not null ? " JOIN playlist_items pi ON pi.track_id = t.id AND pi.playlist_id = @PlaylistId" : "";
        var sql = TrackViewSelect + join + where + $" ORDER BY {order} LIMIT @Limit OFFSET @Offset";
        args.Add("Limit", q.Limit);
        args.Add("Offset", q.Offset);
        args.Add("PlaylistId", q.PlaylistId);
        using var c = Db.Open();
        return c.Query<TrackView>(sql, args).ToList();
    }

    public int CountTracks(TrackQuery q)
    {
        var (where, args) = BuildWhere(q);
        var join = q.PlaylistId is not null ? " JOIN playlist_items pi ON pi.track_id = t.id AND pi.playlist_id = @PlaylistId" : "";
        args.Add("PlaylistId", q.PlaylistId);
        using var c = Db.Open();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM tracks t JOIN videos v ON v.bvid = t.bvid LEFT JOIN streamers s ON s.id = t.streamer_id"
            + join + where, args);
    }

    static (string where, DynamicParameters args) BuildWhere(TrackQuery q)
    {
        var sb = new StringBuilder(" WHERE 1 = 1");
        var a = new DynamicParameters();
        if (!q.IncludeHidden) sb.Append(" AND t.hidden = 0");
        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            sb.Append("""
                 AND (v.title LIKE @kw ESCAPE '\' OR t.part_title LIKE @kw ESCAPE '\' OR t.song_title LIKE @kw ESCAPE '\'
                      OR s.name LIKE @kw ESCAPE '\' OR s.aliases LIKE @kw ESCAPE '\' OR v.up_name LIKE @kw ESCAPE '\'
                      OR t.note LIKE @kw ESCAPE '\' OR t.bvid = @kwRaw
                      OR EXISTS (SELECT 1 FROM track_tags tt JOIN tags g ON g.id = tt.tag_id
                                 WHERE tt.track_id = t.id AND g.name LIKE @kw ESCAPE '\'))
                """);
            a.Add("kw", "%" + EscapeLike(q.Keyword.Trim()) + "%");
            a.Add("kwRaw", q.Keyword.Trim());
        }
        if (q.StreamerId is not null) { sb.Append(" AND t.streamer_id = @sid"); a.Add("sid", q.StreamerId); }
        if (!string.IsNullOrWhiteSpace(q.StreamerName)) { sb.Append(" AND s.name = @sname COLLATE NOCASE"); a.Add("sname", q.StreamerName.Trim()); }
        if (q.UpMid is not null) { sb.Append(" AND v.up_mid = @upmid"); a.Add("upmid", q.UpMid); }
        if (!string.IsNullOrWhiteSpace(q.Tag))
        {
            sb.Append(" AND EXISTS (SELECT 1 FROM track_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.track_id = t.id AND g.name = @tag COLLATE NOCASE)");
            a.Add("tag", q.Tag.Trim());
        }
        if (q.Favorite is bool f) { sb.Append(" AND t.favorite = @fav"); a.Add("fav", f ? 1 : 0); }
        if (q.MissingSongTitle is bool ms) sb.Append(ms ? " AND (t.song_title IS NULL OR t.song_title = '')" : " AND t.song_title <> ''");
        if (q.MissingStreamer is bool mst) sb.Append(mst ? " AND t.streamer_id IS NULL" : " AND t.streamer_id IS NOT NULL");
        return (sb.ToString(), a);
    }

    static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>批量修改曲目。返回受影响的曲目数。</summary>
    public int UpdateTracks(IReadOnlyCollection<long> ids, TrackUpdate u)
    {
        if (ids.Count == 0) return 0;
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        long? streamerId = null;
        var clearStreamer = false;
        if (u.StreamerName is not null)
        {
            if (u.StreamerName.Trim().Length == 0) clearStreamer = true;
            else streamerId = GetOrCreateStreamer(c, tx, u.StreamerName.Trim());
        }
        var n = 0;
        foreach (var id in ids)
        {
            var sets = new List<string>();
            if (u.SongTitle is not null) sets.Add("song_title = NULLIF(@song, '')");
            if (streamerId is not null) sets.Add("streamer_id = @streamerId");
            if (clearStreamer) sets.Add("streamer_id = NULL");
            if (u.Favorite is not null) sets.Add("favorite = @fav");
            if (u.Hidden is not null) sets.Add("hidden = @hidden");
            if (u.Note is not null) sets.Add("note = NULLIF(@note, '')");
            if (u.StartSec is not null) sets.Add("start_sec = NULLIF(@start, -1)");
            if (u.EndSec is not null) sets.Add("end_sec = NULLIF(@end, -1)");
            var affected = 0;
            if (sets.Count > 0)
            {
                affected = c.Execute($"UPDATE tracks SET {string.Join(", ", sets)} WHERE id = @id", new
                {
                    id, song = u.SongTitle?.Trim(), streamerId, fav = u.Favorite == true ? 1 : 0,
                    hidden = u.Hidden == true ? 1 : 0, note = u.Note?.Trim(), start = u.StartSec, end = u.EndSec,
                }, tx);
            }
            else
            {
                affected = c.ExecuteScalar<int>("SELECT COUNT(*) FROM tracks WHERE id = @id", new { id }, tx);
            }
            if (affected == 0) continue;
            foreach (var tag in u.AddTags ?? [])
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var tagId = GetOrCreateTag(c, tx, tag.Trim());
                c.Execute("INSERT OR IGNORE INTO track_tags (track_id, tag_id) VALUES (@id, @tagId)", new { id, tagId }, tx);
            }
            foreach (var tag in u.RemoveTags ?? [])
            {
                c.Execute("DELETE FROM track_tags WHERE track_id = @id AND tag_id IN (SELECT id FROM tags WHERE name = @tag COLLATE NOCASE)",
                    new { id, tag = tag.Trim() }, tx);
            }
            n++;
        }
        tx.Commit();
        return n;
    }

    /// <summary>从曲库移除整个视频（及其曲目、播放记录）。</summary>
    public int DeleteVideo(string bvid)
    {
        using var c = Db.Open();
        return c.Execute("DELETE FROM videos WHERE bvid = @bvid", new { bvid });
    }

    // ---------------- 主播与标签 ----------------

    static long GetOrCreateStreamer(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        var id = c.ExecuteScalar<long?>("SELECT id FROM streamers WHERE name = @name COLLATE NOCASE", new { name }, tx);
        if (id is long existing) return existing;
        // 别名命中
        foreach (var s in c.Query<Streamer>("SELECT * FROM streamers WHERE aliases IS NOT NULL", transaction: tx))
        {
            var aliases = System.Text.Json.JsonSerializer.Deserialize<string[]>(s.Aliases!) ?? [];
            if (aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return s.Id;
        }
        return c.ExecuteScalar<long>("INSERT INTO streamers (name) VALUES (@name); SELECT last_insert_rowid();", new { name }, tx);
    }

    public long GetOrCreateStreamer(string name)
    {
        using var c = Db.Open();
        return GetOrCreateStreamer(c, null, name.Trim());
    }

    static long GetOrCreateTag(SqliteConnection c, SqliteTransaction? tx, string name)
    {
        var id = c.ExecuteScalar<long?>("SELECT id FROM tags WHERE name = @name COLLATE NOCASE", new { name }, tx);
        return id ?? c.ExecuteScalar<long>("INSERT INTO tags (name) VALUES (@name); SELECT last_insert_rowid();", new { name }, tx);
    }

    public IReadOnlyList<NameCount> ListStreamers()
    {
        using var c = Db.Open();
        return c.Query<NameCount>("""
            SELECT s.id, s.name, COUNT(t.id) AS count FROM streamers s
            LEFT JOIN tracks t ON t.streamer_id = s.id AND t.hidden = 0
            GROUP BY s.id ORDER BY count DESC, s.name
            """).ToList();
    }

    public IReadOnlyList<Streamer> GetStreamers()
    {
        using var c = Db.Open();
        return c.Query<Streamer>("SELECT * FROM streamers ORDER BY name").ToList();
    }

    public IReadOnlyList<NameCount> ListUploaders()
    {
        using var c = Db.Open();
        return c.Query<NameCount>("""
            SELECT v.up_mid AS id, MAX(v.up_name) AS name, COUNT(t.id) AS count FROM videos v
            JOIN tracks t ON t.bvid = v.bvid AND t.hidden = 0
            GROUP BY v.up_mid ORDER BY count DESC
            """).ToList();
    }

    public IReadOnlyList<NameCount> ListTags()
    {
        using var c = Db.Open();
        return c.Query<NameCount>("""
            SELECT g.id, g.name, COUNT(tt.track_id) AS count FROM tags g
            LEFT JOIN track_tags tt ON tt.tag_id = g.id
            GROUP BY g.id ORDER BY count DESC, g.name
            """).ToList();
    }

    /// <summary>把 fromIds 这些主播合并进 intoId：曲目改挂过去，名字记为别名。</summary>
    public int MergeStreamers(long intoId, IReadOnlyCollection<long> fromIds)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var into = c.QuerySingle<Streamer>("SELECT * FROM streamers WHERE id = @intoId", new { intoId }, tx);
        var aliases = new List<string>(into.Aliases is null ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(into.Aliases) ?? []);
        var moved = 0;
        foreach (var from in fromIds.Where(f => f != intoId))
        {
            var s = c.QueryFirstOrDefault<Streamer>("SELECT * FROM streamers WHERE id = @from", new { from }, tx);
            if (s is null) continue;
            aliases.Add(s.Name);
            if (s.Aliases is not null) aliases.AddRange(System.Text.Json.JsonSerializer.Deserialize<string[]>(s.Aliases) ?? []);
            moved += c.Execute("UPDATE tracks SET streamer_id = @intoId WHERE streamer_id = @from", new { intoId, from }, tx);
            c.Execute("DELETE FROM streamers WHERE id = @from", new { from }, tx);
        }
        var json = System.Text.Json.JsonSerializer.Serialize(aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        c.Execute("UPDATE streamers SET aliases = @json WHERE id = @intoId", new { json, intoId }, tx);
        tx.Commit();
        return moved;
    }

    public void SetStreamerAliases(long id, IEnumerable<string> aliases)
    {
        using var c = Db.Open();
        var json = System.Text.Json.JsonSerializer.Serialize(aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToArray());
        c.Execute("UPDATE streamers SET aliases = @json WHERE id = @id", new { json, id });
    }

    /// <summary>歌名相同（忽略大小写和空白）的曲目分组。</summary>
    public IReadOnlyList<DuplicateGroup> FindDuplicates()
    {
        using var c = Db.Open();
        var rows = c.Query<(long id, string song, string? streamer)>("""
            SELECT t.id, t.song_title, s.name FROM tracks t LEFT JOIN streamers s ON s.id = t.streamer_id
            WHERE t.song_title IS NOT NULL AND t.song_title <> '' AND t.hidden = 0
            """);
        return rows
            .GroupBy(r => (Song: Normalize(r.song), Streamer: r.streamer?.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.First().song, g.First().streamer, g.Select(x => x.id).ToList()))
            .ToList();

        static string Normalize(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
    }

    // ---------------- 歌单 ----------------

    public IReadOnlyList<Playlist> ListPlaylists()
    {
        using var c = Db.Open();
        return c.Query<Playlist>("""
            SELECT p.id, p.name, p.created_at, COUNT(pi.track_id) AS item_count
            FROM playlists p LEFT JOIN playlist_items pi ON pi.playlist_id = p.id
            GROUP BY p.id ORDER BY p.created_at
            """).ToList();
    }

    public long CreatePlaylist(string name)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long>("INSERT INTO playlists (name, created_at) VALUES (@name, @now); SELECT last_insert_rowid();",
            new { name = name.Trim(), now = Clock.Now() });
    }

    public void RenamePlaylist(long id, string name)
    {
        using var c = Db.Open();
        c.Execute("UPDATE playlists SET name = @name WHERE id = @id", new { id, name = name.Trim() });
    }

    public void DeletePlaylist(long id)
    {
        using var c = Db.Open();
        c.Execute("DELETE FROM playlists WHERE id = @id", new { id });
    }

    /// <summary>追加到歌单末尾，已在歌单里的曲目跳过。返回实际添加数。</summary>
    public int AddToPlaylist(long playlistId, IEnumerable<long> trackIds)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query<long>("SELECT track_id FROM playlist_items WHERE playlist_id = @playlistId", new { playlistId }, tx).ToHashSet();
        var pos = c.ExecuteScalar<int?>("SELECT MAX(position) FROM playlist_items WHERE playlist_id = @playlistId", new { playlistId }, tx) ?? -1;
        var n = 0;
        foreach (var t in trackIds)
        {
            if (!existing.Add(t)) continue;
            c.Execute("INSERT INTO playlist_items (playlist_id, track_id, position) VALUES (@playlistId, @t, @pos)",
                new { playlistId, t, pos = ++pos }, tx);
            n++;
        }
        tx.Commit();
        return n;
    }

    public int RemoveFromPlaylist(long playlistId, IEnumerable<long> trackIds)
    {
        var remove = trackIds.ToHashSet();
        var keep = GetPlaylistTrackIds(playlistId).Where(t => !remove.Contains(t)).ToList();
        var before = GetPlaylistTrackIds(playlistId).Count;
        SetPlaylistOrder(playlistId, keep);
        return before - keep.Count;
    }

    public IReadOnlyList<long> GetPlaylistTrackIds(long playlistId)
    {
        using var c = Db.Open();
        return c.Query<long>("SELECT track_id FROM playlist_items WHERE playlist_id = @playlistId ORDER BY position", new { playlistId }).ToList();
    }

    /// <summary>用给定顺序整体替换歌单内容。</summary>
    public void SetPlaylistOrder(long playlistId, IReadOnlyList<long> trackIds)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM playlist_items WHERE playlist_id = @playlistId", new { playlistId }, tx);
        var pos = 0;
        foreach (var t in trackIds.Distinct())
            c.Execute("INSERT INTO playlist_items (playlist_id, track_id, position) VALUES (@playlistId, @t, @pos)", new { playlistId, t, pos = pos++ }, tx);
        tx.Commit();
    }

    // ---------------- 订阅源 ----------------

    public long UpsertSource(string kind, string remoteId, string? title)
    {
        using var c = Db.Open();
        c.Execute("""
            INSERT INTO sources (kind, remote_id, title) VALUES (@kind, @remoteId, @title)
            ON CONFLICT(kind, remote_id) DO UPDATE SET title = COALESCE(excluded.title, sources.title)
            """, new { kind, remoteId, title });
        return c.ExecuteScalar<long>("SELECT id FROM sources WHERE kind = @kind AND remote_id = @remoteId", new { kind, remoteId });
    }

    public void LinkSourceVideo(long sourceId, string bvid)
    {
        using var c = Db.Open();
        c.Execute("INSERT OR IGNORE INTO source_videos (source_id, bvid) VALUES (@sourceId, @bvid)", new { sourceId, bvid });
    }

    public void MarkSourceSynced(long sourceId)
    {
        using var c = Db.Open();
        c.Execute("UPDATE sources SET last_synced = @now WHERE id = @sourceId", new { sourceId, now = Clock.Now() });
    }

    public IReadOnlyList<Source> ListSources()
    {
        using var c = Db.Open();
        return c.Query<Source>("""
            SELECT s.*, (SELECT COUNT(*) FROM source_videos sv WHERE sv.source_id = s.id) AS video_count
            FROM sources s ORDER BY s.id
            """).ToList();
    }

    public Source? GetSource(long id)
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<Source>("SELECT s.*, (SELECT COUNT(*) FROM source_videos sv WHERE sv.source_id = s.id) AS video_count FROM sources s WHERE s.id = @id", new { id });
    }

    public void DeleteSource(long id)
    {
        using var c = Db.Open();
        c.Execute("DELETE FROM sources WHERE id = @id", new { id });
    }

    // ---------------- 播放记录 ----------------

    public void AddPlayEvent(PlayEvent e)
    {
        using var c = Db.Open();
        c.Execute("""
            INSERT INTO play_events (track_id, started_at, played_sec, completed, skipped, mode, context, recommender, reason)
            VALUES (@TrackId, @StartedAt, @PlayedSec, @Completed, @Skipped, @Mode, @Context, @Recommender, @Reason)
            """, e);
    }

    // ---------------- 推荐用数据 ----------------

    /// <summary>每首未隐藏曲目的打分特征。评分：听完 +1，30 秒内跳过 −1，其余按听的比例映射到 −1..1。</summary>
    public IReadOnlyList<TrackFeatures> GetRecommendFeatures()
    {
        using var c = Db.Open();
        return c.Query<TrackFeatures>("""
            SELECT t.id, t.bvid, COALESCE(t.song_title, v.title) AS title, t.song_title, t.streamer_id, s.name AS streamer_name,
                   v.up_mid, v.up_name, t.favorite, v.added_at,
                   (SELECT GROUP_CONCAT(g.name, ',') FROM track_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.track_id = t.id) AS tags,
                   COUNT(e.id) AS plays,
                   CAST(COALESCE(SUM(CASE
                       WHEN e.completed = 1 THEN 1.0
                       WHEN e.skipped = 1 THEN -1.0
                       ELSE MAX(-1.0, MIN(1.0, 2.0 * e.played_sec /
                            MAX(COALESCE(t.end_sec - COALESCE(t.start_sec, 0), t.duration_sec, 1), 1) - 1.0))
                   END), 0.0) AS REAL) AS rating_sum,
                   MAX(e.started_at) AS last_played_at
            FROM tracks t
            JOIN videos v ON v.bvid = t.bvid
            LEFT JOIN streamers s ON s.id = t.streamer_id
            LEFT JOIN play_events e ON e.track_id = t.id
            WHERE t.hidden = 0 AND v.status = 'ok'
            GROUP BY t.id
            """).ToList();
    }

    /// <summary>最近的播放，最新的在前。</summary>
    public IReadOnlyList<RecentPlay> GetRecentPlays(int limit)
    {
        using var c = Db.Open();
        return c.Query<RecentPlay>("""
            SELECT e.track_id, t.streamer_id, t.song_title, e.completed, e.skipped, e.started_at,
                   (SELECT GROUP_CONCAT(g.name, ',') FROM track_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.track_id = t.id) AS tags
            FROM play_events e JOIN tracks t ON t.id = e.track_id
            ORDER BY e.started_at DESC, e.id DESC LIMIT @limit
            """, new { limit }).ToList();
    }

    public IReadOnlyList<long> GetVisibleTrackIds()
    {
        using var c = Db.Open();
        return c.Query<long>("SELECT t.id FROM tracks t JOIN videos v ON v.bvid = t.bvid WHERE t.hidden = 0 AND v.status = 'ok'").ToList();
    }

    /// <summary>按推荐器统计：播放次数、完播率、跳过率。</summary>
    public IReadOnlyList<RecommenderStat> GetRecommenderStats(long? sinceUnix)
    {
        using var c = Db.Open();
        return c.Query<RecommenderStat>("""
            SELECT COALESCE(recommender, '（旧记录）') AS recommender, COUNT(*) AS plays,
                   SUM(completed) AS completed, SUM(skipped) AS skipped, ROUND(AVG(played_sec), 1) AS avg_played_sec
            FROM play_events WHERE (@sinceUnix IS NULL OR started_at >= @sinceUnix)
            GROUP BY recommender ORDER BY plays DESC
            """, new { sinceUnix }).ToList();
    }

    // ---------------- B 站发现池 ----------------

    /// <summary>加入发现池，已经存在（任何状态）的跳过。返回新加入数。</summary>
    public int AddDiscovery(IEnumerable<DiscoveryItem> items)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var n = 0;
        foreach (var i in items)
        {
            if (i.FoundAt == 0) i.FoundAt = Clock.Now();
            n += c.Execute("""
                INSERT OR IGNORE INTO discovery (bvid, title, uploader, uploader_mid, duration_sec, play_count, pubdate, cover_url, streamer, query, found_at)
                VALUES (@Bvid, @Title, @Uploader, @UploaderMid, @DurationSec, @PlayCount, @Pubdate, @CoverUrl, @Streamer, @Query, @FoundAt)
                """, i, tx);
        }
        tx.Commit();
        return n;
    }

    public bool IsKnownDiscovery(string bvid)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM discovery WHERE bvid = @bvid", new { bvid }) > 0;
    }

    /// <summary>可推荐的发现候选（state = new 且还不在曲库里）。</summary>
    public IReadOnlyList<DiscoveryItem> GetDiscoveryCandidates(int limit = 500)
    {
        using var c = Db.Open();
        return c.Query<DiscoveryItem>("""
            SELECT d.* FROM discovery d
            WHERE d.state = 'new' AND NOT EXISTS (SELECT 1 FROM videos v WHERE v.bvid = d.bvid)
            ORDER BY d.found_at DESC LIMIT @limit
            """, new { limit }).ToList();
    }

    public void SetDiscoveryState(string bvid, string state)
    {
        using var c = Db.Open();
        c.Execute("UPDATE discovery SET state = @state, state_at = @now WHERE bvid = @bvid", new { bvid, state, now = Clock.Now() });
    }

    /// <summary>清理超过 days 天还没被推荐的候选（被拒绝的保留，避免再次入池）。</summary>
    public int PruneDiscovery(int days)
    {
        using var c = Db.Open();
        return c.Execute("DELETE FROM discovery WHERE state = 'new' AND found_at < @cut",
            new { cut = Clock.Now() - days * 86400L });
    }

    public string? GetMeta(string key)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = @key", new { key });
    }

    public void SetMeta(string key, string? value)
    {
        using var c = Db.Open();
        c.Execute("INSERT INTO meta (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value });
    }

    public IReadOnlyList<long> RecentlyPlayed(int limit)
    {
        using var c = Db.Open();
        return c.Query<long>("SELECT track_id FROM play_events ORDER BY started_at DESC, id DESC LIMIT @limit", new { limit }).ToList();
    }

    public IReadOnlyList<PlayStat> GetPlayStats(long? sinceUnix, int limit)
    {
        using var c = Db.Open();
        return c.Query<PlayStat>("""
            SELECT e.track_id, COALESCE(t.song_title, v.title) AS title, COUNT(*) AS plays,
                   SUM(e.completed) AS completed, SUM(e.skipped) AS skipped, ROUND(SUM(e.played_sec) / 60.0, 1) AS played_minutes
            FROM play_events e JOIN tracks t ON t.id = e.track_id JOIN videos v ON v.bvid = t.bvid
            WHERE (@sinceUnix IS NULL OR e.started_at >= @sinceUnix)
            GROUP BY e.track_id ORDER BY plays DESC, played_minutes DESC LIMIT @limit
            """, new { sinceUnix, limit }).ToList();
    }

    // ---------------- 待确认建议 ----------------

    public long AddSuggestion(long trackId, string field, string value, string source, double? confidence)
    {
        using var c = Db.Open();
        // 同一曲目同一字段只保留最新一条待确认
        c.Execute("DELETE FROM suggestions WHERE track_id = @trackId AND field = @field AND state = 'pending' AND (@field <> 'tag' OR value = @value)",
            new { trackId, field, value });
        return c.ExecuteScalar<long>("""
            INSERT INTO suggestions (track_id, field, value, source, confidence, created_at)
            VALUES (@trackId, @field, @value, @source, @confidence, @now); SELECT last_insert_rowid();
            """, new { trackId, field, value, source, confidence, now = Clock.Now() });
    }

    public IReadOnlyList<Suggestion> ListPendingSuggestions()
    {
        using var c = Db.Open();
        return c.Query<Suggestion>("""
            SELECT g.*, COALESCE(t.song_title, v.title) AS track_title FROM suggestions g
            JOIN tracks t ON t.id = g.track_id JOIN videos v ON v.bvid = t.bvid
            WHERE g.state = 'pending' ORDER BY g.track_id, g.field
            """).ToList();
    }

    /// <summary>采纳或拒绝建议。采纳时把值写入曲目。</summary>
    public void ResolveSuggestions(IEnumerable<long> ids, bool accept)
    {
        foreach (var id in ids)
        {
            Suggestion? s;
            using (var c = Db.Open())
                s = c.QueryFirstOrDefault<Suggestion>("SELECT * FROM suggestions WHERE id = @id AND state = 'pending'", new { id });
            if (s is null) continue;
            if (accept)
            {
                var u = s.Field switch
                {
                    "song_title" => new TrackUpdate { SongTitle = s.Value },
                    "streamer" => new TrackUpdate { StreamerName = s.Value },
                    "tag" => new TrackUpdate { AddTags = [s.Value] },
                    _ => null,
                };
                if (u is not null) UpdateTracks([s.TrackId], u);
            }
            using var c2 = Db.Open();
            c2.Execute("UPDATE suggestions SET state = @state WHERE id = @id", new { id, state = accept ? "accepted" : "rejected" });
        }
    }
}
