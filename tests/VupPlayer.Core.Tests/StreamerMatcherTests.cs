using Microsoft.Data.Sqlite;
using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Core.Tests;

public class StreamerMatcherTests
{
    static readonly KnownStreamer Uo = new(1, "雾氧Uo", [], 872);
    static readonly KnownStreamer Azi = new(2, "阿梓歌", ["阿梓"], 20);
    static readonly KnownStreamer Nanami = new(3, "七海Nana7mi", [], 10);

    [Theory]
    [InlineData("雾氧Uo直播翻唱", "雾氧uo")]
    [InlineData("ＡＢＣ酱 official", "abc酱")]
    [InlineData("七海 Nana7mi", "七海nana7mi")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, StreamerMatcher.Normalize(input));

    [Theory]
    [InlineData("2025冰火歌会", true)]
    [InlineData("三小只", true)]
    [InlineData("周年纪念", true)]
    [InlineData("雾氧Uo", false)]
    [InlineData("阿梓歌", false)]
    public void IsNotPerson(string name, bool expected) => Assert.Equal(expected, StreamerMatcher.IsNotPerson(name));

    [Theory]
    [InlineData("雾氧uo", "雾氧up", true)]       // 切片 man 打错一个字
    [InlineData("七海", "七濑", false)]           // 太短，不判
    [InlineData("abcd", "xbcd", false)]           // 开头不同，不判
    [InlineData("雾氧uo", "雾氧uo", false)]       // 相同不算错字
    [InlineData("nana7mi", "nana8mi", true)]
    public void LooksLikeTypo(string a, string b, bool expected) => Assert.Equal(expected, StreamerMatcher.LooksLikeTypo(a, b));

    [Fact]
    public void Match_Typo_OnlyWhenFuzzyAllowed()
    {
        var known = new[] { Uo, Azi, Nanami };
        Assert.Equal(StreamerMatchKind.Fuzzy, StreamerMatcher.Match("雾氧Up", known, allowFuzzy: true)!.Kind);
        Assert.Null(StreamerMatcher.Match("雾氧Up", known, allowFuzzy: false));
    }

    [Fact]
    public void Match_AliasAndNormalized()
    {
        var known = new[] { Uo, Azi };
        Assert.Equal(StreamerMatchKind.Alias, StreamerMatcher.Match("阿梓", known, false)!.Kind);
        var m = StreamerMatcher.Match("雾氧UO直播翻唱", known, false)!;
        Assert.Equal(StreamerMatchKind.Normalized, m.Kind);
        Assert.Equal(1, m.StreamerId);
    }

    [Fact]
    public void FindSimilar_ReportsTypoAndPrefix()
    {
        var pairs = StreamerMatcher.FindSimilar([Uo, new KnownStreamer(9, "雾氧Up", [], 1), new KnownStreamer(10, "阿梓", [], 2), new KnownStreamer(11, "阿梓歌", [], 19)]);
        Assert.Contains(pairs, p => p.A.Name == "雾氧Uo" && p.B.Name == "雾氧Up");
        Assert.Contains(pairs, p => p.A.Name == "阿梓歌" && p.B.Name == "阿梓");
    }
}

public sealed class StreamerRepositoryTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"vup-str-{Guid.NewGuid():N}.db");
    readonly LibraryRepository _repo;

    public StreamerRepositoryTests()
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

    long Import(string bvid, string? streamer, long upMid = 1) =>
        _repo.ImportVideo(new Video { Bvid = bvid, Title = bvid, UpMid = upMid, UpName = "up" },
            [new Track { Bvid = bvid, Cid = bvid.GetHashCode(), Page = 1 }], _ => ("歌", streamer))[0];

    [Fact]
    public void Import_TypoGoesToExistingStreamer_AndBecomesAlias()
    {
        for (var i = 0; i < 5; i++) Import($"BV{i}", "雾氧Uo");
        var t = Import("BVx", "雾氧Up");
        var track = _repo.GetTrack(t)!;
        Assert.Equal("雾氧Uo", track.StreamerName);
        Assert.Single(_repo.GetStreamers());
        Assert.Contains("雾氧Up", _repo.GetStreamers()[0].Aliases);
    }

    [Fact]
    public void Import_EventNameIsNotAStreamer()
    {
        var t = Import("BV1", "2025冰火歌会");
        Assert.Null(_repo.GetTrack(t)!.StreamerId);
        Assert.Empty(_repo.GetStreamers());
    }

    [Fact]
    public void ManualEdit_DoesNotFuzzyMerge()
    {
        var a = Import("BV1", "雾氧Uo");
        var b = Import("BV2", null);
        _repo.UpdateTracks([b], new TrackUpdate { StreamerName = "雾氧Ux" });   // 用户明确写的名字
        Assert.Equal(2, _repo.GetStreamers().Count);
        Assert.Equal("雾氧Uo", _repo.GetTrack(a)!.StreamerName);
    }

    [Fact]
    public void FillByUploader_UsesDominantStreamer()
    {
        for (var i = 0; i < 12; i++) Import($"BV{i}", "雾氧Uo", upMid: 7);
        var blank = Import("BVblank", null, upMid: 7);
        var otherUp = Import("BVother", null, upMid: 8);
        var preview = _repo.FillStreamerByUploader(apply: false);
        Assert.Single(preview);
        Assert.Null(_repo.GetTrack(blank)!.StreamerId);
        _repo.FillStreamerByUploader(apply: true);
        Assert.Equal("雾氧Uo", _repo.GetTrack(blank)!.StreamerName);
        Assert.Null(_repo.GetTrack(otherUp)!.StreamerId);
    }

    [Fact]
    public void Rename_ToExistingName_Merges_AndKeepsOldNameAsAlias()
    {
        var a = Import("BV1", "阿梓歌");
        var b = Import("BV2", "梓宝");
        var bId = _repo.GetTrack(b)!.StreamerId!.Value;
        var final = _repo.RenameStreamer(bId, "阿梓歌");
        Assert.Equal(_repo.GetTrack(a)!.StreamerId, final);
        Assert.Equal("阿梓歌", _repo.GetTrack(b)!.StreamerName);
        Assert.Contains("梓宝", _repo.GetStreamers().Single().Aliases);
    }

    [Fact]
    public void DeleteStreamer_ClearsTracksButKeepsThem()
    {
        var t = Import("BV1", "某某");
        _repo.DeleteStreamer(_repo.GetTrack(t)!.StreamerId!.Value);
        Assert.NotNull(_repo.GetTrack(t));
        Assert.Null(_repo.GetTrack(t)!.StreamerId);
    }
}
