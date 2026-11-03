using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using VupPlayer.Bilibili;
using VupPlayer.Core;
using VupPlayer.Core.Control;
using VupPlayer.Core.Data;

namespace VupPlayer.Mcp;

/// <summary>
/// 直播工具。查开播状态和管理订阅不需要播放器运行（直接访问 B 站 + 曲库）；
/// 看直播需要播放器在运行。
/// </summary>
[McpServerToolType]
public sealed class LiveTools(LibraryRepository repo)
{
    LiveService Service => new(BiliTools.Api.Value, repo);

    [McpServerTool(Name = "live_status", ReadOnly = true, OpenWorld = true)]
    [Description("""
        查订阅的主播谁在直播。live=true 表示正在直播；status=Replay 是轮播（在放录像），不是真的开播。
        refresh=true 会去 B 站查最新状态（一次请求查完所有人），false 只读本地上次轮询的结果。
        """)]
    public async Task<string> LiveStatus([Description("是否去 B 站查最新状态")] bool refresh = true)
    {
        if (refresh)
        {
            var poll = await Service.PollAsync();
            if (poll.Error is not null)
                return LibraryTools.Out(new { ok = false, error = "查开播状态失败：" + poll.Error });
        }
        var rooms = repo.ListLiveRooms();
        return LibraryTools.Out(new
        {
            live_count = rooms.Count(r => r.IsLive),
            rooms = rooms.Select(Row),
        });
    }

    [McpServerTool(Name = "watch_live")]
    [Description("""
        在播放器里开始看某个主播的直播。可以传 streamer（主播名，会在订阅列表里模糊匹配）、room_id 或 mid。
        需要播放器在运行。想只听声音，之后用 player_control 的 stream_mode=Audio。
        """)]
    public async Task<string> WatchLive(
        [Description("主播名，例如「阿梓」")] string? streamer = null,
        [Description("直播间房间号")] long? roomId = null,
        [Description("主播 UID")] long? mid = null)
    {
        var args = new JsonObject();
        if (streamer is { Length: > 0 }) args["streamer"] = streamer;
        if (roomId is long r) args["room_id"] = r;
        if (mid is long m) args["mid"] = m;
        if (args.Count == 0) return LibraryTools.Out(new { ok = false, error = "要给出 streamer、room_id 或 mid" });
        return await Send("watch_live", args);
    }

    [McpServerTool(Name = "stop_live")]
    [Description("离开直播。之后播放器会停下来，可以用 play / player_control 继续放曲库。需要播放器在运行。")]
    public Task<string> StopLive() => Send("stop_live", new JsonObject());

    [McpServerTool(Name = "subscribe_live", OpenWorld = true)]
    [Description("""
        添加一个要关注开播的直播间（开播时播放器会弹 Windows 通知）。传 room_id 或 mid。
        from_library=true 时把曲库里所有有 UID 的主播一次性加进来。
        """)]
    public async Task<string> SubscribeLive(
        [Description("直播间房间号")] long? roomId = null,
        [Description("主播 UID")] long? mid = null,
        [Description("把曲库里有 UID 的主播全部加入")] bool fromLibrary = false)
    {
        var svc = Service;
        if (fromLibrary)
        {
            var n = svc.SyncFromStreamers();
            await svc.PollAsync();
            return LibraryTools.Out(new { added = n, rooms = repo.ListLiveRooms().Select(Row) });
        }
        LiveRoom? room;
        if (roomId is long r) room = await svc.AddByRoomIdAsync(r);
        else if (mid is long m) room = await svc.AddByMidAsync(m);
        else return LibraryTools.Out(new { ok = false, error = "要给出 room_id 或 mid，或者设 from_library=true" });
        return room is null
            ? LibraryTools.Out(new { ok = false, error = "找不到这个直播间" })
            : LibraryTools.Out(Row(room));
    }

    [McpServerTool(Name = "set_live_notify", Idempotent = true)]
    [Description("打开或关掉某个主播的开播提醒。主播仍然留在订阅列表里，只是不弹通知。")]
    public string SetLiveNotify([Description("主播 UID")] long mid, [Description("true 开启提醒")] bool notify)
    {
        if (repo.GetLiveRoom(mid) is null) return LibraryTools.Out(new { ok = false, error = "订阅列表里没有这个 UID" });
        repo.SetLiveNotify(mid, notify);
        return LibraryTools.Out(Row(repo.GetLiveRoom(mid)!));
    }

    [McpServerTool(Name = "unsubscribe_live", Idempotent = true)]
    [Description("不再关注某个主播的开播。曲库里的切片不会被删除。")]
    public string UnsubscribeLive([Description("主播 UID")] long mid)
    {
        repo.DeleteLiveRoom(mid);
        return LibraryTools.Out(new { removed = mid });
    }

    static object Row(LiveRoom r) => new
    {
        r.Mid,
        room_id = r.RoomId,
        streamer = r.DisplayName,
        bilibili_name = r.Uname,
        live = r.IsLive,
        status = r.LastStatus.ToString(),
        title = r.LastTitle,
        area = r.LastArea,
        r.Online,
        live_since = r.LiveSince,
        r.Notify,
        track_count = r.TrackCount,
        r.Url,
    };

    static async Task<string> Send(string cmd, JsonObject args)
    {
        var resp = await ControlClient.SendAsync(cmd, args, 15000);
        if (resp is null) return LibraryTools.Out(new { ok = false, error = "播放器没有运行。请先打开 VupPlayer。" });
        return resp.Ok ? resp.Data?.ToJsonString() ?? "{}" : LibraryTools.Out(new { ok = false, error = resp.Error });
    }
}
