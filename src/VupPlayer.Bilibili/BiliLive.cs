using System.Text.Json.Nodes;
using VupPlayer.Core;

namespace VupPlayer.Bilibili;

/// <summary>直播的一条可播放线路。同一个房间会有多种协议、每种协议多个 CDN。</summary>
public sealed record LiveLine(string Url, string Protocol, string Format, string Codec, int Qn)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";
    public string Describe => $"{Protocol}/{Format}/{Codec}";
}

/// <summary>直播取流结果。Lines 为空表示没在播或拿不到流。</summary>
public sealed record LivePlayInfo(
    long RoomId,
    LiveStatus Status,
    int CurrentQn,
    IReadOnlyList<LiveLine> Lines,
    IReadOnlyList<(int Qn, string Desc)> Qualities);

/// <summary>B 站直播接口。</summary>
public sealed partial class BiliApi
{
    /// <summary>直播接口和直播流都要求这个 Referer。</summary>
    public const string LiveReferer = "https://live.bilibili.com/";

    /// <summary>直播流的请求头（给 mpv 用）。</summary>
    public static readonly IReadOnlyDictionary<string, string> LiveHeaders = new Dictionary<string, string>
    {
        ["Referer"] = LiveReferer,
        ["User-Agent"] = UserAgent,
    };

    /// <summary>画质：原画 10000 / 蓝光 400 / 超清 250 / 高清 150 / 流畅 80。</summary>
    public const int QnOriginal = 10000;

    /// <summary>
    /// 批量查多个主播的直播状态。一次请求查完所有人，对风控非常友好（不要每人一个请求）。
    /// 免登录可用。查不到的 UID 不会出现在结果里。
    /// </summary>
    public async Task<IReadOnlyList<LiveSnapshot>> GetLiveStatusAsync(IReadOnlyList<long> mids, CancellationToken ct = default)
    {
        var result = new List<LiveSnapshot>();
        // 接口对 URL 长度有限制，分批查
        const int batch = 50;
        for (var i = 0; i < mids.Count; i += batch)
        {
            var chunk = mids.Skip(i).Take(batch).Where(m => m > 0).Distinct().ToList();
            if (chunk.Count == 0) continue;
            var query = string.Join("&", chunk.Select(m => $"uids%5B%5D={m}"));
            var node = await GetJsonAsync(
                "https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids?" + query,
                ct, referer: LiveReferer).ConfigureAwait(false);
            if (node["data"] is not JsonObject data) continue;
            foreach (var (_, value) in data)
            {
                if (value is null) continue;
                if (ParseSnapshot(value) is { } snap) result.Add(snap);
            }
        }
        return result;
    }

    static LiveSnapshot? ParseSnapshot(JsonNode d)
    {
        var mid = d["uid"]?.GetValue<long>() ?? 0;
        var room = d["room_id"]?.GetValue<long>() ?? 0;
        if (mid == 0 || room == 0) return null;
        var area = d["area_v2_name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(area)) area = d["area_name"]?.GetValue<string>();
        return new LiveSnapshot(
            mid, room,
            d["uname"]?.GetValue<string>() ?? "",
            d["face"]?.GetValue<string>(),
            (LiveStatus)(d["live_status"]?.GetValue<int>() ?? 0),
            d["title"]?.GetValue<string>() ?? "",
            // cover_from_user 是主播设的封面，keyframe 是实时截图（没开播时为空）
            FirstNonEmpty(d["cover_from_user"]?.GetValue<string>(), d["keyframe"]?.GetValue<string>()),
            area,
            d["online"]?.GetValue<long>() ?? 0,
            d["live_time"]?.GetValue<long>() ?? 0);
    }

    static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// 按房间号查一个直播间（手动粘贴直播链接时用：只有房间号，没有 UID）。
    /// 返回的 Uname 需要再查一次用户名片才有，这里留空由调用方补。
    /// </summary>
    public async Task<LiveSnapshot?> GetRoomAsync(long roomId, CancellationToken ct = default)
    {
        var node = await GetJsonAsync(
            $"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}",
            ct, referer: LiveReferer).ConfigureAwait(false);
        if (node["data"] is not { } d) return null;
        var mid = d["uid"]?.GetValue<long>() ?? 0;
        if (mid == 0) return null;
        var area = FirstNonEmpty(d["area_name"]?.GetValue<string>(), d["parent_area_name"]?.GetValue<string>());
        return new LiveSnapshot(
            mid,
            // 短号房间要用真实的 room_id
            d["room_id"]?.GetValue<long>() ?? roomId,
            "",
            null,
            (LiveStatus)(d["live_status"]?.GetValue<int>() ?? 0),
            d["title"]?.GetValue<string>() ?? "",
            FirstNonEmpty(d["user_cover"]?.GetValue<string>(), d["keyframe"]?.GetValue<string>()),
            area,
            d["online"]?.GetValue<long>() ?? 0,
            // get_info 的 live_time 是 "2026-11-03 10:00:00" 这种字符串，没开播时是 "0000-00-00 00:00:00"
            ParseLiveTime(d["live_time"]?.GetValue<string>()));
    }

    internal static long ParseLiveTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && !s.StartsWith("0000", StringComparison.Ordinal)
        && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var dt)
            ? new DateTimeOffset(dt).ToUnixTimeSeconds()
            : 0;

