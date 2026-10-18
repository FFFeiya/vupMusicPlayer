using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Bilibili;

/// <summary>根据简介得出的主播判断。</summary>
public sealed record StreamerDecision(
    string? Name, long? Mid, string? ExtraAlias, IReadOnlyList<string> Tags, string Source, IReadOnlyList<StreamerMention> Mentions);

/// <summary>按简介回填时的一条改动。</summary>
public sealed record StreamerChange(string Bvid, string Title, IReadOnlyList<long> TrackIds, string? From, string To, long? Mid,
    IReadOnlyList<string> AddTags, string Reason);

/// <summary>
/// 用视频简介确定主播：简介里的空间链接（UID）最可靠，其次是简介里写的名字，再次才是标题。
/// 简介里提到多个主播时（合唱、联动），优先选曲库里已有的那位，并打「合唱」标签。
/// </summary>
public sealed class StreamerIdentifier(BiliApi? api, LibraryRepository repo)
{
    public const string ChorusTag = "合唱";
    readonly Dictionary<long, string?> _nameCache = [];

    public async Task<StreamerDecision?> FromDescriptionAsync(string? description, long uploaderMid, string? titleStreamer, CancellationToken ct = default)
    {
        var mentions = DescriptionParser.Parse(description, uploaderMid).ToList();
        if (mentions.Count == 0) return null;

        // 只有链接没名字的：先看曲库里有没有这个 UID，再去 B 站查名字
        for (var i = 0; i < mentions.Count; i++)
        {
            if (mentions[i] is not { Name: null, Mid: long mid }) continue;
            var name = await NameOfAsync(mid, ct).ConfigureAwait(false);
            if (name is not null) mentions[i] = mentions[i] with { Name = name };
        }

        // 「@某人」经常是点歌的观众或别的 UP，只有在没有更强证据时才用，并且只接受唯一一个
        var strong = mentions.Where(m => m.Mid is not null || m.Source != "简介 @").ToList();
        // 只有 @ 的证据：标题已经认出主播时不采用；否则只接受唯一一个
        var people = strong.Count > 0 ? strong : mentions.Count == 1 && titleStreamer is null ? mentions : [];
        if (people.Count == 0) return null;

        var known = repo.GetKnownStreamers();
        StreamerMention primary;
        if (people.Count == 1) primary = people[0];
        else
        {
            primary = people.FirstOrDefault(m => m.Mid is long x && repo.GetStreamerIdByMid(x) is not null)
                   ?? people.FirstOrDefault(m => titleStreamer is not null && m.Name is not null && SameOrTypo(m.Name, titleStreamer))
                   ?? people.Where(m => m.Name is not null && StreamerMatcher.Match(m.Name, known, false) is not null)
                            .OrderByDescending(m => known.First(k => k.Id == StreamerMatcher.Match(m.Name!, known, false)!.StreamerId).TrackCount)
                            .FirstOrDefault()
                   ?? people[0];
        }

        // 标题里的写法和简介的名字只差一个字（切片 man 打错）：记成别名
        string? alias = null;
        if (titleStreamer is not null && primary.Name is not null
            && !string.Equals(titleStreamer, primary.Name, StringComparison.OrdinalIgnoreCase) && SameOrTypo(primary.Name, titleStreamer))
            alias = titleStreamer;

        // 核实 UID：简介里的链接偶尔会打错（少一位数字等），错的 UID 往往不存在或属于别人
        var chosenMid = primary.Mid;
        if (chosenMid is long pm && primary.Name is not null)
        {
            var existingByMid = repo.GetStreamerIdByMid(pm);
            if (existingByMid is null && await VerifyMidAsync(pm, primary.Name, ct).ConfigureAwait(false) == false)
                chosenMid = null;   // 这个 UID 对不上，不采用
            else if (existingByMid is null && repo.GetKnownStreamers().FirstOrDefault(k => SameOrTypo(k.Name, primary.Name!)) is { } byName)
            {
                // 曲库里同名主播记着另一个 UID：如果那个是错的、这个是对的，就换掉
                var old = repo.GetStreamers().First(s => s.Id == byName.Id).Mid;
                if (old is long om && om != pm
                    && await VerifyMidAsync(pm, primary.Name, ct).ConfigureAwait(false) == true
                    && await VerifyMidAsync(om, primary.Name, ct).ConfigureAwait(false) == false)
                    repo.SetStreamerMid(byName.Id, pm);
            }
        }

        IReadOnlyList<string> tags = people.Count >= 2 ? [ChorusTag] : [];
        return new StreamerDecision(primary.Name, chosenMid, alias, tags, primary.Source, people);
    }

