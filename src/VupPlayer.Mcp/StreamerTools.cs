using System.ComponentModel;
using ModelContextProtocol.Server;
using VupPlayer.Core.Control;
using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Mcp;

/// <summary>主播身份整理：找重复、按 B 站 UID 确认、按切片 man 回填、改名、删掉误识别的「主播」。</summary>
[McpServerToolType]
public sealed class StreamerTools(LibraryRepository repo)
{
    static async Task NotifyAsync()
    {
        try { await ControlClient.SendAsync("library_changed", timeoutMs: 1500); } catch { }
    }

    [McpServerTool(Name = "find_similar_streamers", ReadOnly = true)]
    [Description("""
        找出疑似同一个人的主播（打错字、多了后缀、别名重叠、同一个 B 站 UID），以及看起来不是人名的「主播」（活动名、年份、组合）。
        每一对里 keep 是建议保留的一方（曲目多的）。确认后用 merge_streamers 合并，用 delete_streamer 删除非人名。
        拿不准是不是同一个人时，用 search_bilibili_user 查两个名字是否对应同一个 UID。
        """)]
    public string FindSimilarStreamers()
    {
        var known = repo.GetKnownStreamers();
        var mids = repo.GetStreamers().Where(s => s.Mid is not null).ToDictionary(s => s.Id, s => s.Mid!.Value);
        var pairs = StreamerMatcher.FindSimilar(known).Select(p => new
        {
            keep = new { id = p.A.Id, name = p.A.Name, tracks = p.A.TrackCount },
            merge = new { id = p.B.Id, name = p.B.Name, tracks = p.B.TrackCount },
            reason = p.Reason,
        }).ToList<object>();
        // 同一个 UID 的一定是同一个人
        foreach (var g in known.Where(k => mids.ContainsKey(k.Id)).GroupBy(k => mids[k.Id]).Where(g => g.Count() > 1))
        {
            var ordered = g.OrderByDescending(k => k.TrackCount).ToList();
            foreach (var other in ordered.Skip(1))
                pairs.Add(new
                {
                    keep = new { id = ordered[0].Id, name = ordered[0].Name, tracks = ordered[0].TrackCount },
                    merge = new { id = other.Id, name = other.Name, tracks = other.TrackCount },
                    reason = $"同一个 B 站 UID {g.Key}",
                });
        }
        var notPerson = known.Where(k => StreamerMatcher.IsNotPerson(k.Name))
            .Select(k => new { id = k.Id, name = k.Name, tracks = k.TrackCount });
        return LibraryTools.Out(new { similar = pairs, not_a_person = notPerson });
    }

    [McpServerTool(Name = "search_bilibili_user", ReadOnly = true, OpenWorld = true)]
    [Description("""
        在 B 站搜索用户，用来确认主播身份：两个名字搜出同一个 UID 就是同一个人。
        注意 B 站用户搜索不纠正错别字（例如搜「雾氧Up」搜不到「雾氧Uo」），搜不到时说明这个写法很可能是错字，
        应该结合 find_similar_streamers 的结果判断。主播本人一般粉丝多、有直播间、可能有认证。
        确认后用 update_streamer 记下 UID。
        """)]
    public async Task<string> SearchBilibiliUser(string name, int limit = 5)
    {
        var users = await BiliTools.Api.Value.SearchUsersAsync(name.Trim());
        return LibraryTools.Out(users.Take(Math.Clamp(limit, 1, 20)).Select(u => new
        {
            mid = u.Mid, name = u.Name, fans = u.Fans, videos = u.Videos, has_live_room = u.HasLiveRoom, verify = u.Verify,
            sign = u.Sign is { Length: > 60 } s ? s[..60] + "…" : u.Sign,
            same_as_query = string.Equals(StreamerMatcher.Normalize(u.Name), StreamerMatcher.Normalize(name), StringComparison.Ordinal),
        }));
    }

