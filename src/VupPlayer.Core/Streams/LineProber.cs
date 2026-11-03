using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VupPlayer.Core.Streams;

/// <summary>一次探测的结果。</summary>
/// <param name="Line">选中的线路序号。都探不通时是传入的起始线路——交给 mpv 自己再试一次。</param>
/// <param name="Probed">实际探了几条。</param>
/// <param name="Error">被跳过的线路里第一条的失败原因（全通过时为 null），用来在日志里说明为什么换线路。</param>
public sealed record ProbeResult(int Line, int Probed, string? Error);

/// <summary>
/// 把一条线路交给 mpv 之前先探一下能不能连上。解决两个实测出来的问题：
/// 1. 连不上的线路原来要等看门狗那 10～15 秒才换，这里 3 秒就跳过；
/// 2. 首次 DNS 解析实测要 1.5～3 秒，经常把 mpv 顶过看门狗阈值，被判「加载太慢」
///    换到另一条同样冷的线路，冷启动代价从头再付一遍。探测会焐热系统 DNS 缓存，
///    mpv 接手时同一个主机的解析就是即时的。
/// </summary>
public sealed class LineProber : IDisposable
{
    readonly HttpClient _http;

    /// <summary>单条线路的探测超时。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>一次最多探几条。都不通就把起始线路交给 mpv，让它自己试。</summary>
    public int MaxProbes { get; set; } = 3;

    public LineProber(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // 单条的超时由 CancellationToken 控制，这里放宽
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// 从 from 开始，找第一条探得通的线路。
    /// 探不通的线路会记进 health，后面的歌直接跳过它。
    /// </summary>
    public async Task<ProbeResult> PickAsync(
        ResolvedStream stream, int from, bool audioOnly, LineHealth health, CancellationToken ct = default)
    {
        var count = stream.LineCount;
        if (count <= 1) return new ProbeResult(from, 0, null);

        // 这个主机刚才还好好的（上一首就在用它）：直接用，省掉一次探测的往返
        if (health.IsRecentlyGood(stream.HostAt(from), DateTimeOffset.UtcNow))
            return new ProbeResult(from, 0, null);

        string? firstError = null;
        var probed = 0;
        for (var i = 0; i < Math.Min(MaxProbes, count); i++)
        {
            var line = (from + i) % count;
            var url = audioOnly ? stream.AudioAt(line) : stream.VideoAt(line) ?? stream.AudioAt(line);
            if (string.IsNullOrEmpty(url)) continue;
            probed++;
            var (ok, error) = await ProbeAsync(url, stream.Headers, ct).ConfigureAwait(false);
            if (ok)
            {
                health.MarkGood(stream.HostAt(line), DateTimeOffset.UtcNow);
                // 带上前面那些线路的失败原因，否则日志里只知道换了线路、不知道为什么
                return new ProbeResult(line, probed, firstError);
            }
            firstError ??= error;
            health.MarkBad(stream.HostAt(line), DateTimeOffset.UtcNow);
            if (ct.IsCancellationRequested) break;
        }
        return new ProbeResult(from, probed, firstError);
    }

    /// <summary>
    /// 探一个地址。只要服务器给了回应就算通——重点是确认主机可达、顺便焐热 DNS，
    /// 不是校验内容。只有 403 / 404 / 410 这种「地址本身没用了」才算失败。
    /// </summary>
    async Task<(bool Ok, string? Error)> ProbeAsync(
        string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            // 只要开头两个字节：直播流也不会真的开始拉
            req.Headers.Range = new RangeHeaderValue(0, 1);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? (false, $"HTTP {(int)resp.StatusCode}")
                : (true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"{Timeout.TotalSeconds:0.#} 秒内连不上");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
