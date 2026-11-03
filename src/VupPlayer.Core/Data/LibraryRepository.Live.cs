using Dapper;

namespace VupPlayer.Core.Data;

/// <summary>直播间订阅与看播记录。</summary>
public sealed partial class LibraryRepository
{
    const string LiveRoomSelect = """
        SELECT r.mid, r.room_id, r.uname, r.streamer_id, r.notify, r.last_status, r.last_title, r.last_cover,
               r.last_area, r.online, r.live_since, r.checked_at, r.notified_at, r.added_at,
               s.name AS streamer_name,
               (SELECT COUNT(*) FROM tracks t WHERE t.streamer_id = r.streamer_id AND t.hidden = 0) AS track_count
        FROM live_rooms r
        LEFT JOIN streamers s ON s.id = r.streamer_id
        """;

    /// <summary>所有订阅的直播间：在播的排前面，然后按曲库里的切片数排。</summary>
    public IReadOnlyList<LiveRoom> ListLiveRooms()
    {
        using var c = Db.Open();
        return c.Query<LiveRoom>($"""
            {LiveRoomSelect}
            ORDER BY (r.last_status = 1) DESC, track_count DESC, COALESCE(s.name, r.uname)
            """).ToList();
    }

    public LiveRoom? GetLiveRoom(long mid)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<LiveRoom>($"{LiveRoomSelect} WHERE r.mid = @mid", new { mid });
    }

    public LiveRoom? GetLiveRoomByRoomId(long roomId)
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<LiveRoom>($"{LiveRoomSelect} WHERE r.room_id = @roomId", new { roomId });
    }

    public int CountLive()
    {
        using var c = Db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM live_rooms WHERE last_status = 1");
    }

    /// <summary>
    /// 加入或更新一个订阅的直播间。已存在时只补 room_id / uname / streamer_id，
    /// 不动 notify 和轮询状态（否则会把用户关掉的提醒重新打开、或把状态清成未开播导致重复推送）。
    /// </summary>
    public void UpsertLiveRoom(long mid, long roomId, string? uname, long? streamerId, bool notify = true)
    {
        using var c = Db.Open();
        c.Execute("""
            INSERT INTO live_rooms (mid, room_id, uname, streamer_id, notify, added_at)
            VALUES (@mid, @roomId, @uname, @streamerId, @notify, @now)
            ON CONFLICT(mid) DO UPDATE SET
              room_id     = excluded.room_id,
              uname       = COALESCE(excluded.uname, live_rooms.uname),
              streamer_id = COALESCE(excluded.streamer_id, live_rooms.streamer_id)
            """, new { mid, roomId, uname, streamerId, notify = notify ? 1 : 0, now = Clock.Now() });
    }

    /// <summary>把曲库里有 UID 的主播都加进直播订阅，返回新加入的数量。</summary>
    public int SyncLiveRoomsFromStreamers()
    {
        using var c = Db.Open();
        // room_id 先占 0，等第一次轮询拿到真实房间号再补
        return c.Execute("""
            INSERT INTO live_rooms (mid, room_id, uname, streamer_id, notify, added_at)
            SELECT s.mid, 0, s.name, s.id, 1, @now
            FROM streamers s
            WHERE s.mid IS NOT NULL AND s.mid > 0
              AND NOT EXISTS (SELECT 1 FROM live_rooms r WHERE r.mid = s.mid)
            """, new { now = Clock.Now() });
    }

    public void SetLiveNotify(long mid, bool notify)
    {
        using var c = Db.Open();
        c.Execute("UPDATE live_rooms SET notify = @n WHERE mid = @mid", new { mid, n = notify ? 1 : 0 });
    }

    public void DeleteLiveRoom(long mid)
    {
        using var c = Db.Open();
        c.Execute("DELETE FROM live_rooms WHERE mid = @mid", new { mid });
    }

    /// <summary>
    /// 落库一轮轮询的结果。只更新已订阅的房间，不会自动新增。
    /// live_since 只在本场开播时写入：主播一直在播的话，接口返回的 live_time 不变，不要每轮都覆盖。
    /// </summary>
    public void SaveLiveSnapshots(IEnumerable<LiveSnapshot> snapshots)
    {
        var list = snapshots.ToList();
        if (list.Count == 0) return;
        var now = Clock.Now();
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var s in list)
        {
            c.Execute("""
                UPDATE live_rooms SET
                  room_id     = CASE WHEN @RoomId > 0 THEN @RoomId ELSE room_id END,
                  uname       = COALESCE(NULLIF(@Uname, ''), uname),
                  last_status = @Status,
                  last_title  = @Title,
                  last_cover  = COALESCE(NULLIF(@Cover, ''), last_cover),
                  last_area   = COALESCE(NULLIF(@AreaName, ''), last_area),
                  online      = @Online,
                  live_since  = CASE WHEN @Status = 1 THEN NULLIF(@LiveTime, 0) ELSE NULL END,
                  checked_at  = @now
                WHERE mid = @Mid
                """, new
            {
                s.Mid, s.RoomId, s.Uname, Status = (int)s.Status, s.Title, s.Cover, s.AreaName, s.Online, s.LiveTime, now,
            }, tx);
        }
        tx.Commit();
    }

    public void MarkLiveNotified(long mid)
    {
        using var c = Db.Open();
        c.Execute("UPDATE live_rooms SET notified_at = @now WHERE mid = @mid", new { mid, now = Clock.Now() });
    }

    // ---------------- 看播记录 ----------------

    public void AddLiveEvent(LiveWatchEvent e)
    {
        using var c = Db.Open();
        c.Execute("""
            INSERT INTO live_events (room_id, mid, title, started_at, watched_sec, mode)
            VALUES (@RoomId, @Mid, @Title, @StartedAt, @WatchedSec, @Mode)
            """, e);
    }

    /// <summary>某个房间累计看了多久（秒）。</summary>
    public double LiveWatchedSec(long roomId)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<double?>("SELECT SUM(watched_sec) FROM live_events WHERE room_id = @roomId", new { roomId }) ?? 0;
    }
}
