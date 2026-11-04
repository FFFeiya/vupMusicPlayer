using VupPlayer.Bilibili;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Core.Tests;

public class TitleParserTests
{
    [Theory]
    [InlineData("【七海Nana7mi】《小幸运》翻唱", "小幸运", "七海Nana7mi")]
    [InlineData("【歌回切片】【星瞳】晴天", "晴天", "星瞳")]
    [InlineData("【嘉然】直播唱《想去海边》2024.05.01", "想去海边", "嘉然")]
    [InlineData("【中字/高音质】【塔菲】- 稻香", "稻香", "塔菲")]
    [InlineData("[瓦妮vanee歌回切片]月声尽，心灯明", "月声尽，心灯明", "瓦妮vanee")]
    public void Guess_CommonFormats(string title, string song, string streamer)
    {
        var (s, st) = TitleParser.Guess(title, null, 1);
        Assert.Equal(song, s);
        Assert.Equal(streamer, st);
    }

    [Fact]
    public void Guess_PrefersKnownStreamer_LongestMatch()
    {
        var (_, st) = TitleParser.Guess("七海Nana7mi 唱《起风了》", null, 1, ["七海", "七海Nana7mi"]);
        Assert.Equal("七海Nana7mi", st);
    }

    [Fact]
    public void Guess_MultiPage_UsesPartTitle()
    {
        var (s, _) = TitleParser.Guess("【星瞳】10月歌回合集", "03 《后来》", 12);
        Assert.Equal("后来", s);
    }

    [Theory]
    [InlineData("19Powder Snow-婉婉婉苑", "Powder Snow")]
    [InlineData("18天天-火虫の沸羊羊", "天天")]
    [InlineData("05 2002年的第一场雪", "2002年的第一场雪")]
    [InlineData("P3 晴天", "晴天")]
    public void Guess_MultiPage_GluedNumberAndOriginalSinger(string part, string song)
    {
        var (s, _) = TitleParser.Guess("【烛不遥】歌回合集", part, 30);
        Assert.Equal(song, s);
    }

    [Fact]
    public void Guess_MultiPage_GenericPartTitle_FallsBack()
    {
        var (s, _) = TitleParser.Guess("【星瞳】《晴天》", "P1", 2);
        Assert.Equal("晴天", s);
    }

    [Fact]
    public void Guess_NameEndingWithSingleCharGenericWord()
    {
        var (_, st) = TitleParser.Guess("【阿梓歌】2026.10.13歌回：本宝宝来唱歌了", "后来", 18);
        Assert.Equal("阿梓歌", st);
    }

    [Fact]
    public void Guess_NoHints_ReturnsNulls()
    {
        var (s, st) = TitleParser.Guess("今天的直播好好笑", null, 1);
        Assert.Null(s);
        Assert.Null(st);
    }

    [Theory]
    // 桃几的老切片用尖括号：开头那个是主播，后面那个才是歌名
    [InlineData("<桃几>直播翻唱<如愿>", "如愿", "桃几")]
    [InlineData("<桃几>直播切片<搁浅>", "搁浅", "桃几")]
    [InlineData("<桃几>直播翻唱<影子>(升1key)", "影子", "桃几")]
    [InlineData("<桃几>直播翻唱<左手指月>(第2版)", "左手指月", "桃几")]
    // 开头的尖括号没闭合，只剩歌名那个分组
    [InlineData("<桃几.直播翻唱<独角戏>", "独角戏", null)]
    public void Guess_AngleBrackets(string title, string song, string? streamer)
    {
        var (s, st) = TitleParser.Guess(title, null, 1);
        Assert.Equal(song, s);
        if (streamer is not null) Assert.Equal(streamer, st);
    }

    [Theory]
    // 歌名没加括号，只能靠剥掉开头的「直播翻唱」拿到
    [InlineData("<桃几>直播翻唱 Lydia", "Lydia")]
    [InlineData("<桃几>直播翻唱   What can I do", "What can I do")]
    [InlineData("<桃几>直播翻唱  again（桃神圆圆版）", "again")]
    public void Guess_GenericPrefixStripped(string title, string song)
    {
        var (s, _) = TitleParser.Guess(title, null, 1);
        Assert.Equal(song, s);
    }

    [Theory]
    // 「【歌名】- 主播 / 直播歌切」：主播在括号外，括号里就是歌名
    [InlineData("【不老梦】- 法里达 / 直播歌切", "不老梦")]
    [InlineData("【世界上的另一个我】- 法里达 / 直播歌切", "世界上的另一个我")]
    [InlineData("【澎湃】- 法里达 / 直播歌切", "澎湃")]
    public void Guess_SongInBrackets_StreamerOutside(string title, string song)
    {
        var (s, st) = TitleParser.Guess(title, null, 1, ["法里达"]);
        Assert.Equal(song, s);
        Assert.Equal("法里达", st);
    }

