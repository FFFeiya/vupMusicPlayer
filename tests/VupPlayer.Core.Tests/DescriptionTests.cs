using Microsoft.Data.Sqlite;
using VupPlayer.Bilibili;
using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Core.Tests;

public class DescriptionParserTests
{
    const string Single = "雾氧Uo2023.6.9直播歌切\n\n\n【雾氧Uo直播间】\nhttps://live.bilibili.com/27206038\n【雾氧Uo个人主页】\nhttps://space.bilibili.com/3493078891497932";
    const string SingleInline = "雾氧Uo 2024年12月9日直播翻唱\n【雾氧Uo直播间】https://live.bilibili.com/27206038\n【雾氧Uo个人主页】https://space.bilibili.com/3493078891497932";
    const string Chorus = "原曲：袁娅维 - 说散就散\n桃代Momoka：https://space.bilibili.com/2040984069\n说说Crystal：https://space.bilibili.com/283886865\n" +
                          "有棵里里：https://space.bilibili.com/12939237\n雾氧Uo：https://space.bilibili.com/3493078891497932\n" +
                          "妮莉安Lily：https://space.bilibili.com/3493271057730096\n溪璐Shiro：https://space.bilibili.com/3546655741708943";

    [Theory]
    [InlineData(Single)]
    [InlineData(SingleInline)]
    public void SingleStreamer_NameAndMid(string desc)
    {
        var m = Assert.Single(DescriptionParser.Parse(desc));
        Assert.Equal("雾氧Uo", m.Name);
        Assert.Equal(3493078891497932, m.Mid);
    }

    [Fact]
    public void Chorus_AllSixWithMids_OriginalSingerIgnored()
    {
        var list = DescriptionParser.Parse(Chorus);
        Assert.Equal(6, list.Count);
        Assert.All(list, m => Assert.NotNull(m.Mid));
        Assert.Contains(list, m => m.Name == "雾氧Uo" && m.Mid == 3493078891497932);
        Assert.DoesNotContain(list, m => m.Name is not null && m.Name.Contains("袁娅维"));
    }

    [Fact]
    public void UploaderOwnSpace_IsExcluded()
    {
        var desc = "切片：https://space.bilibili.com/111\n主播：https://space.bilibili.com/222";
        var list = DescriptionParser.Parse(desc, excludeMid: 111);
        Assert.Equal([222L], list.Select(m => m.Mid!.Value));
    }

    [Fact]
    public void LinkWithoutName_GetsNameFromLiveRoomLine()
    {
        var desc = "【阿梓直播间】https://live.bilibili.com/510\n主页 https://space.bilibili.com/7706705";
        var m = Assert.Single(DescriptionParser.Parse(desc));
        Assert.Equal("阿梓", m.Name);
        Assert.Equal(7706705, m.Mid);
    }

    [Fact]
    public void HostColonAndAt()
    {
        var list = DescriptionParser.Parse("主播：七海Nana7mi\n感谢 @某观众 的点歌");
        Assert.Contains(list, m => m.Name == "七海Nana7mi" && m.Source == "简介「主播：」");
        Assert.Contains(list, m => m.Name == "某观众" && m.Source == "简介 @");
    }

    [Fact]
    public void Empty() => Assert.Empty(DescriptionParser.Parse("-"));

    [Fact]
    public void NameSpacePersonalPageColon()
    {
        const string desc = "因为看的时候给我看晕了\n夜莲Annbbers@夜莲Annbbers 个人主页：space.bilibili.com/1629467333\n夜莲Annbbers直播间：live.bilibili.com/24160384";
        var m = Assert.Single(DescriptionParser.Parse(desc));
        Assert.Equal("夜莲Annbbers", m.Name);
        Assert.Equal(1629467333, m.Mid);
    }

    [Fact]
    public void ClippedFromAt_IsIgnored() =>
        Assert.Empty(DescriptionParser.Parse("雾氧Uo2023.6.28\n切自@氧史蛋子"));
}

public class StreamerIdentifierEdgeTests
{
    [Fact]
    public async Task AtOnly_DoesNotOverrideTitleStreamer()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), $"vup-e-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var id = new StreamerIdentifier(null, new LibraryRepository(db));
        Assert.Null(await id.FromDescriptionAsync("@某个二创作者", 1, titleStreamer: "雾氧Uo"));
        SqliteConnection.ClearAllPools();
    }
}

