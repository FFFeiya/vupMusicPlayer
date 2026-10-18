using VupPlayer.Core.Playback;
using VupPlayer.Core.Recommend;

namespace VupPlayer.Core.Tests;

public class PlayQueueTests
{
    static PlayQueue Make(PlayMode mode, int n = 5, int seed = 42)
    {
        var q = new PlayQueue(new Random(seed)) { Mode = mode };
        q.Load(Enumerable.Range(1, n).Select(i => (long)i), startId: 1);
        return q;
    }

    [Fact]
    public void Sequential_StopsAtEnd()
    {
        var q = Make(PlayMode.Sequential, 3);
        Assert.Equal(1, q.Current);
        Assert.Equal(2, q.Next(auto: true));
        Assert.Equal(3, q.Next(auto: true));
        Assert.Null(q.Next(auto: true));
        Assert.Null(q.PeekNext());
    }

    [Fact]
    public void Sequential_ManualNextAtEnd_WrapsAround()
    {
        var q = Make(PlayMode.Sequential, 2);
        q.Next(auto: true);
        Assert.Equal(1, q.Next(auto: false));
    }

    [Fact]
    public void RepeatAll_WrapsAround()
    {
        var q = Make(PlayMode.RepeatAll, 3);
        q.Next(true); q.Next(true);
        Assert.Equal(1, q.PeekNext());
        Assert.Equal(1, q.Next(true));
    }

    [Fact]
    public void RepeatOne_RepeatsOnAuto_AdvancesOnManual()
    {
        var q = Make(PlayMode.RepeatOne, 3);
        Assert.Equal(1, q.Next(auto: true));
        Assert.Equal(1, q.PeekNext(auto: true));
        Assert.Equal(2, q.Next(auto: false));
    }

    [Fact]
    public void Shuffle_PlaysEveryTrackOncePerRound()
    {
        var q = Make(PlayMode.Shuffle, 10);
        var seen = new List<long> { q.Current!.Value };
        for (var i = 0; i < 9; i++) seen.Add(q.Next(true)!.Value);
        Assert.Equal(10, seen.Distinct().Count());
        // 第二轮仍然能继续，且第一首不等于上一轮最后一首
        var last = seen[^1];
        var next = q.Next(true);
        Assert.NotNull(next);
        Assert.NotEqual(last, next);
    }

    [Fact]
    public void Shuffle_KeepsCurrentWhenSwitchingMode()
    {
        var q = Make(PlayMode.RepeatAll, 6);
        q.Next(true);
        Assert.Equal(2, q.Current);
        q.Mode = PlayMode.Shuffle;
        Assert.Equal(2, q.Current);
        q.Mode = PlayMode.RepeatAll;
        Assert.Equal(2, q.Current);
        Assert.Equal(3, q.Next(true));
    }

    [Fact]
    public void Shuffle_JumpToPlayedTrack_DoesNotRewindRound()
    {
        var q = Make(PlayMode.Shuffle, 6);
        var played = new HashSet<long> { q.Current!.Value };
        played.Add(q.Next(true)!.Value);
        played.Add(q.Next(true)!.Value);
        var first = played.First();
        q.Jump(first);
        Assert.Equal(first, q.Current);
        // 剩下的 3 首都还能播到
        var rest = new HashSet<long>();
        for (var i = 0; i < 3; i++) rest.Add(q.Next(true)!.Value);
        Assert.Equal(6, played.Union(rest).Count());
    }

    [Fact]
    public void Previous_InRepeatAll_WrapsToEnd()
    {
        var q = Make(PlayMode.RepeatAll, 3);
        Assert.Equal(3, q.Previous());
    }

    [Fact]
    public void Previous_InRandom_UsesHistory()
    {
        var q = Make(PlayMode.Random, 5);
        q.Jump(4);
        q.Jump(2);
        Assert.Equal(4, q.Previous());
        Assert.Equal(1, q.Previous());
    }

    [Fact]
    public void Random_RequiresRecommender()
    {
        var q = Make(PlayMode.Random, 3);
        Assert.True(q.NeedsRecommender);
        Assert.Throws<InvalidOperationException>(() => q.Next(true));
        Assert.Null(q.PeekNext());
    }

    [Fact]
    public void PlayNext_InsertsAfterCurrent()
    {
        var q = Make(PlayMode.RepeatAll, 5);
        q.PlayNext([4, 5]);
        Assert.Equal([1, 4, 5, 2, 3], q.Upcoming());
        Assert.Equal(4, q.Next(true));
    }

    [Fact]
    public void PlayNext_NewTrack_IsAdded()
    {
        var q = Make(PlayMode.RepeatAll, 3);
        q.PlayNext([99]);
        Assert.Equal(99, q.Next(true));
        Assert.Equal(4, q.Count);
    }

    [Fact]
    public void Enqueue_AppendsAndSkipsDuplicates()
    {
        var q = Make(PlayMode.Sequential, 2);
        q.Enqueue([2, 7]);
        Assert.Equal([1, 2, 7], q.Upcoming());
    }

    [Fact]
    public void Remove_CurrentTrack_NextPlaysFollowing()
    {
        var q = Make(PlayMode.Sequential, 4);
        q.Next(true); // 2
        q.Remove(2);
        Assert.Equal(3, q.Next(true));
        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void Load_EmptyList_HasNoCurrent()
    {
        var q = new PlayQueue();
        q.Load([]);
        Assert.Null(q.Current);
        Assert.Null(q.Next(true));
    }
}

public class RandomRecommenderTests
{
    [Fact]
    public async Task AvoidsCurrentAndRecent()
    {
        var r = new RandomRecommender(new Random(1));
        var candidates = Enumerable.Range(1, 10).Select(i => (long)i).ToList();
        for (var i = 0; i < 200; i++)
        {
            var next = await r.NextAsync(new RecommendContext(candidates, [1, 2, 3, 4, 5], 5));
            Assert.NotNull(next);
            Assert.DoesNotContain(next!.TrackId!.Value, new long[] { 1, 2, 3, 4, 5 });
        }
    }

    [Fact]
    public async Task FallsBackWhenEverythingIsRecent()
    {
        var r = new RandomRecommender(new Random(1));
        var next = await r.NextAsync(new RecommendContext([1, 2], [1, 2], 1));
        Assert.Equal(2, next!.TrackId);
    }

    [Fact]
    public async Task EmptyCandidates_ReturnsNull()
    {
        var r = new RandomRecommender();
        Assert.Null(await r.NextAsync(new RecommendContext([], [], null)));
    }

    [Fact]
    public async Task CoversAllCandidatesOverTime()
    {
        var r = new RandomRecommender(new Random(7));
        var candidates = Enumerable.Range(1, 8).Select(i => (long)i).ToList();
        var seen = new HashSet<long>();
        long? cur = null;
        var history = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            cur = (await r.NextAsync(new RecommendContext(candidates, history, cur)))!.TrackId;
            history.Add(cur!.Value);
            seen.Add(cur.Value);
        }
        Assert.Equal(8, seen.Count);
    }
}
