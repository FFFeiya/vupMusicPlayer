using VupPlayer.Bilibili;
using VupPlayer.Core;

namespace VupPlayer.Core.Tests;

public class LiveTransitionTests
{
    const long Now = 1_800_000_000;

    static LiveRoom Room(long mid, LiveStatus last, bool notify = true, long? notifiedAt = null) =>
        new() { Mid = mid, RoomId = 1000 + mid, Uname = "主播" + mid, LastStatus = last, Notify = notify, NotifiedAt = notifiedAt };

    static Dictionary<long, LiveSnapshot> Snap(long mid, LiveStatus status) =>
        new() { [mid] = new LiveSnapshot(mid, 1000 + mid, "主播" + mid, null, status, "标题", null, "虚拟Singer", 100, Now - 60) };

    [Fact]
    public void 未开播变直播要推送()
    {
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Offline)], Snap(1, LiveStatus.Live), firstPoll: false, Now);
        var c = Assert.Single(changes);
        Assert.True(c.WentLive);
        Assert.True(c.Notify);
    }

    [Fact]
    public void 轮播不算开播也不推送()
    {
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Offline)], Snap(1, LiveStatus.Replay), firstPoll: false, Now);
        var c = Assert.Single(changes);
        Assert.False(c.WentLive);
        Assert.False(c.Notify);
    }

    [Fact]
    public void 轮播转直播要推送()
    {
        // 主播先挂轮播再真开播，是常见情况
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Replay)], Snap(1, LiveStatus.Live), firstPoll: false, Now);
        Assert.True(Assert.Single(changes).Notify);
    }

    [Fact]
    public void 状态没变不产生跳变()
    {
        Assert.Empty(LiveTransitions.Diff([Room(1, LiveStatus.Live)], Snap(1, LiveStatus.Live), firstPoll: false, Now));
    }

    [Fact]
    public void 第一轮只落库不推送()
    {
        // 否则一开机就把所有在播的人全弹一遍
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Offline)], Snap(1, LiveStatus.Live), firstPoll: true, Now);
        var c = Assert.Single(changes);
        Assert.True(c.WentLive);
        Assert.False(c.Notify);
    }

    [Fact]
    public void 关掉提醒的主播不推送但仍记录状态()
    {
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Offline, notify: false)], Snap(1, LiveStatus.Live), firstPoll: false, Now);
        var c = Assert.Single(changes);
        Assert.True(c.WentLive);
        Assert.False(c.Notify);
    }

    [Fact]
    public void 冷却期内不重复推送()
    {
        // 主播网络不好反复断流重连时，不该连环弹通知
        var recent = Room(1, LiveStatus.Offline, notifiedAt: Now - 600);
        Assert.False(Assert.Single(LiveTransitions.Diff([recent], Snap(1, LiveStatus.Live), false, Now)).Notify);

        var old = Room(1, LiveStatus.Offline, notifiedAt: Now - LiveTransitions.NotifyCooldownSec - 1);
        Assert.True(Assert.Single(LiveTransitions.Diff([old], Snap(1, LiveStatus.Live), false, Now)).Notify);
    }

    [Fact]
    public void 下播产生跳变但不推送()
    {
        var changes = LiveTransitions.Diff([Room(1, LiveStatus.Live)], Snap(1, LiveStatus.Offline), firstPoll: false, Now);
        var c = Assert.Single(changes);
        Assert.True(c.WentOffline);
        Assert.False(c.Notify);
    }

    [Fact]
    public void 查不到的主播被跳过()
    {
        // 批量接口查不到的 UID 不会出现在结果里，不能当成下播
        Assert.Empty(LiveTransitions.Diff([Room(1, LiveStatus.Live)], new Dictionary<long, LiveSnapshot>(), firstPoll: false, Now));
    }

    [Fact]
    public void 多个主播分别判断()
    {
        var known = new[] { Room(1, LiveStatus.Offline), Room(2, LiveStatus.Live), Room(3, LiveStatus.Offline) };
        var snaps = Snap(1, LiveStatus.Live);
        foreach (var (k, v) in Snap(2, LiveStatus.Offline)) snaps[k] = v;
        foreach (var (k, v) in Snap(3, LiveStatus.Offline)) snaps[k] = v;

        var changes = LiveTransitions.Diff(known, snaps, firstPoll: false, Now);
        Assert.Equal(2, changes.Count);
        Assert.True(changes.Single(c => c.Room.Mid == 1).Notify);
        Assert.True(changes.Single(c => c.Room.Mid == 2).WentOffline);
    }
}