    [McpServerTool(Name = "update_streamer", Idempotent = true)]
    [Description("修改主播信息：改名（新名字已存在会自动合并过去，旧名字记为别名）、记下 B 站 UID、追加别名。")]
    public async Task<string> UpdateStreamer(
        long streamerId,
        [Description("新名字，可空")] string? name = null,
        [Description("B 站 UID，可空；传 0 清除")] long? mid = null,
        [Description("追加的别名，可空")] string[]? addAliases = null)
    {
        var id = streamerId;
        if (!string.IsNullOrWhiteSpace(name)) id = repo.RenameStreamer(id, name);
        if (mid is long m) repo.SetStreamerMid(id, m == 0 ? null : m);
        if (addAliases is { Length: > 0 })
        {
            var s = repo.GetStreamers().First(x => x.Id == id);
            var current = s.Aliases is null ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(s.Aliases) ?? [];
            repo.SetStreamerAliases(id, current.Concat(addAliases));
        }
        await NotifyAsync();
        var after = repo.GetStreamers().First(x => x.Id == id);
        return LibraryTools.Out(new { id = after.Id, name = after.Name, mid = after.Mid, aliases = after.Aliases });
    }

    [McpServerTool(Name = "delete_streamer")]
    [Description("删除一个误识别的「主播」（例如活动名「2025冰火歌会」）。它的曲目不会被删，只是变成没有主播。")]
    public async Task<string> DeleteStreamer(long streamerId)
    {
        var n = repo.DeleteStreamer(streamerId);
        await NotifyAsync();
        return LibraryTools.Out(new { tracks_cleared = n });
    }

    [McpServerTool(Name = "identify_streamers_from_descriptions", OpenWorld = true)]
    [Description("""
        按视频简介检查整个曲库的主播。切片 man 通常会在简介里写主播名字和个人主页链接（带 UID），这是最可靠的身份证据。
        列出：没有主播的、主播和简介不一致的、可以补记 UID 的、简介里有多位主播（加「合唱」标签）的。
        先用 apply=false 预览，看过没问题再 apply=true。导入新视频时已经自动按简介识别，这个工具用于整理旧数据。
        """)]
    public async Task<string> IdentifyStreamersFromDescriptions(bool apply = false, [Description("预览时最多列出多少条")] int limit = 40)
    {
        var identifier = new VupPlayer.Bilibili.StreamerIdentifier(BiliTools.Api.Value, repo);
        var changes = await identifier.ScanLibraryAsync(apply);
        if (apply) await NotifyAsync();
        return LibraryTools.Out(new
        {
            applied = apply,
            videos = changes.Count,
            tracks = changes.Sum(c => c.TrackIds.Count),
            by_reason = changes.GroupBy(c => c.Reason.Split('，')[0]).Select(g => new { reason = g.Key, videos = g.Count() }),
            items = changes.Take(Math.Clamp(limit, 1, 500)).Select(c => new
            {
                c.Bvid, c.Title, from = c.From, to = c.To, mid = c.Mid, add_tags = c.AddTags.Count > 0 ? c.AddTags : null, c.Reason,
            }),
        });
    }

    [McpServerTool(Name = "fill_streamer_by_uploader")]
    [Description("""
        给没有主播的曲目补主播：如果它的切片 man 至少 10 首已标注的切片里 90% 以上是同一个主播，就归给这个主播。
        先用 apply=false 预览，确认后再 apply=true。
        """)]
    public async Task<string> FillStreamerByUploader(bool apply = false)
    {
        var rows = repo.FillStreamerByUploader(apply);
        if (apply) await NotifyAsync();
        return LibraryTools.Out(new
        {
            applied = apply,
            count = rows.Count,
            by_streamer = rows.GroupBy(r => r.Streamer).Select(g => new { streamer = g.Key, tracks = g.Count() }),
            samples = rows.Take(15).Select(r => new { r.TrackId, r.Title, r.Streamer }),
        });
    }
}
