using Microsoft.Data.Sqlite;
using VupPlayer.Core.Data;

namespace VupPlayer.Core.Tests;

public sealed class RepositoryTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"vup-test-{Guid.NewGuid():N}.db");
    readonly LibraryRepository _repo;

    public RepositoryTests()
    {
        var db = new Database(_path);
        db.Migrate();
        db.Migrate(); // 重复迁移应当无害
        _repo = new LibraryRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { }
    }

    IReadOnlyList<long> Seed(string bvid, int pages, string title = "【星瞳】《晴天》", long upMid = 100, string upName = "切片man甲")
    {
        var video = new Video { Bvid = bvid, Title = title, UpMid = upMid, UpName = upName };
        var tracks = Enumerable.Range(1, pages).Select(p => new Track { Bvid = bvid, Cid = 1000 + p, Page = p, PartTitle = $"P{p}", DurationSec = 200 });
        return _repo.ImportVideo(video, tracks, t => ("晴天", "星瞳"));
    }

    [Fact]
    public void Import_CreatesTracksAndGuessedMetadata()
    {
        var ids = Seed("BV1", 2);
        Assert.Equal(2, ids.Count);
        var t = _repo.GetTrack(ids[0])!;
        Assert.Equal("晴天", t.SongTitle);
        Assert.Equal("星瞳", t.StreamerName);
        Assert.Equal(2, t.PageCount);
        Assert.Equal("切片man甲", t.UpName);
    }

    [Fact]
    public void Reimport_KeepsUserEdits_AndCreatesNothing()
    {
        var ids = Seed("BV1", 1);
        _repo.UpdateTracks(ids, new TrackUpdate { SongTitle = "改过的歌名", Favorite = true });
        var again = Seed("BV1", 1);
        Assert.Empty(again);
        var t = _repo.GetTrack(ids[0])!;
        Assert.Equal("改过的歌名", t.SongTitle);
        Assert.True(t.Favorite);
    }

    [Fact]
    public void Query_FiltersAndKeyword()
    {
        Seed("BV1", 1);
        Seed("BV2", 1, "别的视频", 200, "切片man乙");
        _repo.UpdateTracks(_repo.QueryTracks(new TrackQuery { UpMid = 200 }).Select(t => t.Id).ToList(),
            new TrackUpdate { SongTitle = "", StreamerName = "", AddTags = ["翻唱"] });

        Assert.Equal(2, _repo.CountTracks(new TrackQuery()));
        Assert.Single(_repo.QueryTracks(new TrackQuery { Keyword = "乙" }));
        Assert.Single(_repo.QueryTracks(new TrackQuery { Tag = "翻唱" }));
        Assert.Single(_repo.QueryTracks(new TrackQuery { MissingSongTitle = true }));
        Assert.Single(_repo.QueryTracks(new TrackQuery { MissingStreamer = true }));
        Assert.Single(_repo.QueryTracks(new TrackQuery { StreamerName = "星瞳" }));
        Assert.Single(_repo.QueryTracks(new TrackQuery { Keyword = "BV2" }));
        Assert.Empty(_repo.QueryTracks(new TrackQuery { Keyword = "100%_" }));
    }

    [Fact]
    public void Hidden_IsExcludedByDefault()
    {
        var ids = Seed("BV1", 2);
        _repo.UpdateTracks([ids[0]], new TrackUpdate { Hidden = true });
        Assert.Equal(1, _repo.CountTracks(new TrackQuery()));
        Assert.Equal(2, _repo.CountTracks(new TrackQuery { IncludeHidden = true }));
    }

    [Fact]
    public void Tags_AddAndRemove()
    {
        var ids = Seed("BV1", 1);
        _repo.UpdateTracks(ids, new TrackUpdate { AddTags = ["翻唱", "日语"] });
        _repo.UpdateTracks(ids, new TrackUpdate { RemoveTags = ["日语"] });
        Assert.Equal("翻唱", _repo.GetTrack(ids[0])!.Tags);
    }

    [Fact]
    public void Streamer_AliasesAndMerge()
    {
        var a = Seed("BV1", 1);
        var b = _repo.ImportVideo(new Video { Bvid = "BV2", Title = "x", UpMid = 1, UpName = "u" },
            [new Track { Bvid = "BV2", Cid = 2, Page = 1 }], _ => (null, "星瞳official"));
        var keep = _repo.GetTrack(a[0])!.StreamerId!.Value;
        var other = _repo.GetTrack(b[0])!.StreamerId!.Value;
        Assert.NotEqual(keep, other);

        Assert.Equal(1, _repo.MergeStreamers(keep, [other]));
        Assert.Equal("星瞳", _repo.GetTrack(b[0])!.StreamerName);

        // 别名命中：用别名设置主播会落到同一个人
        _repo.UpdateTracks(b, new TrackUpdate { StreamerName = "星瞳official" });
        Assert.Equal(keep, _repo.GetTrack(b[0])!.StreamerId);
    }

    [Fact]
    public void Playlists_AddRemoveReorder()
    {
        var ids = Seed("BV1", 3);
        var pid = _repo.CreatePlaylist("测试");
        Assert.Equal(3, _repo.AddToPlaylist(pid, ids));
        Assert.Equal(0, _repo.AddToPlaylist(pid, ids));
        _repo.SetPlaylistOrder(pid, [ids[2], ids[0], ids[1]]);
        Assert.Equal([ids[2], ids[0], ids[1]], _repo.GetPlaylistTrackIds(pid));
        Assert.Equal(1, _repo.RemoveFromPlaylist(pid, [ids[0]]));
        Assert.Equal([ids[2], ids[1]], _repo.QueryTracks(new TrackQuery { PlaylistId = pid }).Select(t => t.Id));
        Assert.Equal(2, _repo.ListPlaylists().Single().ItemCount);
    }

    [Fact]
    public void PlayEvents_AndStats()
    {
        var ids = Seed("BV1", 1);
        _repo.AddPlayEvent(new PlayEvent { TrackId = ids[0], StartedAt = Clock.Now(), PlayedSec = 180, Completed = true });
        _repo.AddPlayEvent(new PlayEvent { TrackId = ids[0], StartedAt = Clock.Now(), PlayedSec = 10, Skipped = true });
        var s = _repo.GetPlayStats(null, 10).Single();
        Assert.Equal(2, s.Plays);
        Assert.Equal(1, s.Completed);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(2, _repo.GetTrack(ids[0])!.PlayCount);
        Assert.Equal(ids[0], _repo.RecentlyPlayed(5)[0]);
    }

    [Fact]
    public void Suggestions_AcceptWritesValue_RejectDoesNot()
    {
        var ids = Seed("BV1", 2);
        var s1 = _repo.AddSuggestion(ids[0], "song_title", "新歌名", "claude", 0.8);
        var s2 = _repo.AddSuggestion(ids[1], "tag", "翻唱", "claude", null);
        Assert.Equal(2, _repo.ListPendingSuggestions().Count);
        _repo.ResolveSuggestions([s1], accept: true);
        _repo.ResolveSuggestions([s2], accept: false);
        Assert.Empty(_repo.ListPendingSuggestions());
        Assert.Equal("新歌名", _repo.GetTrack(ids[0])!.SongTitle);
        Assert.Null(_repo.GetTrack(ids[1])!.Tags);
    }

    [Fact]
    public void Sources_LinkAndCount()
    {
        Seed("BV1", 1);
        var sid = _repo.UpsertSource("fav", "123", "我的收藏");
        Assert.Equal(sid, _repo.UpsertSource("fav", "123", null));
        _repo.LinkSourceVideo(sid, "BV1");
        _repo.LinkSourceVideo(sid, "BV1");
        _repo.MarkSourceSynced(sid);
        var s = _repo.ListSources().Single();
        Assert.Equal("我的收藏", s.Title);
        Assert.Equal(1, s.VideoCount);
        Assert.NotNull(s.LastSynced);
    }

    [Fact]
    public void Duplicates_GroupBySongAndStreamer()
    {
        Seed("BV1", 1);
        Seed("BV2", 1);
        var g = _repo.FindDuplicates().Single();
        Assert.Equal(2, g.TrackIds.Count);
    }

    [Fact]
    public void DeleteVideo_CascadesToTracksAndEvents()
    {
        var ids = Seed("BV1", 2);
        _repo.AddPlayEvent(new PlayEvent { TrackId = ids[0], StartedAt = 1, PlayedSec = 5 });
        Assert.Equal(1, _repo.DeleteVideo("BV1"));
        Assert.Equal(0, _repo.CountTracks(new TrackQuery { IncludeHidden = true }));
        Assert.Empty(_repo.RecentlyPlayed(5));
    }
}
