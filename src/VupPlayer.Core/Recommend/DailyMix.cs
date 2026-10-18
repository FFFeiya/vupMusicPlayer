using System.Text.Json;
using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Core.Recommend;

/// <summary>推荐歌单里的一首：曲目 + 推荐理由。</summary>
public sealed record MixItem(long TrackId, string Reason);

/// <summary>
/// 推荐专区的几个歌单（类似网易云的「每日推荐」）：
/// - 每日推荐：每天按打分公式挑 30 首，当天固定不变，可以「换一批」；
/// - 好久不见：以前常听完、但很久没听的；
/// - 新歌速递：最近导入还没听过的。
/// 每日推荐存在 meta 表里（daily.yyyy-MM-dd），播放器和 MCP 看到的是同一份。
/// </summary>
public sealed class DailyMix(LibraryRepository repo)
{
    public const int DefaultCount = 30;
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Func<long> Now { get; set; } = Clock.Now;

    static string Key(DateOnly day) => $"daily.{day:yyyy-MM-dd}";
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>取某天的每日推荐；还没有就生成。会去掉已经被删除或隐藏的曲目。</summary>
    public IReadOnlyList<MixItem> GetDaily(DateOnly? day = null)
    {
        var d = day ?? Today;
        if (repo.GetMeta(Key(d)) is string json)
        {
            var items = JsonSerializer.Deserialize<List<MixItem>>(json) ?? [];
            var visible = repo.GetVisibleTrackIds().ToHashSet();
            var alive = items.Where(i => visible.Contains(i.TrackId)).ToList();
            if (alive.Count > 0) return alive;
        }
        return Refresh(d);
    }

    /// <summary>重新生成（「换一批」）。和当前这批尽量不重复。</summary>
    public IReadOnlyList<MixItem> Refresh(DateOnly? day = null, int count = DefaultCount)
    {
        var d = day ?? Today;
        var previous = repo.GetMeta(Key(d)) is string old
            ? (JsonSerializer.Deserialize<List<MixItem>>(old) ?? []).Select(i => i.TrackId).ToHashSet()
            : [];
        var items = Generate(count, previous, new Random(HashCode.Combine(d.DayNumber, previous.Count, Environment.TickCount)));
        repo.SetMeta(Key(d), JsonSerializer.Serialize(items, Json));
        // 只保留最近 7 天的记录
        repo.SetMeta(Key(d.AddDays(-8)), null);
        return items;
    }

    /// <summary>
    /// 按打分公式加权抽样（不放回），同时保证多样性：同一首歌只出现一次，
    /// 同一个主播每多一首，后面再抽到他的权重就打七折（曲库只有一个主播时不影响）。
    /// </summary>
    public IReadOnlyList<MixItem> Generate(int count, IReadOnlySet<long>? avoid = null, Random? rng = null)
    {
        rng ??= Random.Shared;
        var rec = new WeightedRecommender(repo, new RecommendOptions { HardExcludeRecent = 0 }) { Now = Now };
        var scored = rec.ScoreTracks(new RecommendContext(repo.GetVisibleTrackIds(), [], null)).ToList();
        if (scored.Count == 0) return [];
        var features = repo.GetRecommendFeatures().ToDictionary(f => f.Id);

        // 换一批时，上一批的曲目降权（不完全排除，曲库小的时候也能凑满）
        var pool = scored.Select(s => (s, w: s.Score * (avoid?.Contains(s.TrackId!.Value) == true ? 0.15 : 1))).ToList();
        var picked = new List<MixItem>();
        var songs = new HashSet<string>();
        var perStreamer = new Dictionary<long, int>();
        var singleStreamer = features.Values.Select(f => f.StreamerId).Distinct().Count() <= 1;

        while (picked.Count < count && pool.Count > 0)
        {
            var weights = pool.Select(p =>
            {
                var f = features[p.s.TrackId!.Value];
                var w = p.w;
                if (!singleStreamer && f.StreamerId is long sid && perStreamer.TryGetValue(sid, out var n)) w *= Math.Pow(0.7, n);
                return Math.Max(w, 1e-9);
            }).ToArray();
            var r = rng.NextDouble() * weights.Sum();
            var idx = 0;
            for (; idx < weights.Length - 1; idx++)
            {
                r -= weights[idx];
                if (r <= 0) break;
            }
            var choice = pool[idx].s;
            pool.RemoveAt(idx);
            var feat = features[choice.TrackId!.Value];
            if (!string.IsNullOrWhiteSpace(feat.SongTitle) && !songs.Add(WeightedRecommender.NormalizeSong(feat.SongTitle))) continue;
            if (feat.StreamerId is long s2) perStreamer[s2] = perStreamer.GetValueOrDefault(s2) + 1;
            picked.Add(new MixItem(choice.TrackId!.Value, choice.Reason));
        }
        return picked;
    }

    /// <summary>好久不见：听过至少 2 次、整体听完居多（或标了喜欢），并且超过 days 天没听。</summary>
    public IReadOnlyList<MixItem> Rediscover(int days = 14, int limit = 50)
    {
        var now = Now();
        return repo.GetRecommendFeatures()
            .Where(f => f.LastPlayedAt is long lp && now - lp > days * 86400L)
            .Where(f => (f.Plays >= 2 && f.RatingSum / f.Plays > 0.2) || (f.Favorite && f.Plays >= 1))
            .OrderByDescending(f => (f.Favorite ? 1 : 0) + f.RatingSum / Math.Max(1, f.Plays))
            .ThenBy(f => f.LastPlayedAt)
            .Take(limit)
            .Select(f => new MixItem(f.Id, $"{(int)((now - f.LastPlayedAt!.Value) / 86400)} 天没听了" + (f.Favorite ? " · 你标了喜欢" : $" · 听过 {f.Plays} 次")))
            .ToList();
    }

    /// <summary>新歌速递：最近 days 天导入、还没听过的。</summary>
    public IReadOnlyList<MixItem> Fresh(int days = 14, int limit = 50)
    {
        var now = Now();
        return repo.GetRecommendFeatures()
            .Where(f => f.Plays == 0 && now - f.AddedAt < days * 86400L)
            .OrderByDescending(f => f.AddedAt)
            .Take(limit)
            .Select(f =>
            {
                var d = Math.Max(0, (int)((now - f.AddedAt) / 86400));
                return new MixItem(f.Id, (d == 0 ? "今天导入" : $"{d} 天前导入") + "，还没听过");
            })
            .ToList();
    }
}