public sealed class StreamerIdentifierTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"vup-id-{Guid.NewGuid():N}.db");
    readonly LibraryRepository _repo;

    public StreamerIdentifierTests()
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

    long Import(string bvid, string? streamer, string? desc = null, long upMid = 1) =>
        _repo.ImportVideo(new Video { Bvid = bvid, Title = bvid, UpMid = upMid, UpName = "up", Description = desc },
            [new Track { Bvid = bvid, Cid = bvid.GetHashCode(), Page = 1 }], _ => ("歌", streamer))[0];

    [Fact]
    public async Task Single_TitleTypoBecomesAlias()
    {
        var id = new StreamerIdentifier(null, _repo);
        var d = await id.FromDescriptionAsync("【雾氧Uo个人主页】https://space.bilibili.com/349", 1, titleStreamer: "雾氧Up");
        Assert.NotNull(d);
        Assert.Equal("雾氧Uo", d!.Name);
        Assert.Equal(349, d.Mid);
        Assert.Equal("雾氧Up", d.ExtraAlias);
        Assert.Empty(d.Tags);
    }

    [Fact]
    public async Task Chorus_PrefersStreamerAlreadyInLibrary_AndTagsChorus()
    {
        var t = Import("BV0", "雾氧Uo");
        _repo.SetStreamerMid(_repo.GetTrack(t)!.StreamerId!.Value, 3493078891497932);
        var id = new StreamerIdentifier(null, _repo);
        var d = await id.FromDescriptionAsync(
            "桃代Momoka：https://space.bilibili.com/2040984069\n雾氧Uo：https://space.bilibili.com/3493078891497932", 1, null);
        Assert.Equal("雾氧Uo", d!.Name);
        Assert.Contains(StreamerIdentifier.ChorusTag, d.Tags);
    }

    [Fact]
    public async Task AtMentionAlone_IsUsedOnlyIfItIsTheOnlyEvidence()
    {
        var id = new StreamerIdentifier(null, _repo);
        Assert.Equal("某人", (await id.FromDescriptionAsync("@某人", 1, null))!.Name);
        var d = await id.FromDescriptionAsync("主播：雾氧Uo\n@观众甲 点的歌", 1, null);
        Assert.Equal("雾氧Uo", d!.Name);
        Assert.Empty(d.Tags);   // @ 不算第二个主播
    }

    [Fact]
    public void ImportWithMid_FindsStreamerByMidEvenIfNameDiffers()
    {
        var t = Import("BV0", "雾氧Uo");
        _repo.SetStreamerMid(_repo.GetTrack(t)!.StreamerId!.Value, 349);
        var created = _repo.ImportVideo(new Video { Bvid = "BV1", Title = "x", UpMid = 1, UpName = "up" },
            [new Track { Bvid = "BV1", Cid = 1, Page = 1 }], _ => new TrackGuess("歌", "完全不同的写法", 349, null, ["合唱"]));
        var tr = _repo.GetTrack(created[0])!;
        Assert.Equal("雾氧Uo", tr.StreamerName);
        Assert.Equal("合唱", tr.Tags);
    }

    [Fact]
    public async Task ScanLibrary_FillsMissing_RecordsMid_AndTagsChorus()
    {
        var a = Import("BVa", "雾氧Uo", "【雾氧Uo个人主页】https://space.bilibili.com/349");
        var b = Import("BVb", null, "【雾氧Uo个人主页】https://space.bilibili.com/349");
        var c = Import("BVc", "雾氧Uo", "甲某：https://space.bilibili.com/1000\n雾氧Uo：https://space.bilibili.com/349");
        var id = new StreamerIdentifier(null, _repo);

        var preview = await id.ScanLibraryAsync(apply: false);
        Assert.Equal(3, preview.Count);   // a 补 UID，b 补主播，c 加合唱
        Assert.Null(_repo.GetTrack(b)!.StreamerId);

        await id.ScanLibraryAsync(apply: true);
        Assert.Equal("雾氧Uo", _repo.GetTrack(b)!.StreamerName);
        Assert.Equal(349, _repo.GetStreamers().Single(s => s.Name == "雾氧Uo").Mid);
        Assert.Equal("合唱", _repo.GetTrack(c)!.Tags);
        Assert.Empty(await new StreamerIdentifier(null, _repo).ScanLibraryAsync(apply: false));   // 再扫一遍没有改动
        Assert.Equal("雾氧Uo", _repo.GetTrack(a)!.StreamerName);
    }
}
