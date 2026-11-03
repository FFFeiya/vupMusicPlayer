using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VupPlayer.Bilibili;

public sealed class BiliApiException(int code, string message) : Exception($"B站接口错误 {code}：{message}")
{
    public int Code { get; } = code;
    /// <summary>-412 / -352 / -799 等风控类错误。</summary>
    public bool IsRiskControl => Code is -412 or -352 or -799 or 412;
}

public sealed record BiliPage(long Cid, int Page, string Part, int Duration);

public sealed record BiliVideo(
    string Bvid, long Aid, string Title, string? Cover, string? Desc, long Pubdate,
    long OwnerMid, string OwnerName, IReadOnlyList<BiliPage> Pages, long? SeasonId, long? SeasonOwnerMid);

public sealed record BiliUser(bool IsLogin, long Mid, string Name, string? Face, bool IsVip);

public sealed record FavFolder(long Id, string Title, int MediaCount);

/// <summary>收藏夹、合集等列表里的一项。FirstCid 有值且 PageCount 为 1 时，可以不再请求视频详情。</summary>
public sealed record ListItem(string Bvid, string Title, string? Cover, string? Desc, int Duration, int PageCount,
    long? FirstCid, long UpMid, string UpName, long Pubdate, bool Invalid);

public sealed record ListPage(string? Title, int Total, IReadOnlyList<ListItem> Items, bool HasMore);

/// <summary>B 站视频搜索结果的一项。</summary>
public sealed record SearchItem(string Bvid, string Title, string Uploader, long UploaderMid, int DurationSec,
    long PlayCount, long Pubdate, string? Cover, string? Description);

public sealed record SearchPage(int Total, int Page, IReadOnlyList<SearchItem> Items);

/// <summary>B 站用户搜索结果。</summary>
public sealed record UserSearchItem(long Mid, string Name, long Fans, int Videos, string? Sign, string? Verify, bool HasLiveRoom);

public sealed record DashStream(int Id, string Url, IReadOnlyList<string> BackupUrls, long Bandwidth, string Codecs, int Width, int Height, int CodecId);

public sealed record PlayUrlInfo(IReadOnlyList<DashStream> Videos, IReadOnlyList<DashStream> Audios, string? MuxedUrl, string? QualityDesc);

public enum QrState { Waiting, Scanned, Expired, Success }

