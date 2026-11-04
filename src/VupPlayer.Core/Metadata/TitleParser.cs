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

    /// <summary>
    /// 歌名的引号。除了《》，桃几这类老切片用的是尖括号「&lt;桃几&gt;直播翻唱&lt;如愿&gt;」，
    /// 开头那个是主播、后面那个才是歌名，所以取最后一个分组（见 <see cref="LastQuoted"/>）。
    /// </summary>
    [GeneratedRegex(@"《([^》]{1,60})》|〈([^〉]{1,60})〉|<([^<>]{1,60})>")]
    private static partial Regex SongQuote();

    [GeneratedRegex(@"【([^】]{1,30})】|\[([^\]]{1,30})\]")]
    private static partial Regex Brackets();

    /// <summary>所有「标题前缀」用的括号，包括尖括号。用来剥掉它们之后看剩下什么。</summary>
    [GeneratedRegex(@"【[^】]{1,30}】|\[[^\]]{1,30}\]|<[^<>]{1,60}>")]
    private static partial Regex AnyBracket();

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
        var streamer = FindKnownStreamer(videoTitle, knownStreamers)
                       ?? GuessStreamerFromBrackets(videoTitle)
                       ?? LeadingAngleTag(videoTitle);

        string? song = null;
        // 多P视频：分P标题通常就是歌名
        if (pageCount > 1 && !string.IsNullOrWhiteSpace(partTitle))
        {
            string raw;
            if (LastQuoted(partTitle, streamer) is { } quoted) raw = quoted;
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
        if (song is null && LastQuoted(videoTitle, streamer) is { } bq) song = CleanSong(bq);
        if (song is null)
        {
            // 「【主播】歌名」或「主播 - 歌名」
            var rest = AnyBracket().Replace(videoTitle, " ").Trim();
            var dash = rest.Split([" - ", " – ", "-", "｜", "|", "丨"], 2, StringSplitOptions.TrimEntries);
            var candidate = dash.Length == 2 ? dash[1] : null;
            if (dash.Length == 2 && streamer is not null && dash[1].Contains(streamer, StringComparison.OrdinalIgnoreCase))
                candidate = dash[0];
            // 「【xx】【主播】歌名」：去掉括号后只剩一个短词，多半就是歌名。
            // 20 字的上限和「不许带空格」是防整句标题被当成歌名的（「氪星杯妮莉安Lily部分
            // 雾氧视角，碎碎念全肯定妮信的丑态」）。只有真剥掉了开头的通用词才放开空格，
            // 这样「<桃几>直播翻唱 What can I do」能拿到歌名。
            if (candidate is null && dash.Length == 1 && AnyBracket().IsMatch(videoTitle))
            {
                var tail = StripGenericPrefix(rest);
                if (tail.Length is > 0 and <= 20 && !IsGeneric(tail)
                    && (tail.Length != rest.Length || !tail.Contains(' ')))
                    candidate = tail;
            }
            if (candidate is not null) song = CleanSong(candidate);
        }
        // 「【歌名】- 主播 / 直播歌切」：方括号里不是主播、主播在括号外出现，那括号里就是歌名
        if (song is null && streamer is not null)
        {
            foreach (Match m in Brackets().Matches(videoTitle))
            {
                var inner = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                if (inner.Length == 0 || IsGeneric(inner)) continue;
                if (inner.Contains(streamer, StringComparison.OrdinalIgnoreCase)) continue;
                if (CleanSong(inner) is { } c) { song = c; break; }
            }
        }
        if (song is not null && streamer is not null && song.Equals(streamer, StringComparison.OrdinalIgnoreCase)) song = null;
        return (song, streamer);
    }

    /// <summary>
    /// 取最后一个引号分组里的内容。标题里有多个分组时，前面那些是主播或前缀标签
    /// （「&lt;桃几&gt;直播翻唱&lt;如愿&gt;」），歌名在最后。开头那个尖括号标签永远不算歌名。
    /// </summary>
    static string? LastQuoted(string text, string? streamer)
    {
        string? found = null;
        var tail = text.TrimEnd().Length;
        foreach (Match m in SongQuote().Matches(text))
        {
            // 「<主播>……」开头的尖括号是主播标签，不是歌名（但整个标题就是一个 <...> 时算歌名）
            if (m.Index == 0 && m.Groups[3].Success && m.Length < tail) continue;
            var inner = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim();
            if (inner.Length == 0 || IsGeneric(inner)) continue;
            if (streamer is not null && inner.Equals(streamer, StringComparison.OrdinalIgnoreCase)) continue;
            found = inner;
        }
        return found;
    }

    /// <summary>标题开头的「&lt;主播&gt;」尖括号标签，用法同「【主播】」。</summary>
    static string? LeadingAngleTag(string title)
    {
        var m = Regex.Match(title, @"^<([^<>]{2,16})>\s*\S");
        if (!m.Success) return null;
        var name = StripGenericSuffix(m.Groups[1].Value.Trim());
        return name.Length >= 2 && !IsGeneric(name) && !StreamerMatcher.IsNotPerson(name) ? name : null;
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
                    if (name.Length >= 2 && !IsGeneric(name) && !StreamerMatcher.IsNotPerson(name)) return name;
                    continue;
                }
                if (part.Length is < 2 or > 16) continue;
                if (IsGeneric(part) || StreamerMatcher.IsNotPerson(part)) continue;
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

    /// <summary>剥掉开头的通用词，例如「直播翻唱   What can I do」→「What can I do」。</summary>
    static string StripGenericPrefix(string s)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            s = s.TrimStart(' ', '　', '-', '_', '|', '｜', '：', ':', '/', '／', '~', '～');
            foreach (var g in GenericWords.Where(g => g.Length >= 2).OrderByDescending(g => g.Length))
            {
                if (s.Length > g.Length && s.StartsWith(g, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[g.Length..];
                    changed = true;
                }
            }
        }
        return s.Trim();
    }

    static string? CleanSong(string s)
    {
        s = LeadingNumber().Replace(s, "");
        s = TrailingNoise().Replace(s, "");
        s = s.Trim(' ', '　', '-', '_', '|', '｜', '~', '～', '!', '！');
        return s.Length is >= 1 and <= 60 ? s : null;
    }
}