    readonly Dictionary<long, string?> _verifiedNames = [];

    /// <summary>true：这个 UID 的用户名和 name 一致（或只差一个字）；false：不存在或是别人；null：查不了（没联网等）。</summary>
    async Task<bool?> VerifyMidAsync(long mid, string name, CancellationToken ct)
    {
        if (api is null) return null;
        if (!_verifiedNames.TryGetValue(mid, out var actual))
        {
            try
            {
                actual = await api.GetUserNameAsync(mid, ct).ConfigureAwait(false) ?? "";
            }
            catch (BiliApiException ex) when (ex.Code is -404 or -626 or -400)
            {
                actual = "";   // 用户不存在
            }
            catch (Exception)
            {
                return null;
            }
            _verifiedNames[mid] = actual;
        }
        if (string.IsNullOrEmpty(actual)) return false;
        return SameOrTypo(actual, name) || StreamerMatcher.Normalize(actual).Contains(StreamerMatcher.Normalize(name));
    }

    static bool SameOrTypo(string a, string b)
    {
        var na = StreamerMatcher.Normalize(a);
        var nb = StreamerMatcher.Normalize(b);
        return na == nb || StreamerMatcher.LooksLikeTypo(na, nb);
    }

    async Task<string?> NameOfAsync(long mid, CancellationToken ct)
    {
        if (_nameCache.TryGetValue(mid, out var cached)) return cached;
        string? name = null;
        if (repo.GetStreamerIdByMid(mid) is long sid)
            name = repo.GetStreamers().FirstOrDefault(s => s.Id == sid)?.Name;
        if (name is null && api is not null)
        {
            try { name = await api.GetUserNameAsync(mid, ct).ConfigureAwait(false); }
            catch { /* 查不到就算了 */ }
        }
        _nameCache[mid] = name;
        return name;
    }

    /// <summary>
    /// 按简介检查整个曲库：主播缺失、和简介不一致、缺「合唱」标签的都列出来。apply = true 时直接改。
    /// </summary>
    public async Task<IReadOnlyList<StreamerChange>> ScanLibraryAsync(bool apply, CancellationToken ct = default)
    {
        var changes = new List<StreamerChange>();
        var streamers = repo.GetStreamers().ToDictionary(s => s.Id);
        foreach (var v in repo.GetVideosForIdentify())
        {
            ct.ThrowIfCancellationRequested();
            var current = v.StreamerId is long cid && streamers.TryGetValue(cid, out var cs) ? cs : null;
            var d = await FromDescriptionAsync(v.Description, v.UpMid, current?.Name, ct).ConfigureAwait(false);
            if (d is null || (d.Name is null && d.Mid is null)) continue;

            // 目标主播是不是就是当前主播
            var sameByMid = d.Mid is long m && current?.Mid == m;
            var sameByName = current is not null && d.Name is not null &&
                (StreamerMatcher.Normalize(current.Name) == StreamerMatcher.Normalize(d.Name)
                 || (current.Aliases is not null && (System.Text.Json.JsonSerializer.Deserialize<string[]>(current.Aliases) ?? [])
                        .Any(a => StreamerMatcher.Normalize(a) == StreamerMatcher.Normalize(d.Name))));
            // 名字一致就以名字为准：简介里的 UID 偶尔会被切片 man 打错（少一位数字之类）
            var targetIsCurrent = sameByMid || sameByName;
            var missingTags = d.Tags.Where(t => !(v.Tags ?? "").Split(',').Contains(t)).ToList();
            var missingMid = targetIsCurrent && d.Mid is not null && current!.Mid is null;
            if (targetIsCurrent && missingTags.Count == 0 && !missingMid) continue;

            var reason = current is null ? $"没有主播，{d.Source}写的是 {d.Name}"
                : !targetIsCurrent ? $"当前是 {current.Name}，{d.Source}写的是 {d.Name}"
                : missingMid ? $"补记 {d.Name} 的 UID"
                : "简介里有多位主播，加「合唱」标签";
            var change = new StreamerChange(v.Bvid, v.Title, v.TrackIds, current?.Name, d.Name ?? current?.Name ?? "?", d.Mid, missingTags, reason);
            changes.Add(change);
            if (apply)
            {
                repo.AssignStreamer(v.TrackIds, targetIsCurrent ? current!.Name : d.Name, d.Mid, d.ExtraAlias, missingTags);
                streamers = repo.GetStreamers().ToDictionary(s => s.Id);
            }
        }
        return changes;
    }
}