    [Theory]
    // 标题是歌词或梗，不是歌名：宁可留空，让人工去填
    [InlineData("停留在冬夜的冷风中  我不是也不想装脆弱【雾氧Uo直播翻唱】")]
    [InlineData("底边实力歌势氪星杯落选 直播破防嗦面【雾氧Uo】")]
    [InlineData("Happy Halloween！一首甜美可爱的日文歌【雾氧Uo直播翻唱】")]
    // 没空格但是整句，靠 20 字上限挡住
    [InlineData("氪星杯妮莉安Lily部分雾氧视角，碎碎念全肯定妮信的丑态！！【雾氧Uo】")]
    [InlineData("クリームソーダとシャンデリア奶油苏打和冕形灯【雾氧Uo直播翻唱】")]
    [InlineData("光るなら/若能绽放光芒，四月马上就来了，还有38分钟。【雾氧Uo直播翻唱】")]
    public void Guess_LyricOrMemeTitle_NoSong(string title)
    {
        var (s, _) = TitleParser.Guess(title, null, 1, ["雾氧Uo"]);
        Assert.Null(s);
    }
}

public class LinkParserTests
{
    [Theory]
    [InlineData("BV1NsnM6DEz5", "BV1NsnM6DEz5", null)]
    [InlineData("https://www.bilibili.com/video/BV1NsnM6DEz5/?p=3&spm_id_from=333", "BV1NsnM6DEz5", 3)]
    [InlineData("看看这个 https://m.bilibili.com/video/BV1NsnM6DEz5", "BV1NsnM6DEz5", null)]
    public void Video(string input, string bvid, int? page)
    {
        var l = LinkParser.Parse(input);
        Assert.Equal(LinkKind.Video, l.Kind);
        Assert.Equal(bvid, l.Bvid);
        Assert.Equal(page, l.Page);
    }

    [Fact]
    public void Av() => Assert.Equal(170001, LinkParser.Parse("https://www.bilibili.com/video/av170001").Aid);

    [Theory]
    [InlineData("https://space.bilibili.com/123/favlist?fid=456789&ftype=create", 456789)]
    [InlineData("https://www.bilibili.com/medialist/detail/ml456789", 456789)]
    [InlineData("https://www.bilibili.com/list/ml456789", 456789)]
    public void Fav(string input, long mediaId)
    {
        var l = LinkParser.Parse(input);
        Assert.Equal(LinkKind.Fav, l.Kind);
        Assert.Equal(mediaId, l.MediaId);
    }

    [Theory]
    [InlineData("https://space.bilibili.com/1234/channel/collectiondetail?sid=5678")]
    [InlineData("https://space.bilibili.com/1234/lists/5678?type=season")]
    public void Season(string input)
    {
        var l = LinkParser.Parse(input);
        Assert.Equal(LinkKind.Season, l.Kind);
        Assert.Equal(1234, l.Mid);
        Assert.Equal(5678, l.SeasonId);
    }

    [Theory]
    [InlineData("https://space.bilibili.com/1234")]
    [InlineData("https://space.bilibili.com/1234/video")]
    [InlineData("https://space.bilibili.com/1234/upload/video")]
    public void Space(string input)
    {
        var l = LinkParser.Parse(input);
        Assert.Equal(LinkKind.Space, l.Kind);
        Assert.Equal(1234, l.Mid);
    }

    [Fact]
    public void ShortLink() => Assert.Equal(LinkKind.ShortLink, LinkParser.Parse("【分享】 https://b23.tv/AbCd123").Kind);

    [Fact]
    public void Garbage() => Assert.Equal(LinkKind.Invalid, LinkParser.Parse("hello").Kind);

    [Fact]
    public void SplitInputs_HandlesNewlinesAndCommas() =>
        Assert.Equal(3, LinkParser.SplitInputs("BV1NsnM6DEz5\nBV1WwJy6EEMg，BV1XJJ96oEXg").Count);
}

