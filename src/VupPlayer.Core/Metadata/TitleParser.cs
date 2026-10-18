using System.Text.RegularExpressions;

namespace VupPlayer.Core.Metadata;

/// <summary>
/// 从切片标题里猜主播和歌名的简单规则。不追求准确，只给一个起点，后续交给 Claude 或手动修正。
/// </summary>
public static partial class TitleParser
{
    /// <summary>方括号里常见、但不是主播名的词。</summary>
    static readonly string[] GenericWords =
    [
        "切片", "歌切", "歌回", "翻唱", "直播", "录播", "熟肉", "生肉", "中字", "合集", "高音质", "无损", "4k", "1080p",
        "60帧", "60fps", "纯享", "完整版", "cover", "歌曲", "歌", "唱", "弹唱", "清唱", "钢琴", "吉他", "新曲", "原创",
        "vup", "vtuber", "虚拟主播", "整活", "高能", "名场面", "官方", "首发", "试听", "混剪", "mv", "live", "音频",
    ];

    [GeneratedRegex(@"《([^》]{1,60})》")]
    private static partial Regex BookQuote();

    [GeneratedRegex(@"【([^】]{1,30})】|\[([^\]]{1,30})\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^\s*(?:P?\d{1,3}[\s.、_\-]+)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^\s*(?:P|p)?\d{1,3}(?!\d)[\s.、_\-]*(?=\S)")]
    private static partial Regex GluedNumber();

    [GeneratedRegex(@"^(?:P|p|第)?\d{1,3}(?:P|p|集|期)?$")]
    private static partial Regex PageLabel();

    [GeneratedRegex(@"[（(][^）)]*[)）]|\s+(?:cover|翻唱|弹唱|清唱|live|ver\.?).*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNoise();

    /// <summary>
    /// 猜测结果。knownStreamers 是曲库里已有的主播名（含别名），标题里出现就优先采用。
    /// </summary>
    public static (string? Song, string? Streamer) Guess(
        string videoTitle, string? partTitle, int pageCount, IReadOnlyCollection<string>? knownStreamers = null)
    {
        var streamer = FindKnownStreamer(videoTitle, knownStreamers) ?? GuessStreamerFromBrackets(videoTitle);

        string? song = null;
        // 多P视频：分P标题通常就是歌名
        if (pageCount > 1 && !string.IsNullOrWhiteSpace(partTitle))
        {
            string raw;
            if (BookQuote().Match(partTitle) is { Success: true } m) raw = m.Groups[1].Value;
            else
            {
                // 合集分P常见「19Powder Snow-婉婉婉苑」：去掉序号，横杠后面通常是原唱
                raw = GluedNumber().Replace(partTitle, "");
                var dash = raw.IndexOfAny(['-', '－', '—']);
                if (dash > 0) raw = raw[..dash];
            }
            song = CleanSong(raw);
            if (song is not null && (IsGeneric(song) || PageLabel().IsMatch(song))) song = null;
        }
        if (song is null && BookQuote().Match(videoTitle) is { Success: true } bm)
            song = CleanSong(bm.Groups[1].Value);
        if (song is null)
        {
            // 「【主播】歌名」或「主播 - 歌名」
            var rest = Brackets().Replace(videoTitle, " ").Trim();
            var dash = rest.Split([" - ", " – ", "-", "｜", "|", "丨"], 2, StringSplitOptions.TrimEntries);
            var candidate = dash.Length == 2 ? dash[1] : null;
            if (dash.Length == 2 && streamer is not null && dash[1].Contains(streamer, StringComparison.OrdinalIgnoreCase))
                candidate = dash[0];
            // 「【xx】【主播】歌名」：去掉方括号后只剩一个短词，多半就是歌名
            if (candidate is null && dash.Length == 1 && Brackets().IsMatch(videoTitle)
                && rest.Length is > 0 and <= 20 && !rest.Contains(' ') && !IsGeneric(rest))
                candidate = rest;
            if (candidate is not null) song = CleanSong(candidate);
        }
        if (song is not null && streamer is not null && song.Equals(streamer, StringComparison.OrdinalIgnoreCase)) song = null;
        return (song, streamer);
    }

    static string? FindKnownStreamer(string title, IReadOnlyCollection<string>? known)
    {
        if (known is null || known.Count == 0) return null;
        // 优先匹配最长的名字，避免「七海」抢掉「七海Nana7mi」
        return known.Where(k => k.Length >= 2 && title.Contains(k, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => k.Length).FirstOrDefault();
    }

    static string? GuessStreamerFromBrackets(string title)
    {
        foreach (Match m in Brackets().Matches(title))
        {
            var inner = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            foreach (var part in inner.Split(['/', '／', '|', '｜', ' ', '·', '&', '×'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 去掉粘在名字后面的通用词，例如「瓦妮vanee歌回切片」→「瓦妮vanee」
                var name = StripGenericSuffix(part);
                if (name != part)
                {
                    if (name.Length >= 2 && !IsGeneric(name)) return name;
                    continue;
                }
                if (part.Length is < 2 or > 16) continue;
                if (IsGeneric(part)) continue;
                if (part.Any(char.IsDigit) && part.All(ch => char.IsDigit(ch) || ch is '.' or '-' or '/')) continue;   // 日期
                return part;
            }
        }
        return null;
    }

    static bool IsGeneric(string s)
    {
        var lower = s.ToLowerInvariant();
        // 单字通用词（歌、唱）只做精确匹配，否则「阿梓歌」这类名字会被误伤
        return GenericWords.Any(g => lower == g || (g.Length >= 2 && lower.Length <= g.Length + 2 && lower.Contains(g)));
    }

    static string StripGenericSuffix(string s)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var g in GenericWords.Where(g => g.Length >= 2).OrderByDescending(g => g.Length))
            {
                if (s.Length > g.Length + 1 && s.EndsWith(g, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^g.Length].TrimEnd();
                    changed = true;
                }
            }
        }
        return s;
    }

    static string? CleanSong(string s)
    {
        s = LeadingNumber().Replace(s, "");
        s = TrailingNoise().Replace(s, "");
        s = s.Trim(' ', '　', '-', '_', '|', '｜', '~', '～', '!', '！');
        return s.Length is >= 1 and <= 60 ? s : null;
    }
}
