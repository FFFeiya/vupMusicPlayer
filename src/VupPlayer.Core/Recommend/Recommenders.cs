using VupPlayer.Core.Data;

namespace VupPlayer.Core.Recommend;

/// <summary>推荐上下文。</summary>
/// <param name="CandidateTrackIds">候选范围（整个曲库或当前列表）。</param>
/// <param name="RecentHistory">最近播放过的曲目，最新的在最后。</param>
/// <param name="CurrentTrackId">正在播放的曲目。</param>
/// <param name="AllowDiscovery">是否允许推荐 B 站发现池里还没导入的视频。</param>
public sealed record RecommendContext(
    IReadOnlyList<long> CandidateTrackIds,
    IReadOnlyList<long> RecentHistory,
    long? CurrentTrackId,
    bool AllowDiscovery = false);

/// <summary>
/// 推荐结果：要么是曲库里的一首（TrackId），要么是 B 站发现池里的一个视频（Discovery，需要先导入）。
/// </summary>
public sealed record Recommendation(long? TrackId, DiscoveryItem? Discovery, string Reason, double Score, string Recommender);

/// <summary>
/// 推荐器接口。自己写算法时实现它，然后在 <see cref="RecommenderRegistry"/> 里注册。
/// 需要更多数据时可以在构造函数里注入 LibraryRepository，读取 play_events 等表。
/// </summary>
public interface IRecommender
{
    string Name { get; }
    Task<Recommendation?> NextAsync(RecommendContext ctx, CancellationToken ct = default);
}

/// <summary>最简单的推荐器：均匀随机，排除最近播放过的若干首。保留作对照组。</summary>
public sealed class RandomRecommender(Random? rng = null) : IRecommender
{
    readonly Random _rng = rng ?? Random.Shared;

    public string Name => "random";

    public Task<Recommendation?> NextAsync(RecommendContext ctx, CancellationToken ct = default)
    {
        var candidates = ctx.CandidateTrackIds.Distinct().ToList();
        if (candidates.Count == 0) return Task.FromResult<Recommendation?>(null);
        if (candidates.Count == 1) return Task.FromResult<Recommendation?>(Make(candidates[0]));

        // 排除最近 N 首：N = min(20, 候选数 / 2)，且一定排除当前曲目
        var avoidCount = Math.Min(20, candidates.Count / 2);
        var avoid = ctx.RecentHistory.Reverse().Distinct().Take(avoidCount).ToHashSet();
        if (ctx.CurrentTrackId is long cur) avoid.Add(cur);

        var pool = candidates.Where(c => !avoid.Contains(c)).ToList();
        if (pool.Count == 0) pool = candidates.Where(c => c != ctx.CurrentTrackId).ToList();
        return Task.FromResult<Recommendation?>(Make(pool[_rng.Next(pool.Count)]));

        Recommendation Make(long id) => new(id, null, "随机挑的", 1, Name);
    }
}

/// <summary>推荐器的可调参数。</summary>
public sealed class RecommendOptions
{
    /// <summary>
    /// 温度：越小越保守（集中在高分曲目），越大越随机。抽样权重 = 分数 ^ (1 / 温度)。
    /// 保守 0.6 / 均衡 1.0 / 冒险 1.6。
    /// </summary>
    public double Temperature { get; set; } = 1.0;

    /// <summary>从 B 站发现池里挑的概率（0 表示关闭）。</summary>
    public double DiscoveryRate { get; set; } = 0.1;

    /// <summary>最近播放过的多少首一定不推荐。</summary>
    public int HardExcludeRecent { get; set; } = 10;
}

/// <summary>一个因子：乘数 + 给人看的说明。</summary>
public sealed record ScoreFactor(string Name, double Multiplier, string? Note);

