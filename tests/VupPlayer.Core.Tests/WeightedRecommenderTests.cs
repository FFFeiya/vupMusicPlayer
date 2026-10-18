using Microsoft.Data.Sqlite;
using VupPlayer.Bilibili;
using VupPlayer.Core.Data;
using VupPlayer.Core.Recommend;

namespace VupPlayer.Core.Tests;

public sealed class WeightedRecommenderTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"vup-rec-{Guid.NewGuid():N}.db");
    readonly LibraryRepository _repo;
    const long Now = 1_800_000_000;
    const long Day = 86400;

    public WeightedRecommenderTests()
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

    /// <summary>导入一首单曲，返回曲目 id。</summary>
    long Add(string bvid, string song, string streamer, long upMid = 1, long addedDaysAgo = 60, int duration = 200)
    {
        var v = new Video { Bvid = bvid, Title = song, UpMid = upMid, UpName = $"up{upMid}", AddedAt = Now - addedDaysAgo * Day };
        return _repo.ImportVideo(v, [new Track { Bvid = bvid, Cid = bvid.GetHashCode(), Page = 1, DurationSec = duration }], _ => (song, streamer))[0];
    }

    void Play(long id, long hoursAgo, bool completed = false, bool skipped = false, double played = 100) =>
        _repo.AddPlayEvent(new PlayEvent { TrackId = id, StartedAt = Now - hoursAgo * 3600, PlayedSec = played, Completed = completed, Skipped = skipped });

    WeightedRecommender Make(int seed = 1, double temperature = 1.0, double discovery = 0) =>
        new(_repo, new RecommendOptions { Temperature = temperature, DiscoveryRate = discovery, HardExcludeRecent = 2 }, new Random(seed)) { Now = () => Now };

    RecommendContext Ctx(long? current = null, bool discovery = false) => new(_repo.GetVisibleTrackIds(), [], current, discovery);

    double ScoreOf(WeightedRecommender r, long id, long? current = null) =>
        r.ScoreTracks(Ctx(current)).Single(s => s.TrackId == id).Score;

    [Fact]
    public void LovedTrackScoresHigherThanSkippedTrack()
    {
        var loved = Add("BV1", "晴天", "甲");
        var skipped = Add("BV2", "稻香", "甲");
        for (var i = 0; i < 4; i++) Play(loved, 200 + i, completed: true, played: 200);
        for (var i = 0; i < 4; i++) Play(skipped, 200 + i, skipped: true, played: 10);
        Add("BV3", "七里香", "乙");   // 凑够候选，避免排除规则影响
        Add("BV4", "夜曲", "乙");
        var r = Make();
        Assert.True(ScoreOf(r, loved) > ScoreOf(r, skipped) * 3);
    }

    [Fact]
    public void RecentlyPlayedIsCooledDown()
    {
        var ids = Enumerable.Range(1, 30).Select(i => Add($"BV{i}", $"歌{i}", "甲")).ToList();
        Play(ids[0], hoursAgo: 1, completed: true, played: 200);     // 1 小时前
        Play(ids[1], hoursAgo: 300, completed: true, played: 200);   // 很久以前
        Play(ids[2], hoursAgo: 500, completed: true, played: 200);
        Play(ids[3], hoursAgo: 600, completed: true, played: 200);
        var r = Make();
        // ids[0] 在最近 2 首之外吗？最近播放顺序：0,1,2,3（按时间倒序 0 最新）→ 0、1 被硬排除
        var scored = r.ScoreTracks(Ctx());
        Assert.DoesNotContain(scored, s => s.TrackId == ids[0]);   // 1 小时前播过：硬排除
        Assert.Contains(scored, s => s.TrackId == ids[1]);         // 300 小时前：不排除，只看冷却
        // 2 和 3 的冷却都接近 1；和刚播的比较：用冷却因子单独验证
        var cool = scored.Single(s => s.TrackId == ids[2]).Factors.Single(f => f.Name == "冷却").Multiplier;
        Assert.True(cool > 0.9);
    }

    [Fact]
    public void CooldownFactorGrowsWithTime()
    {
        var ids = Enumerable.Range(1, 30).Select(i => Add($"BV{i}", $"歌{i}", "甲")).ToList();
        Play(ids[10], hoursAgo: 1);
        Play(ids[11], hoursAgo: 48);
        Play(ids[0], hoursAgo: 0);   // 两首最新的被硬排除，不影响 10 和 11
        Play(ids[1], hoursAgo: 0);
        var r = Make();
        var scored = r.ScoreTracks(Ctx());
        double Cool(long id) => scored.Single(s => s.TrackId == id).Factors.Single(f => f.Name == "冷却").Multiplier;
        Assert.True(Cool(ids[10]) < 0.6);
        Assert.True(Cool(ids[11]) > Cool(ids[10]));
    }

    [Fact]
    public void NewUnplayedTrackGetsBoost()
    {
        var fresh = Add("BV1", "新歌", "甲", addedDaysAgo: 0);
        var old = Add("BV2", "老歌", "甲", addedDaysAgo: 200);
        Add("BV3", "别的", "乙");
        Add("BV4", "别的2", "乙");
        var r = Make();
        var f = r.ScoreTracks(Ctx()).Single(s => s.TrackId == fresh);
        Assert.Contains(f.Factors, x => x.Name == "新鲜度" && x.Multiplier > 2);
        Assert.Contains("新导入", f.Reason);
        Assert.True(ScoreOf(r, fresh) > ScoreOf(r, old));
    }

    [Fact]
    public void FavoriteBoostsAndShowsInReason()
    {
        var fav = Add("BV1", "晴天", "甲");
        var plain = Add("BV2", "稻香", "甲");
        Add("BV3", "a", "乙");
        Add("BV4", "b", "乙");
        _repo.UpdateTracks([fav], new TrackUpdate { Favorite = true });
        var r = Make();
        var s = r.ScoreTracks(Ctx()).Single(x => x.TrackId == fav);
        Assert.Contains("喜欢", s.Reason);
        Assert.True(s.Score > ScoreOf(r, plain) * 1.5);
    }

    [Fact]
    public void AnotherClipOfSameSongJustPlayedIsPenalized()
    {
        var a = Add("BV1", "晴天", "甲", upMid: 1);
        var b = Add("BV2", "晴天", "甲", upMid: 2);   // 同一首歌的另一个切片
        var c = Add("BV3", "稻香", "甲", upMid: 2);
        var fillers = Enumerable.Range(10, 20).Select(i => Add($"BV{i}", $"歌{i}", "乙")).ToList();
        Play(fillers[0], 0); Play(fillers[1], 0);   // 占住硬排除名额
        Play(a, hoursAgo: 1, completed: true, played: 200);
        var r = Make();
        var scored = r.ScoreTracks(Ctx());
        var sameSong = scored.Single(s => s.TrackId == b).Factors.SingleOrDefault(f => f.Name == "同歌冷却");
        Assert.NotNull(sameSong);
        Assert.True(sameSong!.Multiplier < 0.7, $"同歌冷却 {sameSong.Multiplier}");
        Assert.DoesNotContain(scored.Single(s => s.TrackId == c).Factors, f => f.Name == "同歌冷却");
    }

    [Fact]
    public void StreakOfSameStreamerIsPenalized()
    {
        var a1 = Add("BV1", "a1", "甲"); var a2 = Add("BV2", "a2", "甲"); var a3 = Add("BV3", "a3", "甲");
        var a4 = Add("BV4", "a4", "甲");
        var b1 = Add("BV5", "b1", "乙");
        Enumerable.Range(10, 10).Select(i => Add($"BV{i}", $"c{i}", "丙")).ToList();
        Play(a1, 3); Play(a2, 2); Play(a3, 1);
        var r = Make();
        var scored = r.ScoreTracks(Ctx());
        var streak = scored.Single(s => s.TrackId == a4).Factors.Single(f => f.Name == "连续同主播").Multiplier;
        Assert.Equal(0.4, streak, 3);
        Assert.DoesNotContain(scored.Single(s => s.TrackId == b1).Factors, f => f.Name == "连续同主播");
    }

    [Fact]
    public void LowTemperatureConcentratesOnTopScores()
    {
        var top = Add("BV1", "最爱", "甲");
        _repo.UpdateTracks([top], new TrackUpdate { Favorite = true });
        for (var i = 0; i < 5; i++) Play(top, 300 + i, completed: true, played: 200);
        var others = Enumerable.Range(2, 20).Select(i => Add($"BV{i}", $"歌{i}", "乙")).ToList();
        foreach (var o in others) { Play(o, 400, skipped: true, played: 5); }

        int Count(double t)
        {
            var r = Make(seed: 3, temperature: t);
            var n = 0;
            for (var i = 0; i < 300; i++) if (r.NextAsync(Ctx()).Result!.TrackId == top) n++;
            return n;
        }
        var conservative = Count(0.5);
        var adventurous = Count(2.0);
        Assert.True(conservative > adventurous, $"保守 {conservative} 应多于冒险 {adventurous}");
        Assert.True(adventurous > 0);
    }

    [Fact]
    public void ExcludesCurrentTrack()
    {
        var ids = Enumerable.Range(1, 5).Select(i => Add($"BV{i}", $"歌{i}", "甲")).ToList();
        var r = Make();
        for (var i = 0; i < 50; i++)
            Assert.NotEqual(ids[0], r.NextAsync(Ctx(current: ids[0])).Result!.TrackId);
    }

    [Fact]
    public void Discovery_PickedAtConfiguredRate_AndPrefersKnownStreamer()
    {
        var a = Add("BV1", "晴天", "甲", upMid: 7);
        for (var i = 0; i < 5; i++) Play(a, 300 + i, completed: true, played: 200);
        Enumerable.Range(2, 10).Select(i => Add($"BV{i}", $"歌{i}", "乙")).ToList();
        _repo.AddDiscovery([
            new DiscoveryItem { Bvid = "BVd1", Title = "【甲】新歌", Streamer = "甲", UploaderMid = 7, PlayCount = 100000, Pubdate = Now - Day },
            new DiscoveryItem { Bvid = "BVd2", Title = "【丙】别的", Streamer = "丙", UploaderMid = 99, PlayCount = 100 },
        ]);
        var r = Make(seed: 5, discovery: 0.3);
        var picks = Enumerable.Range(0, 500).Select(_ => r.NextAsync(Ctx(discovery: true)).Result!).ToList();
        var disc = picks.Where(p => p.Discovery is not null).ToList();
        Assert.InRange(disc.Count, 100, 200);   // 约 30%
        Assert.True(disc.Count(p => p.Discovery!.Bvid == "BVd1") > disc.Count(p => p.Discovery!.Bvid == "BVd2"));
        Assert.Contains("B 站发现", disc[0].Reason);

        // 关闭发现时不会挑
        var off = Make(seed: 5, discovery: 0.3);
        Assert.All(Enumerable.Range(0, 100).Select(_ => off.NextAsync(Ctx(discovery: false)).Result!), p => Assert.Null(p.Discovery));
    }

    [Fact]
    public void DiscoveryPool_SkipsVideosAlreadyInLibrary()
    {
        Add("BV1", "晴天", "甲");
        _repo.AddDiscovery([new DiscoveryItem { Bvid = "BV1", Title = "x" }, new DiscoveryItem { Bvid = "BVn", Title = "y" }]);
        Assert.Equal(["BVn"], _repo.GetDiscoveryCandidates().Select(d => d.Bvid));
        _repo.SetDiscoveryState("BVn", "rejected");
        Assert.Empty(_repo.GetDiscoveryCandidates());
        Assert.Equal(0, _repo.AddDiscovery([new DiscoveryItem { Bvid = "BVn", Title = "y" }]));   // 拒绝过的不会再入池
    }

    [Fact]
    public void RecommenderStats_GroupByPicker()
    {
        var a = Add("BV1", "晴天", "甲");
        _repo.AddPlayEvent(new PlayEvent { TrackId = a, StartedAt = Now, PlayedSec = 200, Completed = true, Recommender = "weighted-v1" });
        _repo.AddPlayEvent(new PlayEvent { TrackId = a, StartedAt = Now, PlayedSec = 5, Skipped = true, Recommender = "weighted-v1" });
        _repo.AddPlayEvent(new PlayEvent { TrackId = a, StartedAt = Now, PlayedSec = 200, Completed = true, Recommender = "manual" });
        var stats = _repo.GetRecommenderStats(null).ToDictionary(s => s.Recommender);
        Assert.Equal(2, stats["weighted-v1"].Plays);
        Assert.Equal(0.5, stats["weighted-v1"].SkipRate);
        Assert.Equal(1.0, stats["manual"].CompletionRate);
    }
}

public class DiscoveryFilterTests
{
    static SearchItem Item(string title, int dur = 200) => new("BVx", title, "up", 1, dur, 1000, 0, null, null);

    [Theory]
    [InlineData("【阿梓】《绝不认输》", 200, true)]
    [InlineData("阿梓唱晴天", 200, true)]
    [InlineData("【阿梓】歌回合集", 200, false)]          // 合集
    [InlineData("【阿梓】《绝不认输》", 30, false)]        // 太短
    [InlineData("【阿梓】《绝不认输》", 3000, false)]      // 太长
    [InlineData("【白夕】《绝不认输》", 200, false)]       // 不是这个主播
    [InlineData("【阿梓】打游戏名场面", 200, false)]
    public void LooksLikeSong(string title, int duration, bool expected) =>
        Assert.Equal(expected, DiscoveryService.LooksLikeSong(Item(title, duration), ["阿梓"]));
}
