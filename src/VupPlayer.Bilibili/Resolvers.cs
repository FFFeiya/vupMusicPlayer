using VupPlayer.Core.Streams;

namespace VupPlayer.Bilibili;

/// <summary>画质偏好。</summary>
public sealed class StreamPreferences
{
    /// <summary>视频最高高度。切片一般 1080P 足够，也省带宽。</summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>编码优先顺序：7 = AVC，12 = HEVC，13 = AV1。AVC 兼容性最好。</summary>
    public int[] CodecPreference { get; set; } = [7, 12, 13];

    /// <summary>
    /// 优先使用的镜像（例如 upos-sz-mirrorali.bilivideo.com）。为空表示用 B 站分配的地址，卡住时再自动换。
    /// </summary>
    public string? PreferredMirror { get; set; }

    /// <summary>
    /// 可以互换的国内镜像。B 站 upos 地址的签名和服务器无关，换服务器名就能用；
    /// akamaized 等海外地址的签名格式不同，不能替换。
    /// </summary>
    /// <summary>
    /// 注意：这里只放解析得了的域名。upos-sz-mirror08c.bilivideo.com 已经不存在了
    /// （日志里 46 次 DNS 失败全是它），放进来只会白占一个线路槽。
    /// </summary>
    public static readonly string[] Mirrors =
    [
        "upos-sz-mirrorali.bilivideo.com",
        "upos-sz-mirrorhw.bilivideo.com",
        "upos-sz-mirrorcos.bilivideo.com",
        "upos-sz-mirrorbd.bilivideo.com",
    ];

    /// <summary>
    /// 主地址 + 备用地址 + 换成各镜像的版本，去重后排序。
    /// B 站分配的是海外节点（cosov、akamai 等）时，国内镜像排在前面：
    /// 海外节点单次测速很快，但长时间播放时经常断连接，mpv 重连失败就卡住。
    /// </summary>
    public IReadOnlyList<string> BuildCandidates(string primary, IEnumerable<string> backups)
    {
        var list = new List<string>();
        void Add(string u) { if (!list.Contains(u)) list.Add(u); }

        var originals = new[] { primary }.Concat(backups).ToList();
        var replaceable = originals.FirstOrDefault(IsUpos);
        if (PreferredMirror is not null && replaceable is not null) Add(WithHost(replaceable, PreferredMirror));
        if (PreferredMirror is null && replaceable is not null && IsOverseas(primary))
            foreach (var m in Mirrors.Take(3)) Add(WithHost(replaceable, m));
        foreach (var u in originals) Add(u);
        if (replaceable is not null)
            foreach (var m in Mirrors) Add(WithHost(replaceable, m));
        return list;
    }

    static bool IsUpos(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.StartsWith("upos-", StringComparison.OrdinalIgnoreCase)
        && u.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>海外节点：akamai 等非 bilivideo 域名，或 upos-xx-mirrorcosov / aliov / hwov 这类以 ov 结尾的。</summary>
    internal static bool IsOverseas(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!u.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase)) return true;
        var name = u.Host[..u.Host.IndexOf('.')];
        return name.StartsWith("upos-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("ov", StringComparison.OrdinalIgnoreCase);
    }

    static string WithHost(string url, string host) => new UriBuilder(url) { Host = host, Port = -1 }.Uri.ToString();

    internal static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["Referer"] = BiliApi.Referer,
        ["User-Agent"] = BiliApi.UserAgent,
    };
}

/// <summary>直接调用 B 站 playurl 接口（快，通常 0.5 秒内）。</summary>
public sealed class BiliApiResolver(BiliApi api, StreamPreferences prefs) : IStreamResolver
{
    public string Name => "B站接口";