/// <summary>打分明细，用于调试和「为什么推荐这首」。</summary>
public sealed record ScoredCandidate(long? TrackId, DiscoveryItem? Discovery, string Title, double Score, IReadOnlyList<ScoreFactor> Factors)
{
    /// <summary>挑最有说服力的两三个正面因子组成一句话。</summary>
    public string Reason
    {
        get
        {
            var notes = Factors.Where(f => f.Note is not null && f.Multiplier >= 1.1)
                               .OrderByDescending(f => f.Multiplier).Take(3).Select(f => f.Note!).ToList();
            if (Discovery is not null) notes.Insert(0, "B 站发现");
            return notes.Count > 0 ? string.Join(" · ", notes) : "随机挑的";
        }
    }
}

/// <summary>
/// 加权打分推荐器（第一版公式）。
///
/// 每首候选的分数是一串乘数相乘：
///   分数 = 单曲喜好 × 喜欢 × 主播亲和 × 切片 man 亲和 × 冷却 × 同歌冷却 × 新鲜度 × 连续同主播 × 连贯性
/// 然后按 分数^(1/温度) 做加权随机抽样，而不是总选最高分，保证多样性。
///
/// 「评分」来自播放记录：听完 +1，30 秒内手动跳过 −1，其余按听的比例映射到 −1..1。
/// 次数少的数据用贝叶斯平滑往先验值拉，避免偶然跳过一次就被打入冷宫。
/// </summary>
public sealed class WeightedRecommender(LibraryRepository repo, RecommendOptions options, Random? rng = null) : IRecommender
{
    readonly Random _rng = rng ?? Random.Shared;

    public string Name => "weighted-v1";
    public RecommendOptions Options => options;

    /// <summary>当前时间（测试时可以替换）。</summary>
    public Func<long> Now { get; set; } = Clock.Now;

    // ---- 公式参数：想调手感就改这里 ----
    const double TrackPrefWeight = 1.1;     // 单曲喜好：exp(1.1 × pref)，pref ∈ [−1, 1] → 0.33 .. 3.0
    const double TrackPriorStrength = 3;    // 单曲平滑强度：相当于 3 次「主播平均水平」的虚拟播放
    const double FavoriteBoost = 1.8;
    const double StreamerWeight = 0.5;      // 主播亲和：exp(0.5 × a) → 0.6 .. 1.65
    const double StreamerPriorStrength = 5;
    const double UploaderWeight = 0.25;     // 切片 man 亲和：exp(0.25 × a) → 0.78 .. 1.28
    const double UploaderPriorStrength = 8;
    const double NewTrackBoost = 0.9;       // 新导入未听：1.2 + 0.9 × exp(−天数 / 14)
    const double NewHalfLifeDays = 14;
    const double SameSongPenalty = 0.8;     // 刚听过同一首歌的另一个切片：最多压到 0.2
    static readonly double[] StreakMultipliers = [1.0, 0.85, 0.6, 0.4];  // 最近 3 首里同主播 0/1/2/3 首
    const double ContinuityBoost = 1.15;    // 上一首听完了且有相同标签：顺着这个风格

    public Task<Recommendation?> NextAsync(RecommendContext ctx, CancellationToken ct = default)
    {
        // 按概率先试 B 站发现池
        if (ctx.AllowDiscovery && options.DiscoveryRate > 0 && _rng.NextDouble() < options.DiscoveryRate)
        {
            var found = Sample(ScoreDiscovery());
            if (found is not null)
                return Task.FromResult<Recommendation?>(new Recommendation(null, found.Discovery, found.Reason, found.Score, Name));
        }
        var pick = Sample(ScoreTracks(ctx));
        return Task.FromResult(pick is null ? null : new Recommendation(pick.TrackId, null, pick.Reason, pick.Score, Name));
    }

    ScoredCandidate? Sample(IReadOnlyList<ScoredCandidate> scored)
    {
        if (scored.Count == 0) return null;
        var t = Math.Clamp(options.Temperature, 0.2, 5);
        var weights = scored.Select(s => Math.Pow(Math.Max(s.Score, 1e-6), 1 / t)).ToArray();
        var total = weights.Sum();
        var r = _rng.NextDouble() * total;
        for (var i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return scored[i];
        }
        return scored[^1];
    }

