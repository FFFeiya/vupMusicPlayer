namespace VupPlayer.Core.Recommend;

/// <summary>推荐上下文。</summary>
/// <param name="CandidateTrackIds">候选范围（通常是当前队列）。</param>
/// <param name="RecentHistory">最近播放过的曲目，最新的在最后。</param>
/// <param name="CurrentTrackId">正在播放的曲目。</param>
public sealed record RecommendContext(
    IReadOnlyList<long> CandidateTrackIds,
    IReadOnlyList<long> RecentHistory,
    long? CurrentTrackId);

/// <summary>
/// 推荐器接口。自己写算法时实现它，然后在 <see cref="RecommenderRegistry"/> 里注册。
/// 需要更多数据时可以在构造函数里注入 LibraryRepository，读取 play_events 等表。
/// </summary>
public interface IRecommender
{
    string Name { get; }
    Task<long?> NextAsync(RecommendContext ctx, CancellationToken ct = default);
}

/// <summary>默认推荐器：均匀随机，排除最近播放过的若干首。</summary>
public sealed class RandomRecommender(Random? rng = null) : IRecommender
{
    readonly Random _rng = rng ?? Random.Shared;

    public string Name => "随机（避开最近播放）";

    public Task<long?> NextAsync(RecommendContext ctx, CancellationToken ct = default)
    {
        var candidates = ctx.CandidateTrackIds.Distinct().ToList();
        if (candidates.Count == 0) return Task.FromResult<long?>(null);
        if (candidates.Count == 1) return Task.FromResult<long?>(candidates[0]);

        // 排除最近 N 首：N = min(20, 候选数 / 2)，且一定排除当前曲目
        var avoidCount = Math.Min(20, candidates.Count / 2);
        var avoid = ctx.RecentHistory.Reverse().Distinct().Take(avoidCount).ToHashSet();
        if (ctx.CurrentTrackId is long cur) avoid.Add(cur);

        var pool = candidates.Where(c => !avoid.Contains(c)).ToList();
        if (pool.Count == 0) pool = candidates.Where(c => c != ctx.CurrentTrackId).ToList();
        return Task.FromResult<long?>(pool[_rng.Next(pool.Count)]);
    }
}

/// <summary>可用推荐器列表。新算法在这里加一行。</summary>
public static class RecommenderRegistry
{
    public static IReadOnlyList<Func<IRecommender>> Factories { get; } =
    [
        () => new RandomRecommender(),
    ];

    public static IRecommender CreateDefault() => Factories[0]();
}