public class LiveLinkTests
{
    [Theory]
    [InlineData("https://live.bilibili.com/80397", 80397)]
    [InlineData("https://live.bilibili.com/80397?broadcast_type=0&is_room_feed=1", 80397)]
    [InlineData("live.bilibili.com/21452505", 21452505)]
    [InlineData("https://live.bilibili.com/h5/1883943131", 1883943131)]
    [InlineData("https://live.bilibili.com/blanc/972474", 972474)]
    public void 识别直播间链接(string input, long roomId)
    {
        var link = LinkParser.Parse(input);
        Assert.Equal(LinkKind.Live, link.Kind);
        Assert.Equal(roomId, link.RoomId);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("https://space.bilibili.com/7706705")]
    [InlineData("BV1xx411c7mD")]
    public void 其它链接不会被当成直播(string input) =>
        Assert.NotEqual(LinkKind.Live, LinkParser.Parse(input).Kind);

    [Fact]
    public void 直播间的个人页之类没有房间号的链接不算直播间()
    {
        // live.bilibili.com/p/eden/... 这类没有房间号
        Assert.NotEqual(LinkKind.Live, LinkParser.Parse("https://live.bilibili.com/p/eden/area-tags").Kind);
    }
}

public class LiveLineOrderTests
{
    static LiveLine L(string protocol, string format, string codec, string host) =>
        new($"https://{host}/live/x.{format}", protocol, format, codec, 10000);

    [Fact]
    public void 线路按稳定性排序()
    {
        // B 站返回的顺序通常是 flv 在前，但 flv 长时间播放容易卡住，所以排到最后当兜底
        var ordered = BiliApi.OrderLines([
            L("http_stream", "flv", "avc", "a.com"),
            L("http_hls", "ts", "avc", "b.com"),
            L("http_hls", "fmp4", "avc", "c.com"),
        ]);
        Assert.Equal(["fmp4", "ts", "flv"], ordered.Select(l => l.Format));
    }

    [Fact]
    public void 同协议下AVC优先于HEVC()
    {
        var ordered = BiliApi.OrderLines([
            L("http_hls", "fmp4", "hevc", "a.com"),
            L("http_hls", "fmp4", "avc", "b.com"),
        ]);
        Assert.Equal("avc", ordered[0].Codec);
    }

    [Fact]
    public void 同协议同编码保留B站给的CDN顺序()
    {
        var ordered = BiliApi.OrderLines([
            L("http_hls", "fmp4", "avc", "first.com"),
            L("http_hls", "fmp4", "avc", "second.com"),
        ]);
        Assert.Equal(["first.com", "second.com"], ordered.Select(l => l.Host));
    }
}

public class LiveTimeParseTests
{
    [Fact]
    public void 没开播时的时间解析成零()
    {
        Assert.Equal(0, BiliApi.ParseLiveTime("0000-00-00 00:00:00"));
        Assert.Equal(0, BiliApi.ParseLiveTime(""));
        Assert.Equal(0, BiliApi.ParseLiveTime(null));
    }

    [Fact]
    public void 开播时间解析成时间戳()
    {
        var ts = BiliApi.ParseLiveTime("2026-11-03 20:30:00");
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 3, 20, 30, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 3, 20, 30, 0))).ToUnixTimeSeconds(), ts);
    }
}