    /// <summary>给候选曲目逐个打分（按分数从高到低）。公开出来给预览和调试用。</summary>
    public IReadOnlyList<ScoredCandidate> ScoreTracks(RecommendContext ctx)
    {
        var now = Now();
        var all = repo.GetRecommendFeatures();
        var byId = all.ToDictionary(f => f.Id);
        var candidates = ctx.CandidateTrackIds.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (candidates.Count == 0) return [];

        var recent = repo.GetRecentPlays(30);

        // 一定排除：当前曲目 + 12 小时内最近播放的若干首（候选太少时放宽）。更早的交给冷却因子处理
        var recentIds = recent.Where(p => now - p.StartedAt < 12 * 3600).Select(p => p.TrackId)
            .Concat(ctx.RecentHistory.Reverse()).Distinct();
        var exclude = new HashSet<long>(recentIds.Take(Math.Min(options.HardExcludeRecent, candidates.Count / 2)));
        if (ctx.CurrentTrackId is long cur) exclude.Add(cur);
        var pool = candidates.Where(c => !exclude.Contains(c.Id)).ToList();
        if (pool.Count == 0) pool = candidates.Where(c => c.Id != ctx.CurrentTrackId).ToList();
        if (pool.Count == 0) pool = candidates;

        // 冷却时间常数：曲库越大，冷却越久。300 首约 20 小时，最少 1 小时，最多 72 小时
        var tauHours = Math.Clamp(all.Count / 15.0, 1, 72);

        // 主播 / 切片 man 的平均评分（贝叶斯平滑，先验 0）
        var streamerAffinity = all.Where(f => f.StreamerId is not null).GroupBy(f => f.StreamerId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.RatingSum) / (g.Sum(f => f.Plays) + StreamerPriorStrength));
        var uploaderAffinity = all.GroupBy(f => f.UpMid)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.RatingSum) / (g.Sum(f => f.Plays) + UploaderPriorStrength));

        // 同一首歌（不同切片）最近一次播放时间
        var songLastPlayed = all.Where(f => f.LastPlayedAt is not null && !string.IsNullOrWhiteSpace(f.SongTitle))
            .GroupBy(f => NormalizeSong(f.SongTitle!))
            .ToDictionary(g => g.Key, g => g.Max(f => f.LastPlayedAt!.Value));

        var last3 = recent.Take(3).ToList();
        var lastPlay = recent.FirstOrDefault();
        var lastTags = SplitTags(lastPlay?.Tags);

        var result = new List<ScoredCandidate>(pool.Count);
        foreach (var f in pool)
        {
            var factors = new List<ScoreFactor>();
            var aS = f.StreamerId is long sid && streamerAffinity.TryGetValue(sid, out var a1) ? a1 : 0;
            var aU = uploaderAffinity.TryGetValue(f.UpMid, out var a2) ? a2 : 0;

            // 1. 单曲喜好：往主播平均水平平滑
            var pref = (f.RatingSum + aS * TrackPriorStrength) / (f.Plays + TrackPriorStrength);
            factors.Add(new("单曲喜好", Math.Exp(TrackPrefWeight * pref),
                f.Plays >= 2 && pref > 0.3 ? $"常听完（听过 {f.Plays} 次）" : null));

            // 2. 标了喜欢
            if (f.Favorite) factors.Add(new("喜欢", FavoriteBoost, "你标了喜欢"));

            // 3. 主播亲和
            if (f.StreamerId is not null)
                factors.Add(new("主播亲和", Math.Exp(StreamerWeight * aS), aS > 0.2 ? $"常听的主播 {f.StreamerName}" : null));

            // 4. 切片 man 亲和
            factors.Add(new("切片man亲和", Math.Exp(UploaderWeight * aU), aU > 0.3 ? $"常听 {f.UpName} 的切片" : null));

            // 5. 冷却：上次播放越近分数越低，按时间常数指数恢复
            if (f.LastPlayedAt is long lp)
            {
                var h = Math.Max(0, (now - lp) / 3600.0);
                var cool = Math.Max(0.02, 1 - Math.Exp(-h / tauHours));
                factors.Add(new("冷却", cool, h > tauHours * 3 ? "很久没听了" : null));
            }

            // 6. 同歌冷却：刚听过同一首歌的另一个切片
            if (!string.IsNullOrWhiteSpace(f.SongTitle) && songLastPlayed.TryGetValue(NormalizeSong(f.SongTitle), out var songAt)
                && songAt != f.LastPlayedAt)
            {
                var h = Math.Max(0, (now - songAt) / 3600.0);
                factors.Add(new("同歌冷却", 1 - SameSongPenalty * Math.Exp(-h / tauHours), null));
            }

            // 7. 新鲜度：没听过的给加成，新导入的加成更大
            if (f.Plays == 0)
            {
                var days = Math.Max(0, (now - f.AddedAt) / 86400.0);
                var boost = 1.2 + NewTrackBoost * Math.Exp(-days / NewHalfLifeDays);
                factors.Add(new("新鲜度", boost, days < 7 ? "新导入，还没听过" : "还没听过"));
            }

            // 8. 连续同主播：最近 3 首里同主播越多越压
            if (f.StreamerId is not null)
            {
                var k = last3.Count(p => p.StreamerId == f.StreamerId);
                if (k > 0) factors.Add(new("连续同主播", StreakMultipliers[Math.Min(k, 3)], null));
            }

            // 9. 连贯性：上一首听完了，并且和它有相同标签
            if (lastPlay is { Completed: true } && lastTags.Count > 0 && SplitTags(f.Tags).Overlaps(lastTags))
                factors.Add(new("连贯性", ContinuityBoost, "顺着刚才的风格"));

            var score = factors.Aggregate(1.0, (acc, x) => acc * x.Multiplier);
            result.Add(new ScoredCandidate(f.Id, null, f.Title, score, factors));
        }
        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>给 B 站发现池里的候选打分。</summary>
    public IReadOnlyList<ScoredCandidate> ScoreDiscovery()
    {
        var items = repo.GetDiscoveryCandidates();
        if (items.Count == 0) return [];
        var now = Now();
        var all = repo.GetRecommendFeatures();
        var streamerAffinity = all.Where(f => f.StreamerName is not null).GroupBy(f => f.StreamerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.RatingSum) / (g.Sum(f => f.Plays) + StreamerPriorStrength), StringComparer.OrdinalIgnoreCase);
        var knownUploaders = all.Select(f => f.UpMid).ToHashSet();

        var result = new List<ScoredCandidate>();
        foreach (var d in items)
        {
            var factors = new List<ScoreFactor>();
            var aS = d.Streamer is not null && streamerAffinity.TryGetValue(d.Streamer, out var a) ? a : 0;
            factors.Add(new("主播亲和", Math.Exp(StreamerWeight * aS * 2), d.Streamer is not null ? $"{d.Streamer} 的切片" : null));
            if (knownUploaders.Contains(d.UploaderMid))
                factors.Add(new("熟悉的切片man", 1.4, $"{d.Uploader} 切的"));
            factors.Add(new("热度", 1 + Math.Log10(1 + Math.Max(0, d.PlayCount)) / 6, d.PlayCount >= 50_000 ? "播放量高" : null));
            if (d.Pubdate > 0 && now - d.Pubdate < 30 * 86400L)
                factors.Add(new("新发布", 1.3, "最近发布"));
            var score = factors.Aggregate(1.0, (acc, x) => acc * x.Multiplier);
            result.Add(new ScoredCandidate(null, d, d.Title, score, factors));
        }
        return result.OrderByDescending(r => r.Score).ToList();
    }

    internal static string NormalizeSong(string s) =>
        new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch is not ('《' or '》' or '「' or '」')).ToArray()).ToLowerInvariant();

    static HashSet<string> SplitTags(string? tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>可用推荐器。新算法在这里加一行，然后在设置里就能切换。</summary>
public static class RecommenderRegistry
{
    public static IReadOnlyList<(string Name, string DisplayName)> Available { get; } =
    [
        ("weighted-v1", "加权打分（第一版）"),
        ("random", "纯随机（对照组）"),
    ];

    public static IRecommender Create(string name, LibraryRepository repo, RecommendOptions options) => name switch
    {
        "random" => new RandomRecommender(),
        _ => new WeightedRecommender(repo, options),
    };
}
