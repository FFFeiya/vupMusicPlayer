using System.Text.RegularExpressions;

namespace VupPlayer.Bilibili;

public enum LinkKind { Video, Fav, Season, Space, Live, ShortLink, Other, Invalid }

public sealed record ParsedLink(LinkKind Kind, string? Bvid = null, long? Aid = null, int? Page = null,
    long? MediaId = null, long? Mid = null, long? SeasonId = null, long? RoomId = null, string? Url = null);

/// <summary>识别用户粘贴的各种 B 站链接或编号。</summary>
public static partial class LinkParser
{
    [GeneratedRegex(@"(?:b23\.tv|bili2233\.cn)/[A-Za-z0-9]+", RegexOptions.IgnoreCase)] private static partial Regex Short();
    [GeneratedRegex(@"[?&]fid=(\d+)|/ml(\d+)|medialist/(?:detail|play)/ml(\d+)", RegexOptions.IgnoreCase)] private static partial Regex Fav();
    [GeneratedRegex(@"space\.bilibili\.com/(\d+)/(?:channel/collectiondetail\?sid=(\d+)|lists/(\d+)\?type=season)", RegexOptions.IgnoreCase)] private static partial Regex Season();
    [GeneratedRegex(@"space\.bilibili\.com/(\d+)(?:/(?:video|upload/video|upload))?/?(?:[?#].*)?$", RegexOptions.IgnoreCase)] private static partial Regex Space();
    [GeneratedRegex(@"(BV[0-9A-Za-z]{10})")] private static partial Regex Bv();
    [GeneratedRegex(@"(?:^|[^a-z0-9])av(\d{1,15})(?![0-9])", RegexOptions.IgnoreCase)] private static partial Regex Av();
    [GeneratedRegex(@"[?&]p=(\d+)")] private static partial Regex PageParam();
    [GeneratedRegex(@"live\.bilibili\.com/(?:h5/|blanc/)?(\d+)", RegexOptions.IgnoreCase)] private static partial Regex Live();

    public static ParsedLink Parse(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return new ParsedLink(LinkKind.Invalid);

        if (Short().Match(s) is { Success: true } sm)
            return new ParsedLink(LinkKind.ShortLink, Url: "https://" + sm.Value);

        // 直播间链接要在其它 bilibili.com 规则之前判断，否则会掉进 Other
        if (Live().Match(s) is { Success: true } lm && long.TryParse(lm.Groups[1].Value, out var roomId) && roomId > 0)
            return new ParsedLink(LinkKind.Live, RoomId: roomId, Url: s);

        int? page = PageParam().Match(s) is { Success: true } pm ? int.Parse(pm.Groups[1].Value) : null;

        if (s.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) && !Bv().IsMatch(s)
            && Fav().Match(s) is { Success: true } fm)
        {
            var id = fm.Groups.Values.Skip(1).First(g => g.Success).Value;
            return new ParsedLink(LinkKind.Fav, MediaId: long.Parse(id), Url: s);
        }

        if (Season().Match(s) is { Success: true } se)
        {
            var sid = se.Groups[2].Success ? se.Groups[2].Value : se.Groups[3].Value;
            return new ParsedLink(LinkKind.Season, Mid: long.Parse(se.Groups[1].Value), SeasonId: long.Parse(sid), Url: s);
        }

        if (Bv().Match(s) is { Success: true } bm)
            return new ParsedLink(LinkKind.Video, Bvid: bm.Groups[1].Value, Page: page, Url: s);

        if (Av().Match(s) is { Success: true } am)
            return new ParsedLink(LinkKind.Video, Aid: long.Parse(am.Groups[1].Value), Page: page, Url: s);

        if (Space().Match(s) is { Success: true } sp)
            return new ParsedLink(LinkKind.Space, Mid: long.Parse(sp.Groups[1].Value), Url: s);

        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && s.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase))
            return new ParsedLink(LinkKind.Other, Url: s);

        return new ParsedLink(LinkKind.Invalid, Url: s);
    }

    /// <summary>从一段文本里拆出多条输入（按空白和换行分隔）。</summary>
    public static IReadOnlyList<string> SplitInputs(string text) =>
        text.Split(['\r', '\n', ' ', '\t', '，', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
