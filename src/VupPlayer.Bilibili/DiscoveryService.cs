using System.Text.Json;
using VupPlayer.Core;
using VupPlayer.Core.Data;

namespace VupPlayer.Bilibili;

public sealed record DiscoveryRefreshResult(int Added, int Queries, IReadOnlyList<string> Streamers, string? Error);

/// <summary>
/// B 站发现池：按你常听的主播去 B 站搜「主播 歌回」「主播 翻唱」，
/// 过滤出像单曲的切片放进池子，推荐器会按一定比例从这里挑新歌。
/// </summary>
public sealed class DiscoveryService(BiliApi api, LibraryRepository repo)
{
    const string LastRefreshKey = "discovery.last_refresh";

    /// <summary>每次刷新最多搜几个主播。</summary>
    public int MaxStreamers { get; set; } = 6;

    /// <summary>池子里新候选够多、并且刷新过不久，就不重复搜。</summary>
    public int EnoughCandidates { get; set; } = 30;
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(6);

    static readonly string[] SongHints = ["《", "唱", "歌", "翻唱", "cover", "Cover", "COVER"];
    static readonly string[] Exclude = ["合集", "录播", "全程", "回放", "完整版直播", "杂谈", "游戏", "打游戏", "高能", "名场面", "整活"];

    public bool NeedsRefresh()
    {
        var last = repo.GetMeta(LastRefreshKey) is string s && long.TryParse(s, out var v) ? v : 0;
        var stale = Clock.Now() - last > (long)RefreshInterval.TotalSeconds;
        return stale || repo.GetDiscoveryCandidates(EnoughCandidates).Count < EnoughCandidates / 3;
    }

    public async Task<DiscoveryRefreshResult> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !NeedsRefresh()) return new DiscoveryRefreshResult(0, 0, [], null);
        repo.PruneDiscovery(30);

        var streamers = PickStreamers();
        if (streamers.Count == 0)
            return new DiscoveryRefreshResult(0, 0, [], "曲库里还没有标注主播的曲目，没法按主播去 B 站找");

        var added = 0;
        var queries = 0;
        string? error = null;
        foreach (var (name, aliases) in streamers)
        {
            foreach (var (suffix, order) in new[] { ("歌回", "pubdate"), ("翻唱", "totalrank") })
            {
                ct.ThrowIfCancellationRequested();
                var keyword = $"{name} {suffix}";
                SearchPage page;
                try
                {
                    page = await api.SearchVideosAsync(keyword, 1, order, ct).ConfigureAwait(false);
                    queries++;
                }
                catch (BiliApiException ex) when (ex.IsRiskControl)
                {
                    // 被风控就停，下次再说
                    error = ex.Message;
                    goto done;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    continue;
                }
                var names = aliases.Prepend(name).ToList();
                var items = page.Items
                    .Where(i => LooksLikeSong(i, names))
                    .Where(i => !repo.HasVideo(i.Bvid) && !repo.IsKnownDiscovery(i.Bvid))
                    .Select(i => new DiscoveryItem
                    {
                        Bvid = i.Bvid, Title = i.Title, Uploader = i.Uploader, UploaderMid = i.UploaderMid,
                        DurationSec = i.DurationSec, PlayCount = i.PlayCount, Pubdate = i.Pubdate, CoverUrl = i.Cover,
                        Streamer = name, Query = keyword,
                    });
                added += repo.AddDiscovery(items);
            }
        }
        done:
        repo.SetMeta(LastRefreshKey, Clock.Now().ToString());
        return new DiscoveryRefreshResult(added, queries, streamers.Select(s => s.Name).ToList(), error);
    }

    /// <summary>挑要去搜的主播：按评分和播放次数排序，没有播放记录时按曲目数。</summary>
    List<(string Name, string[] Aliases)> PickStreamers()
    {
        var features = repo.GetRecommendFeatures();
        var aliases = repo.GetStreamers().ToDictionary(s => s.Id,
            s => s.Aliases is null ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(s.Aliases) ?? []);
        return features.Where(f => f.StreamerId is not null && !string.IsNullOrWhiteSpace(f.StreamerName))
            .GroupBy(f => f.StreamerId!.Value)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.First().StreamerName!,
                Score = g.Sum(f => f.RatingSum) + 0.5 * g.Sum(f => f.Plays) + 0.1 * g.Count() + (g.Any(f => f.Favorite) ? 2 : 0),
            })
            .OrderByDescending(x => x.Score)
            .Take(MaxStreamers)
            .Select(x => (x.Name, aliases.TryGetValue(x.Id, out var a) ? a : Array.Empty<string>()))
            .ToList();
    }

    /// <summary>像一首歌的切片：时长 1–10 分钟，标题里有主播名，有「唱/歌/《」之类的字样，没有合集录播之类的字样。</summary>
    internal static bool LooksLikeSong(SearchItem i, IReadOnlyCollection<string> streamerNames) =>
        i.DurationSec is >= 60 and <= 600
        && streamerNames.Any(n => n.Length >= 2 && i.Title.Contains(n, StringComparison.OrdinalIgnoreCase))
        && SongHints.Any(h => i.Title.Contains(h, StringComparison.Ordinal))
        && !Exclude.Any(x => i.Title.Contains(x, StringComparison.Ordinal));
}
