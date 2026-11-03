namespace VupPlayer.Core;

/// <summary>一个直播间的状态跳变。</summary>
/// <param name="Notify">这次跳变要不要弹开播通知。</param>
public sealed record LiveChange(LiveRoom Room, LiveSnapshot Snapshot, LiveStatus From, LiveStatus To, bool Notify)
{
    public bool WentLive => From != LiveStatus.Live && To == LiveStatus.Live;
    public bool WentOffline => From == LiveStatus.Live && To != LiveStatus.Live;
}

/// <summary>
/// 把轮询结果和已存状态对比，算出哪些房间刚开播、哪些刚下播、哪些要推送。
/// 纯函数，不碰网络也不碰数据库，方便测试。
/// </summary>
public static class LiveTransitions
{
    /// <summary>同一个房间多久之内不重复推送。主播网络不好反复断流时，不至于连环弹通知。</summary>
    public const long NotifyCooldownSec = 30 * 60;

    /// <summary>
    /// 规则：
    /// 1. 只有「非直播 → 直播(1)」算开播；轮播(2) 是在放录像，不推送也不算开播。
    /// 2. firstPoll（进程启动后的第一轮）只落库不推送，否则一开机就把所有在播的人全弹一遍。
    ///    注意状态是存库的，所以重启后仍然只会推真正新开播的人。
    /// 3. 关掉了 Notify 的房间不推送，但状态照常更新。
    /// 4. 距上次推送不到 NotifyCooldownSec 的不重复推。
    /// </summary>
    public static IReadOnlyList<LiveChange> Diff(
        IEnumerable<LiveRoom> known,
        IReadOnlyDictionary<long, LiveSnapshot> snapshots,
        bool firstPoll,
        long nowUnix)
    {
        var changes = new List<LiveChange>();
        foreach (var room in known)
        {
            if (!snapshots.TryGetValue(room.Mid, out var snap)) continue;
            var from = room.LastStatus;
            var to = snap.Status;
            if (from == to) continue;

            var wentLive = from != LiveStatus.Live && to == LiveStatus.Live;
            var notify = wentLive
                         && !firstPoll
                         && room.Notify
                         && (room.NotifiedAt is not { } last || nowUnix - last >= NotifyCooldownSec);
            changes.Add(new LiveChange(room, snap, from, to, notify));
        }
        return changes;
    }
}
