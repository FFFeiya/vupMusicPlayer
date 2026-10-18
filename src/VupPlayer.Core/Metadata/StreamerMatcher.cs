using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VupPlayer.Core.Metadata;

public enum StreamerMatchKind { None, Exact, Alias, Normalized, Fuzzy }

public sealed record StreamerMatch(long StreamerId, string Name, StreamerMatchKind Kind, int Distance);

/// <summary>一个已知主播（名字 + 别名 + 曲目数），用于匹配。</summary>
public sealed record KnownStreamer(long Id, string Name, IReadOnlyList<string> Aliases, int TrackCount);

/// <summary>
/// 主播名识别：规范化、别名、错别字容错、非人名判断。
/// 例子：「雾氧Up」和「雾氧Uo」只差一个字 → 同一个人；「雾氧Uo直播翻唱」去掉后缀 → 雾氧Uo；「2025冰火歌会」不是人名。
/// </summary>
public static partial class StreamerMatcher
{
    /// <summary>名字后面常粘着的说明性后缀，规范化时去掉。</summary>
    static readonly string[] Suffixes =
    [
        "直播翻唱", "直播歌切", "直播切片", "直播歌回", "直播", "翻唱", "歌切", "切片", "歌回", "录播", "official", "官方", "频道", "channel",
    ];

    /// <summary>出现这些词的多半是活动、企划或组合，不是一个主播。</summary>
    static readonly string[] EventWords =
    [
        "歌会", "演唱会", "生日会", "周年", "联动", "合唱", "企划", "纪念", "晚会", "春晚", "年会", "跨年", "三小只", "合集", "大赛", "比赛", "音乐节",
    ];

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex Year();

    [GeneratedRegex(@"[\s\p{P}\p{S}]")]
    private static partial Regex PunctAndSpace();

    /// <summary>全角转半角、小写、去空白标点、去说明性后缀。</summary>
    public static string Normalize(string name)
    {
        var s = name.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        s = PunctAndSpace().Replace(s, "");
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suf in Suffixes)
            {
                if (s.Length > suf.Length + 1 && s.EndsWith(suf, StringComparison.Ordinal))
                {
                    s = s[..^suf.Length];
                    changed = true;
                }
            }
        }
        return s;
    }

    /// <summary>看起来不是一个人名（活动名、年份、组合）。</summary>
    public static bool IsNotPerson(string name) =>
        Year().IsMatch(name) || EventWords.Any(w => name.Contains(w, StringComparison.Ordinal));

    /// <summary>按文本元素（字）计算的编辑距离。</summary>
    public static int EditDistance(string a, string b)
    {
        var x = Elements(a);
        var y = Elements(b);
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) prev[j] = j;
        for (var i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[y.Length];
    }

    static string[] Elements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list.ToArray();
    }

    static int Length(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>
    /// 两个规范化后的名字是否像同一个人打错字：长度至少 4 个字、只差 1 个字、开头两个字相同。
    /// 开头相同的要求可以挡掉「七海 / 七濑」这类真正不同的人。
    /// </summary>
    public static bool LooksLikeTypo(string normA, string normB)
    {
        if (normA == normB) return false;
        if (Math.Min(Length(normA), Length(normB)) < 4) return false;
        var ea = Elements(normA);
        var eb = Elements(normB);
        if (ea[0] != eb[0] || ea[1] != eb[1]) return false;
        return EditDistance(normA, normB) == 1;
    }

    /// <summary>
    /// 在已知主播里找这个名字。allowFuzzy 只在自动导入时开启：用户或 Claude 明确写的名字不做错别字合并。
    /// </summary>
    public static StreamerMatch? Match(string name, IReadOnlyList<KnownStreamer> known, bool allowFuzzy)
    {
        var trimmed = name.Trim();
        foreach (var k in known)
            if (string.Equals(k.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return new(k.Id, k.Name, StreamerMatchKind.Exact, 0);
        foreach (var k in known)
            if (k.Aliases.Any(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase))) return new(k.Id, k.Name, StreamerMatchKind.Alias, 0);

        var norm = Normalize(trimmed);
        if (norm.Length == 0) return null;
        foreach (var k in known)
            if (Normalize(k.Name) == norm || k.Aliases.Any(a => Normalize(a) == norm)) return new(k.Id, k.Name, StreamerMatchKind.Normalized, 0);

        if (!allowFuzzy) return null;
        // 错别字：只接受唯一的候选，并且优先曲目多的（更可能是「正确」的那个）
        var hits = known.Where(k => LooksLikeTypo(norm, Normalize(k.Name)) || k.Aliases.Any(a => LooksLikeTypo(norm, Normalize(a))))
                        .OrderByDescending(k => k.TrackCount).ToList();
        if (hits.Count == 1 || (hits.Count > 1 && hits[0].TrackCount >= 3 * hits[1].TrackCount))
            return new(hits[0].Id, hits[0].Name, StreamerMatchKind.Fuzzy, 1);
        return null;
    }

    public sealed record SimilarPair(KnownStreamer A, KnownStreamer B, string Reason);

    /// <summary>找出疑似同一个人的主播对，以及看起来不是人名的主播。</summary>
    public static IReadOnlyList<SimilarPair> FindSimilar(IReadOnlyList<KnownStreamer> known)
    {
        var result = new List<SimilarPair>();
        for (var i = 0; i < known.Count; i++)
        {
            for (var j = i + 1; j < known.Count; j++)
            {
                var a = known[i];
                var b = known[j];
                var na = Normalize(a.Name);
                var nb = Normalize(b.Name);
                string? reason = null;
                if (na == nb) reason = "去掉后缀、大小写和标点后相同";
                else if (LooksLikeTypo(na, nb)) reason = "只差一个字，像是打错字";
                else if (na.Length >= 2 && nb.Length >= 2 && (na.StartsWith(nb) || nb.StartsWith(na)) && Math.Abs(Length(na) - Length(nb)) <= 2)
                    reason = "一个名字是另一个加了一两个字";
                else if (a.Aliases.Concat([a.Name]).Any(x => b.Aliases.Concat([b.Name]).Any(y => Normalize(x) == Normalize(y))))
                    reason = "别名重叠";
                if (reason is not null)
                {
                    // 曲目多的放前面，作为建议保留的一方
                    var (keep, drop) = a.TrackCount >= b.TrackCount ? (a, b) : (b, a);
                    result.Add(new SimilarPair(keep, drop, reason));
                }
            }
        }
        return result;
    }
}
