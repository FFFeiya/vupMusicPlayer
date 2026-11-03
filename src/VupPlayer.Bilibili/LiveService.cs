using VupPlayer.Core;
using VupPlayer.Core.Data;
using VupPlayer.Core.Streams;

namespace VupPlayer.Bilibili;

/// <summary>一轮直播状态轮询的结果。</summary>
public sealed record LivePollResult(int Checked, IReadOnlyList<LiveChange> Changes, string? Error)
{
    public IEnumerable<LiveChange> ToNotify => Changes.Where(c => c.Notify);
}

/// <summary>解析好的直播流。Stream 直接复用曲库那套多线路结构，卡住换线路的逻辑原样可用。</summary>
public sealed record ResolvedLive(
    long RoomId,
    LiveStatus Status,
    ResolvedStream Stream,
    int CurrentQn,
    IReadOnlyList<(int Qn, string Desc)> Qualities,
    IReadOnlyList<LiveLine> Lines)
{
    public string QualityName => Qualities.FirstOrDefault(q => q.Qn == CurrentQn).Desc ?? $"qn {CurrentQn}";
}

/// <summary>
/// 直播订阅与取流。轮询用一次批量请求查完所有订阅的主播，不要每人一个请求。
/// 不负责弹通知——那是 App 层的事，这里只给出「谁刚开播」。
/// </summary>
public sealed class LiveService(BiliApi api, LibraryRepository repo)
{
    /// <summary>本进程是否已经轮询过一轮。第一轮只落库不推送（见 LiveTransitions.Diff）。</summary>
    public bool HasPolled { get; private set; }

    /// <summary>轮询一次所有订阅的直播间，落库并返回状态跳变。</summary>
    public async Task<LivePollResult> PollAsync(CancellationToken ct = default)
    {
        var known = repo.ListLiveRooms();
        if (known.Count == 0)
        {
            HasPolled = true;
            return new LivePollResult(0, [], null);
        }

        IReadOnlyList<LiveSnapshot> snapshots;
        try
        {
            snapshots = await api.GetLiveStatusAsync(known.Select(r => r.Mid).ToList(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 轮询失败不改动已存状态：否则下次会把「还在播」误判成刚开播，重复推送
            return new LivePollResult(0, [], ex.Message);
        }

        var byMid = snapshots.ToDictionary(s => s.Mid);
        var changes = LiveTransitions.Diff(known, byMid, firstPoll: !HasPolled, Clock.Now());
        repo.SaveLiveSnapshots(snapshots);
        HasPolled = true;
        foreach (var c in changes.Where(c => c.Notify)) repo.MarkLiveNotified(c.Room.Mid);
        return new LivePollResult(snapshots.Count, changes, null);
    }

    /// <summary>取直播流。没在播或拿不到线路时抛 StreamResolveException。</summary>
    public async Task<ResolvedLive> ResolveAsync(long roomId, int qn = BiliApi.QnOriginal, CancellationToken ct = default)
    {
        LivePlayInfo info;
        try
        {
            info = await api.GetRoomPlayUrlAsync(roomId, qn, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StreamResolveException("取直播流失败：" + ex.Message);
        }
        if (info.Status != LiveStatus.Live)
            throw new StreamResolveException(info.Status == LiveStatus.Replay ? "主播正在轮播（放录像），没有直播流" : "主播没有在直播");
        if (info.Lines.Count == 0)
            throw new StreamResolveException("拿不到直播流地址，可能是付费直播或需要登录");

        var urls = info.Lines.Select(l => l.Url).ToList();
        // 直播是音视频合一的一条流：VideoUrl == AudioUrl，正好落在 MpvPlayer 已有的分支上；
        // 纯音频模式对同一个地址加 vid=no 即可。
        var stream = new ResolvedStream(
            urls[0], urls[0], BiliApi.LiveHeaders,
            // 直播地址比视频直链短命得多，而且主播切流会立刻失效，所以不缓存、随时可重取
            DateTimeOffset.UtcNow.AddMinutes(10),
            "B站直播",
            info.Qualities.FirstOrDefault(q => q.Qn == info.CurrentQn).Desc,
            urls, urls);
        return new ResolvedLive(info.RoomId, info.Status, stream, info.CurrentQn, info.Qualities, info.Lines);
    }

    /// <summary>查一个房间当前的状态（看播时确认主播是否已下播）。</summary>
    public async Task<LiveSnapshot?> GetRoomAsync(long roomId, CancellationToken ct = default)
    {
        try { return await api.GetRoomAsync(roomId, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>把曲库里有 UID 的主播加进订阅，返回新加入数。</summary>
    public int SyncFromStreamers() => repo.SyncLiveRoomsFromStreamers();

    /// <summary>
    /// 手动添加一个直播间（粘贴的是房间号）。会查出主播 UID 和昵称，
    /// 并和曲库里 UID 相同的主播关联起来，这样列表上显示的就是曲库里的主播名。
    /// </summary>
    public async Task<LiveRoom?> AddByRoomIdAsync(long roomId, CancellationToken ct = default)
    {
        var snap = await api.GetRoomAsync(roomId, ct).ConfigureAwait(false);
        if (snap is null) return null;
        return await AddAsync(snap, ct).ConfigureAwait(false);
    }

    /// <summary>手动添加一个直播间（粘贴的是空间链接 / UID）。</summary>
    public async Task<LiveRoom?> AddByMidAsync(long mid, CancellationToken ct = default)
    {
        var list = await api.GetLiveStatusAsync([mid], ct).ConfigureAwait(false);
        return list.Count == 0 ? null : await AddAsync(list[0], ct).ConfigureAwait(false);
    }

    async Task<LiveRoom?> AddAsync(LiveSnapshot snap, CancellationToken ct)
    {
        var uname = snap.Uname;
        if (string.IsNullOrWhiteSpace(uname))
        {
            try { uname = await api.GetUserNameAsync(snap.Mid, ct).ConfigureAwait(false) ?? ""; }
            catch { uname = ""; }
        }
        var streamerId = repo.GetStreamers().FirstOrDefault(s => s.Mid == snap.Mid)?.Id;
        repo.UpsertLiveRoom(snap.Mid, snap.RoomId, uname, streamerId);
        repo.SaveLiveSnapshots([snap with { Uname = uname }]);
        return repo.GetLiveRoom(snap.Mid);
    }
}