public class StreamLineTests
{
    [Fact]
    public void BuildCandidates_DomesticPrimaryFirst_ThenBackups_ThenMirrors()
    {
        var prefs = new StreamPreferences();
        var lines = prefs.BuildCandidates(
            "https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/a.m4s?deadline=1",
            ["https://upos-sz-mirrorhw.bilivideo.com/upgcxcode/a.m4s?deadline=1"]);
        Assert.StartsWith("https://upos-sz-mirrorcos.bilivideo.com/", lines[0]);
        Assert.StartsWith("https://upos-sz-mirrorhw.bilivideo.com/", lines[1]);
        Assert.StartsWith("https://upos-sz-mirrorali.bilivideo.com/", lines[2]);
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public void BuildCandidates_OverseasPrimary_DomesticMirrorsFirst()
    {
        var prefs = new StreamPreferences();
        var lines = prefs.BuildCandidates(
            "https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/a.m4s?deadline=1&os=cosovbv",
            ["https://upos-hz-mirrorakam.akamaized.net/upgcxcode/a.m4s?hdnts=x"]);
        // 前三条是国内镜像（取 Mirrors 的前 3 个），之后才是 B 站原本分配的海外地址
        Assert.StartsWith("https://upos-sz-mirrorali.bilivideo.com/upgcxcode/a.m4s?deadline=1&os=cosovbv", lines[0]);
        Assert.StartsWith("https://upos-sz-mirrorhw.bilivideo.com/", lines[1]);
        Assert.StartsWith("https://upos-sz-mirrorcos.bilivideo.com/", lines[2]);
        Assert.StartsWith("https://upos-sz-mirrorcosov.bilivideo.com/", lines[3]);
        Assert.StartsWith("https://upos-hz-mirrorakam.akamaized.net/", lines[4]);
        Assert.Equal(lines.Count, lines.Distinct().Count());
        // akamai 地址的签名不同，不能拿来换服务器名
        Assert.DoesNotContain(lines, l => l.Contains("akamaized") && l.Contains("deadline"));
    }

    [Fact]
    public void BuildCandidates_PreferredMirrorFirst()
    {
        var prefs = new StreamPreferences { PreferredMirror = "upos-sz-mirrorhw.bilivideo.com" };
        var lines = prefs.BuildCandidates("https://upos-sz-mirrorcosov.bilivideo.com/x.m4s?a=1", []);
        Assert.StartsWith("https://upos-sz-mirrorhw.bilivideo.com/", lines[0]);
    }

    [Fact]
    public void ResolvedStream_LinesCycle()
    {
        var s = new VupPlayer.Core.Streams.ResolvedStream("v0", "a0", new Dictionary<string, string>(), DateTimeOffset.MaxValue, "t",
            VideoCandidates: ["v0", "v1"], AudioCandidates: ["a0", "a1", "a2"]);
        Assert.Equal(3, s.LineCount);
        Assert.Equal("v0", s.VideoAt(2));
        Assert.Equal("a2", s.AudioAt(2));
    }
}

public class LineHealthTests
{
    [Fact]
    public void FirstHealthyLine_SkipsRecentlyBadHosts_UntilExpired()
    {
        var s = new VupPlayer.Core.Streams.ResolvedStream(null, "https://a.com/x", new Dictionary<string, string>(), DateTimeOffset.MaxValue, "t",
            AudioCandidates: ["https://a.com/x", "https://b.com/x", "https://c.com/x"]);
        var health = new VupPlayer.Core.Streams.LineHealth(TimeSpan.FromMinutes(10));
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Equal(0, s.FirstHealthyLine(health, t0));
        health.MarkBad("a.com", t0);
        health.MarkBad("b.com", t0);
        Assert.Equal(2, s.FirstHealthyLine(health, t0.AddMinutes(1)));
        health.MarkBad("c.com", t0);
        Assert.Equal(0, s.FirstHealthyLine(health, t0.AddMinutes(1)));   // 都坏了就从头来
        Assert.Equal(0, s.FirstHealthyLine(health, t0.AddMinutes(11)));
    }
}

public class SearchParsingTests
{
    [Theory]
    [InlineData("4:6", 246)]
    [InlineData("12:34", 754)]
    [InlineData("1:02:03", 3723)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void ParseDuration(string s, int expected) => Assert.Equal(expected, BiliApi.ParseDuration(s));

    [Fact]
    public void CleanHtml_RemovesHighlightAndEntities() =>
        Assert.Equal("【阿梓】《绝不认输》 & 合集",
            BiliApi.CleanHtml("【<em class=\"keyword\">阿梓</em>】《<em class=\"keyword\">绝不认输</em>》 &amp; 合集"));
}

public class WbiSignerTests
{
    // 社区文档里的公开示例
    [Fact]
    public void Sign_MatchesKnownVector()
    {
        var mixin = WbiSigner.MixinKey("7cd084941338484aae1ad9425b84077c", "4932caff0ff746eab6f01bf08b70ac45");
        Assert.Equal("ea1db124af3c7062474693fa704f4ff8", mixin);
        var q = WbiSigner.Sign([new("foo", "114"), new("bar", "514"), new("zab", "1919810")], mixin, 1702204169);
        Assert.Equal("bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4", q);
    }

    [Fact]
    public void Sign_FiltersSpecialCharsAndEncodes()
    {
        var q = WbiSigner.Sign([new("keyword", "a b!'()*中")], "k", 1);
        Assert.StartsWith("keyword=a%20b%E4%B8%AD&wts=1&w_rid=", q);
    }

    [Fact]
    public void KeyFromUrl() =>
        Assert.Equal("7cd084941338484aae1ad9425b84077c", WbiSigner.KeyFromUrl("https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png"));
}