/// <summary>B 站网页端接口的最小封装：带 cookie、限流、WBI 签名和 buvid 初始化。</summary>
public sealed partial class BiliApi : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    public const string Referer = "https://www.bilibili.com/";

    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _lastRequest = DateTime.MinValue;
    string? _mixinKey;
    DateTime _mixinKeyAt;

    public CookieContainer Cookies { get; private set; }

    /// <summary>两次请求的最小间隔，避免触发风控。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(350);

    public BiliApi(CookieContainer? cookies = null)
    {
        Cookies = cookies ?? new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(Referer);
        _http.DefaultRequestHeaders.Add("Origin", "https://www.bilibili.com");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
    }

    /// <summary>cookie 发生变化（登录、获取 buvid）时触发，用于持久化。</summary>
    public event Action? CookiesChanged;

    public bool HasLoginCookie => Cookies.GetCookies(new Uri("https://www.bilibili.com")).Any(c => c.Name == "SESSDATA");

    // ---------------- 底层 ----------------

    /// <param name="referer">覆盖默认 Referer（直播接口和直播流要求 live.bilibili.com）。</param>
    async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct, bool checkCode = true, string? referer = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 150)), ct).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) req.Headers.Referrer = new Uri(referer);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed) throw new BiliApiException(412, "请求被风控拦截，请稍后再试或登录");
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode node;
        try
        {
            node = JsonNode.Parse(text) ?? throw new BiliApiException(-1, "空响应");
        }
        catch (JsonException)
        {
            throw new BiliApiException((int)resp.StatusCode, "返回的不是 JSON");
        }
        if (checkCode)
        {
            var code = node["code"]?.GetValue<int>() ?? 0;
            if (code != 0) throw new BiliApiException(code, node["message"]?.GetValue<string>() ?? "未知错误");
        }
        return node;
    }

    /// <summary>确保有 buvid3/buvid4（很多接口没有它会返回 412）。</summary>
    public async Task EnsureBuvidAsync(CancellationToken ct = default)
    {
        var cookies = Cookies.GetCookies(new Uri("https://www.bilibili.com"));
        if (cookies.Any(c => c.Name == "buvid3")) return;
        var node = await GetJsonAsync("https://api.bilibili.com/x/frontend/finger/spi", ct).ConfigureAwait(false);
        var b3 = node["data"]?["b_3"]?.GetValue<string>();
        var b4 = node["data"]?["b_4"]?.GetValue<string>();
        var expires = DateTime.Now.AddYears(1);
        if (!string.IsNullOrEmpty(b3)) Cookies.Add(new Cookie("buvid3", b3, "/", ".bilibili.com") { Expires = expires });
        if (!string.IsNullOrEmpty(b4)) Cookies.Add(new Cookie("buvid4", b4, "/", ".bilibili.com") { Expires = expires });
        Cookies.Add(new Cookie("b_nut", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), "/", ".bilibili.com") { Expires = expires });
        CookiesChanged?.Invoke();
    }

    async Task<string> GetMixinKeyAsync(CancellationToken ct)
    {
        if (_mixinKey is not null && DateTime.UtcNow - _mixinKeyAt < TimeSpan.FromHours(1)) return _mixinKey;
        var node = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct, checkCode: false).ConfigureAwait(false);
        var img = node["data"]?["wbi_img"]?["img_url"]?.GetValue<string>() ?? throw new BiliApiException(-1, "取不到 WBI 密钥");
        var sub = node["data"]?["wbi_img"]?["sub_url"]?.GetValue<string>() ?? throw new BiliApiException(-1, "取不到 WBI 密钥");
        _mixinKey = WbiSigner.MixinKey(WbiSigner.KeyFromUrl(img), WbiSigner.KeyFromUrl(sub));
        _mixinKeyAt = DateTime.UtcNow;
        return _mixinKey;
    }

    async Task<JsonNode> GetWbiAsync(string baseUrl, IEnumerable<KeyValuePair<string, string>> args, CancellationToken ct)
    {
        await EnsureBuvidAsync(ct).ConfigureAwait(false);
        var key = await GetMixinKeyAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetJsonAsync(baseUrl + "?" + WbiSigner.Sign(args, key), ct).ConfigureAwait(false);
        }
        catch (BiliApiException ex) when (ex.Code == -403)
        {
            // 密钥可能已轮换：刷新后重试一次
            _mixinKey = null;
            key = await GetMixinKeyAsync(ct).ConfigureAwait(false);
            return await GetJsonAsync(baseUrl + "?" + WbiSigner.Sign(args, key), ct).ConfigureAwait(false);
        }
    }

    static KeyValuePair<string, string> P(string k, object v) => new(k, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!);

    // ---------------- 用户 ----------------

    public async Task<BiliUser> GetMyselfAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct, checkCode: false).ConfigureAwait(false);
        var d = node["data"];
        if (d?["isLogin"]?.GetValue<bool>() != true) return new BiliUser(false, 0, "", null, false);
        return new BiliUser(true, d["mid"]!.GetValue<long>(), d["uname"]?.GetValue<string>() ?? "",
            d["face"]?.GetValue<string>(), (d["vipStatus"]?.GetValue<int>() ?? 0) == 1);
    }

    // ---------------- 扫码登录 ----------------

    public async Task<(string Url, string Key)> CreateQrLoginAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", ct).ConfigureAwait(false);
        return (node["data"]!["url"]!.GetValue<string>(), node["data"]!["qrcode_key"]!.GetValue<string>());
    }

    public async Task<QrState> PollQrLoginAsync(string key, CancellationToken ct = default)
    {
        var node = await GetJsonAsync($"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        var code = node["data"]?["code"]?.GetValue<int>() ?? -1;
        switch (code)
        {
            case 0:
                _mixinKey = null;
                CookiesChanged?.Invoke();
                return QrState.Success;
            case 86090: return QrState.Scanned;
            case 86038: return QrState.Expired;
            default: return QrState.Waiting;   // 86101 未扫码
        }
    }

    public void Logout()
    {
        // HttpClientHandler 的 CookieContainer 不能替换，只能逐个置过期
        foreach (Cookie c in Cookies.GetAllCookies())
            if (c.Name is not ("buvid3" or "buvid4" or "b_nut")) c.Expired = true;
        _mixinKey = null;
        CookiesChanged?.Invoke();
    }

    // ---------------- 视频 ----------------

    public async Task<BiliVideo> GetVideoAsync(string bvid, CancellationToken ct = default)
    {
        var node = await GetWbiAsync("https://api.bilibili.com/x/web-interface/wbi/view", [P("bvid", bvid)], ct).ConfigureAwait(false);
        return ParseVideo(node["data"]!);
    }

    public async Task<BiliVideo> GetVideoByAidAsync(long aid, CancellationToken ct = default)
    {
        var node = await GetWbiAsync("https://api.bilibili.com/x/web-interface/wbi/view", [P("aid", aid)], ct).ConfigureAwait(false);
        return ParseVideo(node["data"]!);
    }

    static BiliVideo ParseVideo(JsonNode d)
    {
        var pages = d["pages"]?.AsArray().Select(p => new BiliPage(
            p!["cid"]!.GetValue<long>(), p["page"]!.GetValue<int>(), p["part"]?.GetValue<string>() ?? "",
            p["duration"]?.GetValue<int>() ?? 0)).ToList() ?? [];
        var season = d["ugc_season"];
        return new BiliVideo(
            d["bvid"]!.GetValue<string>(), d["aid"]!.GetValue<long>(), d["title"]!.GetValue<string>(),
            d["pic"]?.GetValue<string>(), d["desc"]?.GetValue<string>(), d["pubdate"]?.GetValue<long>() ?? 0,
            d["owner"]!["mid"]!.GetValue<long>(), d["owner"]!["name"]!.GetValue<string>(), pages,
            season?["id"]?.GetValue<long>(), season?["mid"]?.GetValue<long>());
    }

    /// <summary>取 DASH 播放地址（fnval=4048：DASH + HDR + 4K + 杜比 + 8K + AV1）。</summary>
    public async Task<PlayUrlInfo> GetPlayUrlAsync(string bvid, long cid, CancellationToken ct = default)
    {
        var node = await GetWbiAsync("https://api.bilibili.com/x/player/wbi/playurl",
        [
            P("bvid", bvid), P("cid", cid), P("fnval", 4048), P("fnver", 0), P("fourk", 1), P("qn", 127),
        ], ct).ConfigureAwait(false);
        var d = node["data"]!;
        var dash = d["dash"];
        var videos = new List<DashStream>();
        var audios = new List<DashStream>();
        if (dash is not null)
        {
            foreach (var v in dash["video"]?.AsArray() ?? []) videos.Add(ParseStream(v!));
            foreach (var a in dash["audio"]?.AsArray() ?? []) audios.Add(ParseStream(a!));
            if (dash["flac"]?["audio"] is JsonObject flac) audios.Add(ParseStream(flac) with { Bandwidth = long.MaxValue / 2 });
            foreach (var a in dash["dolby"]?["audio"]?.AsArray() ?? []) audios.Add(ParseStream(a!));
        }
        string? muxed = null;
        if (videos.Count == 0 && d["durl"]?.AsArray() is { Count: > 0 } durl)
            muxed = durl[0]!["url"]!.GetValue<string>();
        var qn = d["quality"]?.GetValue<int>();
        var descs = d["accept_description"]?.AsArray();
        var quals = d["accept_quality"]?.AsArray();
        string? qdesc = null;
        if (qn is not null && descs is not null && quals is not null)
        {
            for (var i = 0; i < quals.Count && i < descs.Count; i++)
                if (quals[i]!.GetValue<int>() == qn) qdesc = descs[i]!.GetValue<string>();
        }
        return new PlayUrlInfo(videos, audios, muxed, qdesc);
    }

    static DashStream ParseStream(JsonNode s)
    {
        var url = (s["baseUrl"] ?? s["base_url"])!.GetValue<string>();
        var backups = (s["backupUrl"] ?? s["backup_url"])?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
        return new DashStream(
            s["id"]?.GetValue<int>() ?? 0, url, backups, s["bandwidth"]?.GetValue<long>() ?? 0,
            s["codecs"]?.GetValue<string>() ?? "", s["width"]?.GetValue<int>() ?? 0, s["height"]?.GetValue<int>() ?? 0,
            s["codecid"]?.GetValue<int>() ?? 0);
    }

    // ---------------- 收藏夹 ----------------

    public async Task<IReadOnlyList<FavFolder>> GetFavFoldersAsync(long upMid, CancellationToken ct = default)
    {
        var node = await GetJsonAsync($"https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid={upMid}&type=2", ct).ConfigureAwait(false);
        return node["data"]?["list"]?.AsArray().Select(f => new FavFolder(
            f!["id"]!.GetValue<long>(), f["title"]!.GetValue<string>(), f["media_count"]?.GetValue<int>() ?? 0)).ToList() ?? [];
    }

    public async Task<ListPage> GetFavPageAsync(long mediaId, int page, CancellationToken ct = default)
    {
        var node = await GetJsonAsync(
            $"https://api.bilibili.com/x/v3/fav/resource/list?media_id={mediaId}&pn={page}&ps=20&keyword=&order=mtime&type=0&tid=0&platform=web", ct).ConfigureAwait(false);
        var d = node["data"]!;
        var items = new List<ListItem>();
        foreach (var m in d["medias"]?.AsArray() ?? [])
        {
            if (m is null) continue;
            if ((m["type"]?.GetValue<int>() ?? 2) != 2) continue;   // 只要视频
            var bvid = m["bvid"]?.GetValue<string>() ?? m["bv_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(bvid)) continue;
            var title = m["title"]?.GetValue<string>() ?? "";
            items.Add(new ListItem(bvid, title,m["cover"]?.GetValue<string>(), m["intro"]?.GetValue<string>(),
                m["duration"]?.GetValue<int>() ?? 0, m["page"]?.GetValue<int>() ?? 1, m["ugc"]?["first_cid"]?.GetValue<long>(),
                m["upper"]?["mid"]?.GetValue<long>() ?? 0, m["upper"]?["name"]?.GetValue<string>() ?? "",
                m["pubtime"]?.GetValue<long>() ?? 0, title == "已失效视频"));
        }
        return new ListPage(d["info"]?["title"]?.GetValue<string>(), d["info"]?["media_count"]?.GetValue<int>() ?? items.Count,
            items, d["has_more"]?.GetValue<bool>() ?? false);
    }

    // ---------------- 合集 ----------------

    public async Task<ListPage> GetSeasonPageAsync(long mid, long seasonId, int page, CancellationToken ct = default)
    {
        var node = await GetJsonAsync(
            $"https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid={mid}&season_id={seasonId}&sort_reverse=false&page_num={page}&page_size=30", ct).ConfigureAwait(false);
        var d = node["data"]!;
        var items = (d["archives"]?.AsArray() ?? []).Where(a => a is not null).Select(a => new ListItem(
            a!["bvid"]!.GetValue<string>(), a["title"]?.GetValue<string>() ?? "", a["pic"]?.GetValue<string>(), null,
            a["duration"]?.GetValue<int>() ?? 0, 0, null, mid, "", a["pubdate"]?.GetValue<long>() ?? 0, false)).ToList();
        var total = d["page"]?["total"]?.GetValue<int>() ?? items.Count;
        return new ListPage(d["meta"]?["name"]?.GetValue<string>(), total, items, page * 30 < total);
    }

    // ---------------- 搜索 ----------------

    /// <summary>
    /// 搜索视频。order：totalrank（综合）/ click（播放多）/ pubdate（最新）/ dm（弹幕多）/ stow（收藏多）。
    /// </summary>
    public async Task<SearchPage> SearchVideosAsync(string keyword, int page = 1, string order = "totalrank", CancellationToken ct = default)
    {
        var node = await GetWbiAsync("https://api.bilibili.com/x/web-interface/wbi/search/type",
        [
            P("search_type", "video"), P("keyword", keyword), P("page", page), P("order", order),
        ], ct).ConfigureAwait(false);
        var d = node["data"];
        var items = new List<SearchItem>();
        foreach (var r in d?["result"]?.AsArray() ?? [])
        {
            if (r is null || r["type"]?.GetValue<string>() is string t && t != "video") continue;
            var bvid = r["bvid"]?.GetValue<string>();
            if (string.IsNullOrEmpty(bvid)) continue;
            items.Add(new SearchItem(
                bvid,
                CleanHtml(r["title"]?.GetValue<string>() ?? ""),
                r["author"]?.GetValue<string>() ?? "",
                ReadLong(r["mid"]),
                ParseDuration(r["duration"]?.ToString()),
                ReadLong(r["play"]),
                ReadLong(r["pubdate"]),
                NormalizeUrl(r["pic"]?.GetValue<string>()),
                r["description"]?.GetValue<string>()));
        }
        return new SearchPage(d?["numResults"]?.GetValue<int>() ?? items.Count, page, items);
    }

    /// <summary>按 UID 查用户名（用户名片接口）。</summary>
    public async Task<string?> GetUserNameAsync(long mid, CancellationToken ct = default)
    {
        await EnsureBuvidAsync(ct).ConfigureAwait(false);
        var node = await GetJsonAsync($"https://api.bilibili.com/x/web-interface/card?mid={mid}", ct).ConfigureAwait(false);
        return node["data"]?["card"]?["name"]?.GetValue<string>();
    }

    /// <summary>搜索 B 站用户（用来把主播名对应到唯一的 UID）。</summary>
    public async Task<IReadOnlyList<UserSearchItem>> SearchUsersAsync(string keyword, CancellationToken ct = default)
    {
        var node = await GetWbiAsync("https://api.bilibili.com/x/web-interface/wbi/search/type",
            [P("search_type", "bili_user"), P("keyword", keyword), P("page", 1)], ct).ConfigureAwait(false);
        var list = new List<UserSearchItem>();
        foreach (var r in node["data"]?["result"]?.AsArray() ?? [])
        {
            if (r is null) continue;
            list.Add(new UserSearchItem(
                ReadLong(r["mid"]),
                CleanHtml(r["uname"]?.GetValue<string>() ?? ""),
                ReadLong(r["fans"]),
                (int)ReadLong(r["videos"]),
                r["usign"]?.GetValue<string>(),
                r["official_verify"]?["desc"]?.GetValue<string>() is { Length: > 0 } desc ? desc : null,
                ReadLong(r["room_id"]) > 0));
        }
        return list;
    }

    /// <summary>搜索结果的标题带 &lt;em class="keyword"&gt; 高亮和 HTML 实体，去掉。</summary>
    internal static string CleanHtml(string s) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "")).Trim();

    /// <summary>「4:6」「12:34」「1:02:03」→ 秒。</summary>
    internal static int ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var total = 0;
        foreach (var part in s.Split(':'))
        {
            if (!int.TryParse(part, out var v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>有些字段有时是数字有时是字符串（例如播放量「--」）。</summary>
    static long ReadLong(JsonNode? n) =>
        n is null ? 0 : long.TryParse(n.ToString(), out var v) ? v : 0;

    static string? NormalizeUrl(string? url) =>
        url is null ? null : url.StartsWith("//") ? "https:" + url : url;

    // ---------------- 短链 ----------------

    /// <summary>展开 b23.tv 短链。</summary>
    public async Task<string> ExpandShortLinkAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return resp.RequestMessage?.RequestUri?.ToString() ?? url;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
