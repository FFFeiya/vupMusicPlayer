using System.ComponentModel;
using ModelContextProtocol.Server;
using VupPlayer.Bilibili;
using VupPlayer.Core.Data;
using VupPlayer.Core.Recommend;

namespace VupPlayer.Mcp;

/// <summary>推荐相关工具：预览打分、比较推荐器效果、管理 B 站发现池。</summary>
[McpServerToolType]
public sealed class RecommendTools(LibraryRepository repo)
{
    [McpServerTool(Name = "recommend_preview", ReadOnly = true)]
    [Description("""
        用当前的加权打分公式给整个曲库打分，返回分数最高的若干首和每个因子的乘数，用来理解或调整推荐。
        实际播放时是按 分数^(1/温度) 加权随机抽取，不一定按这个顺序。
        """)]
    public string RecommendPreview(
        [Description("返回多少首")] int limit = 15,
        [Description("是否同时列出 B 站发现池的候选")] bool includeDiscovery = true)
    {
        var rec = new WeightedRecommender(repo, new RecommendOptions());
        var ctx = new RecommendContext(repo.GetVisibleTrackIds(), [], null);
        object Row(ScoredCandidate s) => new
        {
            track_id = s.TrackId,
            bvid = s.Discovery?.Bvid,
            title = s.Title,
            score = Math.Round(s.Score, 3),
            reason = s.Reason,
            factors = s.Factors.ToDictionary(f => f.Name, f => Math.Round(f.Multiplier, 3)),
        };
        return LibraryTools.Out(new
        {
            formula = "分数 = 单曲喜好 × 喜欢 × 主播亲和 × 切片man亲和 × 冷却 × 同歌冷却 × 新鲜度 × 连续同主播 × 连贯性",
            library = rec.ScoreTracks(ctx).Take(Math.Clamp(limit, 1, 100)).Select(Row),
            discovery = includeDiscovery ? rec.ScoreDiscovery().Take(10).Select(Row) : null,
        });
    }

    [McpServerTool(Name = "get_recommender_stats", ReadOnly = true)]
    [Description("""
        按「谁选的这首」统计播放效果：weighted-v1 / random 是推荐器，manual 是手动点播，queue:* 是按队列顺序。
        比较完播率（越高越好）和跳过率（越低越好）可以判断推荐算法好不好。
        """)]
    public string GetRecommenderStats([Description("最近多少天，空表示全部")] int? days = null)
    {
        long? since = days is int d ? DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() : null;
        return LibraryTools.Out(repo.GetRecommenderStats(since).Select(s => new
        {
            s.Recommender, s.Plays, completion_rate = s.CompletionRate, skip_rate = s.SkipRate, avg_played_sec = s.AvgPlayedSec,
        }));
    }

    [McpServerTool(Name = "refresh_discovery", OpenWorld = true)]
    [Description("按常听的主播去 B 站搜新切片，补充发现池（推荐时会按比例从池里挑新歌）。")]
    public async Task<string> RefreshDiscovery()
    {
        var r = await new DiscoveryService(BiliTools.Api.Value, repo).RefreshAsync(force: true);
        return LibraryTools.Out(new { added = r.Added, queries = r.Queries, streamers = r.Streamers, error = r.Error });
    }

    [McpServerTool(Name = "list_discovery", ReadOnly = true)]
    [Description("查看 B 站发现池里还没推荐过的候选。")]
    public string ListDiscovery(int limit = 30) =>
        LibraryTools.Out(repo.GetDiscoveryCandidates(Math.Clamp(limit, 1, 200)).Select(d => new
        {
            d.Bvid, d.Title, d.Uploader, d.Streamer, duration_sec = d.DurationSec, play_count = d.PlayCount,
        }));

    [McpServerTool(Name = "reject_discovery", Idempotent = true)]
    [Description("把发现池里不想要的候选标记为拒绝，以后不会再推荐。")]
    public string RejectDiscovery(string[] bvids)
    {
        foreach (var b in bvids) repo.SetDiscoveryState(b, "rejected");
        return LibraryTools.Out(new { rejected = bvids.Length });
    }
}
