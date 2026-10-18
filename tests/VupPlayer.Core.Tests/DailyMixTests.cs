using Microsoft.Data.Sqlite;
using VupPlayer.Core.Data;
using VupPlayer.Core.Recommend;

namespace VupPlayer.Core.Tests;

public sealed class DailyMixTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"vup-mix-{Guid.NewGuid():N}.db");
    readonly LibraryRepository _repo;
    const long Now = 1_800_000_000;
    const long Day = 86400;

    public DailyMixTests()
    {
        var db = new Database(_path);
        db.Migrate();
        _repo = new LibraryRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { }
    }

    long Add(string bvid, string song, string streamer, long addedDaysAgo = 60) =>
        _repo.ImportVideo(new Video { Bvid = bvid, Title = song, UpMid = 1, UpName = "up", AddedAt = Now - addedDaysAgo * Day },
            [new Track { Bvid = bvid, Cid = bvid.GetHashCode(), Page = 1, DurationSec = 200 }], _ => (song, streamer))[0];

    DailyMix Mix() => new(_repo) { Now = () => Now };

    [Fact]
    public void Generate_RespectsCount_AndSongsAreUnique()
    {
        for (var i = 0; i < 40; i++) Add($"BV{i}", $"歌{i % 30}", "甲");   // 30 首不同的歌，其中 10 首有两个切片
        var items = Mix().Generate(30, rng: new Random(1));
        Assert.True(items.Count <= 30);
        var songs = items.Select(i => _repo.GetTrack(i.TrackId)!.SongTitle).ToList();
        Assert.Equal(songs.Count, songs.Distinct().Count());
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Reason)));
    }

    [Fact]
    public void Generate_SpreadsAcrossStreamers()
    {
        for (var i = 0; i < 40; i++) Add($"BVa{i}", $"a{i}", "甲");
        for (var i = 0; i < 40; i++) Add($"BVb{i}", $"b{i}", "乙");
        for (var i = 0; i < 40; i++) Add($"BVc{i}", $"c{i}", "丙");
        var items = Mix().Generate(30, rng: new Random(2));
        var counts = items.GroupBy(i => _repo.GetTrack(i.TrackId)!.StreamerName).Select(g => g.Count()).ToList();
        Assert.Equal(3, counts.Count);
        Assert.True(counts.Max() <= 15, $"最多的主播占了 {counts.Max()} 首");
    }

    [Fact]
    public void GetDaily_IsStableWithinADay_RefreshChangesIt()
    {
        for (var i = 0; i < 100; i++) Add($"BV{i}", $"歌{i}", i % 2 == 0 ? "甲" : "乙");
        var mix = Mix();
        var day = new DateOnly(2026, 10, 18);
        var first = mix.GetDaily(day).Select(i => i.TrackId).ToList();
        var again = mix.GetDaily(day).Select(i => i.TrackId).ToList();
        Assert.Equal(first, again);
        var refreshed = mix.Refresh(day).Select(i => i.TrackId).ToList();
        Assert.True(refreshed.Intersect(first).Count() < first.Count / 2, "换一批应该大部分不同");
        Assert.Equal(refreshed, mix.GetDaily(day).Select(i => i.TrackId));
    }

    [Fact]
    public void GetDaily_DropsHiddenTracks()
    {
        for (var i = 0; i < 10; i++) Add($"BV{i}", $"歌{i}", "甲");
        var mix = Mix();
        var day = new DateOnly(2026, 10, 18);
        var list = mix.GetDaily(day);
        _repo.UpdateTracks([list[0].TrackId], new TrackUpdate { Hidden = true });
        Assert.DoesNotContain(mix.GetDaily(day), i => i.TrackId == list[0].TrackId);
    }

    [Fact]
    public void Rediscover_LovedButNotRecent()
    {
        var loved = Add("BV1", "老歌", "甲");
        var recent = Add("BV2", "最近的", "甲");
        var skipped = Add("BV3", "跳过的", "甲");
        foreach (var t in new[] { loved, recent }) for (var i = 0; i < 3; i++)
            _repo.AddPlayEvent(new PlayEvent { TrackId = t, StartedAt = t == loved ? Now - 30 * Day : Now - Day, PlayedSec = 200, Completed = true });
        for (var i = 0; i < 3; i++) _repo.AddPlayEvent(new PlayEvent { TrackId = skipped, StartedAt = Now - 30 * Day, PlayedSec = 5, Skipped = true });
        var items = Mix().Rediscover();
        Assert.Equal([loved], items.Select(i => i.TrackId));
        Assert.Contains("30 天没听了", items[0].Reason);
    }

    [Fact]
    public void Fresh_RecentImportsNotPlayed()
    {
        var fresh = Add("BV1", "新的", "甲", addedDaysAgo: 0);
        var old = Add("BV2", "旧的", "甲", addedDaysAgo: 60);
        var played = Add("BV3", "听过的新歌", "甲", addedDaysAgo: 1);
        _repo.AddPlayEvent(new PlayEvent { TrackId = played, StartedAt = Now, PlayedSec = 100 });
        var items = Mix().Fresh();
        Assert.Equal([fresh], items.Select(i => i.TrackId));
        Assert.StartsWith("今天导入", items[0].Reason);
    }
}
