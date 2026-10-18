using System.Text.RegularExpressions;

namespace VupPlayer.Core.Metadata;

/// <summary>简介里提到的一个主播。Mid 来自空间链接，最可靠；Name 可能为空（只有链接）。</summary>
public sealed record StreamerMention(string? Name, long? Mid, string Source);

/// <summary>
/// 从视频简介里找主播。切片 man 通常会写：
///   【雾氧Uo个人主页】https://space.bilibili.com/3493078891497932
///   雾氧Uo：https://space.bilibili.com/3493078891497932
///   【雾氧Uo直播间】https://live.bilibili.com/27206038
///   主播：雾氧Uo   /   @雾氧Uo
/// </summary>
public static partial class DescriptionParser
{
    const string Name = @"[^\s【】\[\]:：@#＃|｜/,，。、()（）""“”'‘’]{2,20}";

    // 【名字个人主页】链接 / 【名字的主页】链接 / 【名字空间】链接
    [GeneratedRegex(@"【(?<name>" + Name + @"?)(?:的)?(?:个人主页|主页|个人空间|空间|B站|b站)】\s*(?:https?://)?space\.bilibili\.com/(?<mid>\d+)")]
    private static partial Regex BracketSpace();

    // 名字 个人主页：链接 / 名字的主页 链接
    [GeneratedRegex(@"(?<name>" + Name + @"?)\s*(?:的)?(?:个人主页|主页|个人空间|空间|B站|b站)\s*[:：]?\s*(?:https?://)?space\.bilibili\.com/(?<mid>\d+)")]
    private static partial Regex NameThenSpace();

    // 名字：链接（同一行）
    [GeneratedRegex(@"(?<name>" + Name + @")\s*[:：]\s*(?:https?://)?space\.bilibili\.com/(?<mid>\d+)")]
    private static partial Regex ColonSpace();

    // 任何空间链接
    [GeneratedRegex(@"space\.bilibili\.com/(?<mid>\d+)")]
    private static partial Regex AnySpace();

    // 【名字直播间】 / 名字直播间：
    [GeneratedRegex(@"【(?<name>" + Name + @"?)(?:的)?直播间】|(?<name>" + Name + @"?)(?:的)?直播间\s*[:：]")]
    private static partial Regex BracketLive();

    // 主播：名字
    [GeneratedRegex(@"主播\s*[:：]\s*(?<name>" + Name + ")")]
    private static partial Regex HostColon();

    // @名字（「切自@某人」「感谢@某人」说的是别的切片 man 或观众，不算）
    [GeneratedRegex(@"(?<!切自|切片自|来自|转自|转载自|搬运自|感谢|鸣谢|原视频|录播)@(?<name>" + Name + ")")]
    private static partial Regex At();

    /// <summary>提取简介里的主播。excludeMid 是切片 man 自己的 UID（他常在简介里放自己的主页）。</summary>
    public static IReadOnlyList<StreamerMention> Parse(string? description, long? excludeMid = null)
    {
        if (string.IsNullOrWhiteSpace(description)) return [];
        var byMid = new Dictionary<long, StreamerMention>();
        var nameOnly = new List<StreamerMention>();

        void AddMid(string? name, long mid, string source)
        {
            if (mid == excludeMid || mid <= 0) return;
            name = Clean(name);
            if (byMid.TryGetValue(mid, out var old))
            {
                if (old.Name is null && name is not null) byMid[mid] = old with { Name = name };
                return;
            }
            byMid[mid] = new StreamerMention(name, mid, source);
        }

        foreach (Match m in BracketSpace().Matches(description)) AddMid(m.Groups["name"].Value, long.Parse(m.Groups["mid"].Value), "简介主页链接");
        foreach (Match m in NameThenSpace().Matches(description)) AddMid(m.Groups["name"].Value, long.Parse(m.Groups["mid"].Value), "简介主页链接");
        foreach (Match m in ColonSpace().Matches(description)) AddMid(m.Groups["name"].Value, long.Parse(m.Groups["mid"].Value), "简介主页链接");
        foreach (Match m in AnySpace().Matches(description)) AddMid(null, long.Parse(m.Groups["mid"].Value), "简介主页链接");

        foreach (var rx in new[] { BracketLive(), HostColon(), At() })
        {
            foreach (Match m in rx.Matches(description))
            {
                var name = Clean(m.Groups["name"].Value);
                if (name is null) continue;
                if (byMid.Values.Any(x => SameName(x.Name, name)) || nameOnly.Any(x => SameName(x.Name, name))) continue;
                nameOnly.Add(new StreamerMention(name, null, rx == BracketLive() ? "简介直播间" : rx == HostColon() ? "简介「主播：」" : "简介 @"));
            }
        }

        // 只有链接没有名字的，如果同一个简介里「直播间」写了唯一一个名字，就配上
        var unnamed = byMid.Values.Where(x => x.Name is null).ToList();
        if (unnamed.Count == 1 && nameOnly.Count == 1 && byMid.Values.All(x => x.Name is null))
        {
            byMid[unnamed[0].Mid!.Value] = unnamed[0] with { Name = nameOnly[0].Name };
            nameOnly.Clear();
        }
        return byMid.Values.Concat(nameOnly).ToList();
    }

    static bool SameName(string? a, string? b) =>
        a is not null && b is not null && StreamerMatcher.Normalize(a) == StreamerMatcher.Normalize(b);

    /// <summary>去掉名字里「的」「直播」之类的尾巴；看起来不是名字的返回 null。</summary>
    static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimEnd('的');
        foreach (var suf in new[] { "个人主页", "主页", "直播间", "直播", "空间" })
            if (n.EndsWith(suf, StringComparison.Ordinal) && n.Length > suf.Length + 1) n = n[..^suf.Length];
        if (n.Length < 2 || n.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        // 链接残片：纯数字、带点（域名）
        if (n.All(char.IsDigit) || n.Contains('.')) return null;
        // 只剩标签词（例如把「个人主页」误当成名字）
        if (n is "个人主页" or "主页" or "个人空间" or "空间" or "直播间" or "个人" or "个人直播间" or "B站" or "b站") return null;
        if (StreamerMatcher.IsNotPerson(n)) return null;
        // 常见的非人名词
        if (n is "原曲" or "原唱" or "作曲" or "作词" or "编曲" or "混音" or "后期" or "切片" or "录播" or "直播间" or "主播" or "UP主" or "up主") return null;
        return n;
    }
}