    /// <summary>
    /// 取直播流地址。免登录也能拿到原画，登录后画质选项更多。
    /// 返回的线路按稳定性排序：hls/fmp4 → hls/ts → flv，每种协议内按 B 站给的 CDN 顺序。
    /// </summary>
    public async Task<LivePlayInfo> GetRoomPlayUrlAsync(long roomId, int qn = QnOriginal, CancellationToken ct = default)
    {
        var node = await GetJsonAsync(
            $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={roomId}" +
            $"&protocol=0,1&format=0,1,2&codec=0,1&qn={qn}&platform=web&ptype=8&dolby=5&panorama=1",
            ct, referer: LiveReferer).ConfigureAwait(false);
        var d = node["data"] ?? throw new BiliApiException(-1, "直播间返回空数据");
        var status = (LiveStatus)(d["live_status"]?.GetValue<int>() ?? 0);
        var realRoom = d["room_id"]?.GetValue<long>() ?? roomId;

        var quals = new List<(int, string)>();
        var playurl = d["playurl_info"]?["playurl"];
        foreach (var q in playurl?["g_qn_desc"]?.AsArray() ?? [])
        {
            if (q?["qn"]?.GetValue<int>() is int n && q["desc"]?.GetValue<string>() is string desc)
                quals.Add((n, desc));
        }

        var lines = new List<LiveLine>();
        var current = 0;
        foreach (var stream in playurl?["stream"]?.AsArray() ?? [])
        {
            var protocol = stream?["protocol_name"]?.GetValue<string>() ?? "";
            foreach (var format in stream?["format"]?.AsArray() ?? [])
            {
                var fmt = format?["format_name"]?.GetValue<string>() ?? "";
                foreach (var codec in format?["codec"]?.AsArray() ?? [])
                {
                    if (codec is null) continue;
                    var codecName = codec["codec_name"]?.GetValue<string>() ?? "";
                    var baseUrl = codec["base_url"]?.GetValue<string>() ?? "";
                    var curQn = codec["current_qn"]?.GetValue<int>() ?? 0;
                    if (curQn > current) current = curQn;
                    if (baseUrl.Length == 0) continue;
                    foreach (var info in codec["url_info"]?.AsArray() ?? [])
                    {
                        var host = info?["host"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(host)) continue;
                        var extra = info?["extra"]?.GetValue<string>() ?? "";
                        lines.Add(new LiveLine(host + baseUrl + extra, protocol, fmt, codecName, curQn));
                    }
                }
            }
        }

        return new LivePlayInfo(realRoom, status, current, OrderLines(lines), quals);
    }

    /// <summary>
    /// 线路排序。hls/fmp4 最稳（mpv 支持最好、断流能自己重连），flv 延迟最低但容易卡住，
    /// 所以把 flv 放最后当兜底。同一协议内保留 B 站给的 CDN 顺序，AVC 优先于 HEVC（兼容性）。
    /// </summary>
    internal static IReadOnlyList<LiveLine> OrderLines(IEnumerable<LiveLine> lines) =>
        lines.Select((l, i) => (l, i))
             .OrderBy(x => ProtocolRank(x.l))
             .ThenBy(x => x.l.Codec.Equals("avc", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
             .ThenBy(x => x.i)
             .Select(x => x.l)
             .ToList();

    static int ProtocolRank(LiveLine l) =>
        (l.Protocol.ToLowerInvariant(), l.Format.ToLowerInvariant()) switch
        {
            ("http_hls", "fmp4") => 0,
            ("http_hls", "ts") => 1,
            ("http_hls", _) => 2,
            ("http_stream", "flv") => 3,
            _ => 4,
        };
}
