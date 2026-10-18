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
