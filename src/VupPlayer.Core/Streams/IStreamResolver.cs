namespace VupPlayer.Core.Streams;

public enum StreamMode
{
    /// <summary>画面 + 声音。</summary>
    Video,
    /// <summary>纯音频：只请求音频流。</summary>
    Audio,
}

public sealed record TrackRef(string Bvid, long Cid, int Page);

/// <summary>
/// 解析出的可播放直链。VideoCandidates / AudioCandidates 是同一个流的多条线路（不同 CDN 镜像），
/// 第一条就是 VideoUrl / AudioUrl；某条线路卡住时换下一条。
/// </summary>
public sealed record ResolvedStream(
    string? VideoUrl,
    string AudioUrl,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt,
    string Resolver,
    string? Quality = null,
    IReadOnlyList<string>? VideoCandidates = null,
    IReadOnlyList<string>? AudioCandidates = null)
{
    /// <summary>可用线路数。</summary>
    public int LineCount => Math.Max(VideoCandidates?.Count ?? 1, AudioCandidates?.Count ?? 1);

    /// <summary>第 line 条线路的视频地址（超出范围时循环）。</summary>
    public string? VideoAt(int line) =>
        VideoUrl is null ? null : VideoCandidates is { Count: > 0 } v ? v[line % v.Count] : VideoUrl;

    public string AudioAt(int line) =>
        AudioCandidates is { Count: > 0 } a ? a[line % a.Count] : AudioUrl;

    /// <summary>线路的服务器名（用于显示和日志）。</summary>
    public string HostAt(int line) =>
        Uri.TryCreate(VideoAt(line) ?? AudioAt(line), UriKind.Absolute, out var u) ? u.Host : "";

    /// <summary>第一条最近没出过问题的线路；都出过问题就从 0 开始。</summary>
    public int FirstHealthyLine(LineHealth health, DateTimeOffset now)
    {
        for (var i = 0; i < LineCount; i++)
            if (!health.IsBad(HostAt(i), now)) return i;
        return 0;
    }
}

/// <summary>
/// 记录最近卡住或连不上的服务器。同一个镜像坏了往往持续一段时间，
/// 之后的歌直接跳过它，不用每首都先在它上面等一次超时。
/// </summary>
public sealed class LineHealth(TimeSpan? ttl = null)
{
    readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);
    readonly Dictionary<string, DateTimeOffset> _bad = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _lock = new();

    public void MarkBad(string host, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (_lock) _bad[host] = now;
    }

    public bool IsBad(string host, DateTimeOffset now)
    {
        lock (_lock) return _bad.TryGetValue(host, out var t) && now - t < _ttl;
    }
}

public interface IStreamResolver
{
    string Name { get; }

    /// <summary>总是同时解析视频流和音频流，调用方按模式取用。</summary>
    Task<ResolvedStream> ResolveAsync(TrackRef track, CancellationToken ct = default);
}

/// <summary>按顺序尝试多个解析器，并缓存未过期的结果。</summary>
public sealed class CachingStreamResolver(IReadOnlyList<IStreamResolver> resolvers) : IStreamResolver
{
    readonly Dictionary<(string, long), ResolvedStream> _cache = [];
    readonly Dictionary<(string, long), Task<ResolvedStream>> _inflight = [];
    readonly Lock _lock = new();
    IReadOnlyList<IStreamResolver> _resolvers = resolvers;

    public string Name => string.Join(" → ", _resolvers.Select(r => r.Name));

    /// <summary>更换解析器及其顺序，同时清空缓存。</summary>
    public void SetResolvers(IReadOnlyList<IStreamResolver> list)
    {
        lock (_lock)
        {
            _resolvers = list;
            _cache.Clear();
        }
    }

    /// <summary>清空缓存（例如改了画质设置）。</summary>
    public void ClearAll()
    {
        lock (_lock) _cache.Clear();
    }

    /// <summary>最后一次失败的原因，便于界面提示。</summary>
    public string? LastError { get; private set; }

    public Task<ResolvedStream> ResolveAsync(TrackRef track, CancellationToken ct = default)
    {
        var key = (track.Bvid, track.Cid);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10))
                return Task.FromResult(hit);
            if (_inflight.TryGetValue(key, out var running)) return running;
            var task = ResolveUncachedAsync(track, key);
            _inflight[key] = task;
            return task;
        }
    }

    /// <summary>直链失效（例如 403）时调用，强制下次重新解析。</summary>
    public void Invalidate(TrackRef track)
    {
        lock (_lock) _cache.Remove((track.Bvid, track.Cid));
    }

    async Task<ResolvedStream> ResolveUncachedAsync(TrackRef track, (string, long) key)
    {
        var errors = new List<string>();
        try
        {
            IReadOnlyList<IStreamResolver> list;
            lock (_lock) list = _resolvers;
            foreach (var r in list)
            {
                try
                {
                    // 解析与具体播放请求解耦，不传播调用方的取消，避免预取被取消后重复解析
                    var result = await r.ResolveAsync(track, CancellationToken.None).ConfigureAwait(false);
                    lock (_lock) _cache[key] = result;
                    LastError = null;
                    return result;
                }
                catch (Exception ex)
                {
                    errors.Add($"{r.Name}: {ex.Message}");
                }
            }
            LastError = string.Join("；", errors);
            throw new StreamResolveException(LastError);
        }
        finally
        {
            lock (_lock) _inflight.Remove(key);
        }
    }
}

public sealed class StreamResolveException(string message) : Exception(message);