    public async Task<ResolvedStream> ResolveAsync(TrackRef track, CancellationToken ct = default)
    {
        var info = await api.GetPlayUrlAsync(track.Bvid, track.Cid, ct).ConfigureAwait(false);
        var expires = DateTimeOffset.UtcNow.AddMinutes(100);
        if (info.Videos.Count == 0 && info.MuxedUrl is not null)
            return new ResolvedStream(info.MuxedUrl, info.MuxedUrl, StreamPreferences.Headers, expires, Name, info.QualityDesc);

        var audio = info.Audios.OrderByDescending(a => a.Bandwidth).FirstOrDefault()
                    ?? throw new StreamResolveException("没有音频流");
        var video = PickVideo(info.Videos, prefs);
        var q = video is null ? null : $"{video.Height}p {CodecName(video.CodecId)}";
        var audioLines = prefs.BuildCandidates(audio.Url, audio.BackupUrls);
        var videoLines = video is null ? null : prefs.BuildCandidates(video.Url, video.BackupUrls);
        return new ResolvedStream(videoLines?[0], audioLines[0], StreamPreferences.Headers, expires, Name, q, videoLines, audioLines);
    }

    internal static DashStream? PickVideo(IReadOnlyList<DashStream> videos, StreamPreferences prefs)
    {
        if (videos.Count == 0) return null;
        var fit = videos.Where(v => v.Height <= prefs.MaxHeight || v.Height == 0).ToList();
        if (fit.Count == 0) fit = [videos.MinBy(v => v.Height)!];
        var bestHeight = fit.Max(v => v.Height);
        var sameHeight = fit.Where(v => v.Height == bestHeight).ToList();
        foreach (var codec in prefs.CodecPreference)
            if (sameHeight.FirstOrDefault(v => v.CodecId == codec) is { } hit) return hit;
        return sameHeight[0];
    }

    static string CodecName(int id) => id switch { 7 => "AVC", 12 => "HEVC", 13 => "AV1", _ => "" };
}

/// <summary>用 yt-dlp 解析（慢，约 3–5 秒，但 B 站改版时更新 yt-dlp 就能恢复）。</summary>
public sealed class YtDlpResolver(YtDlp ytdlp, StreamPreferences prefs) : IStreamResolver
{
    public string Name => "yt-dlp";

    public async Task<ResolvedStream> ResolveAsync(TrackRef track, CancellationToken ct = default)
    {
        var url = $"https://www.bilibili.com/video/{track.Bvid}?p={track.Page}";
        var node = await ytdlp.DumpJsonAsync(url, flatPlaylist: false, ct, timeoutSec: 60).ConfigureAwait(false);
        var formats = node["formats"]?.AsArray().Where(f => f is not null).Select(f => f!).ToList() ?? [];
        if (formats.Count == 0) throw new StreamResolveException("yt-dlp 没有返回可用格式");

        static string S(System.Text.Json.Nodes.JsonNode f, string k) => f[k]?.ToString() ?? "";
        static double D(System.Text.Json.Nodes.JsonNode f, string k) => double.TryParse(f[k]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        var audio = formats.Where(f => S(f, "vcodec") == "none" && S(f, "acodec") != "none")
                           .OrderByDescending(f => D(f, "abr")).ThenByDescending(f => D(f, "tbr")).FirstOrDefault();
        var videos = formats.Where(f => S(f, "vcodec") is not ("none" or "") && S(f, "acodec") == "none").ToList();

        int CodecId(System.Text.Json.Nodes.JsonNode f) => S(f, "vcodec") switch
        {
            var c when c.StartsWith("avc") => 7,
            var c when c.StartsWith("hev") || c.StartsWith("hvc") => 12,
            var c when c.StartsWith("av01") => 13,
            _ => 0,
        };
        var dash = videos.Select(f => new DashStream(0, S(f, "url"), [], (long)D(f, "tbr"), S(f, "vcodec"),
            (int)D(f, "width"), (int)D(f, "height"), CodecId(f))).ToList();
        var video = BiliApiResolver.PickVideo(dash, prefs);

        if (audio is null)
        {
            // 老视频可能只有音视频合一的流
            var muxed = formats.LastOrDefault(f => S(f, "acodec") is not ("none" or ""))
                        ?? throw new StreamResolveException("yt-dlp 没有找到音频流");
            var mu = S(muxed, "url");
            return new ResolvedStream(mu, mu, StreamPreferences.Headers, DateTimeOffset.UtcNow.AddMinutes(100), Name);
        }
        return new ResolvedStream(video?.Url, S(audio, "url"), StreamPreferences.Headers,
            DateTimeOffset.UtcNow.AddMinutes(100), Name, video is null ? null : $"{video.Height}p");
    }
}
