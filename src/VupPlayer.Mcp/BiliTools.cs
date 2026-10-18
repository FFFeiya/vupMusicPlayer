using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using VupPlayer.Bilibili;
using VupPlayer.Core;
using VupPlayer.Core.Control;
using VupPlayer.Core.Data;

namespace VupPlayer.Mcp;

/// <summary>
/// B 站相关工具：在 MCP 进程里直接访问 B 站（使用播放器保存的登录 cookie，只读不写），播放器不开也能用。
/// </summary>
[McpServerToolType]
public sealed class BiliTools(LibraryRepository repo)
{
    static readonly Lazy<BiliApi> Api = new(() => new BiliApi(CookieStore.Load()));

    [McpServerTool(Name = "search_bilibili", ReadOnly = true, OpenWorld = true)]
    [Description("""
        在 B 站搜索视频，用来找曲库里还没有的切片。返回候选列表和判断线索：
        matches_all_terms = 标题包含关键词里的每个词；in_library = 已在曲库；known_uploader = UP 主是曲库里已有的切片 man。
        关键词建议写成「主播 歌名」，例如「阿梓 绝不认输」。
        """)]
    public async Task<string> SearchBilibili(
        [Description("关键词，空格分隔多个词")] string keyword,
        [Description("页码，从 1 开始")] int page = 1,
        [Description("排序：totalrank 综合 / click 播放多 / pubdate 最新 / stow 收藏多")] string order = "totalrank",
        [Description("最多返回条数（每页最多 20）")] int limit = 20)
    {
        var result = await Api.Value.SearchVideosAsync(keyword.Trim(), Math.Max(1, page), order);
        var terms = keyword.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries);
        var uploaders = repo.GetUploaderMids();
        var items = result.Items.Take(Math.Clamp(limit, 1, 20)).Select(i => new
        {
            bvid = i.Bvid,
            title = i.Title,
            uploader = i.Uploader,
            uploader_mid = i.UploaderMid,
            duration_sec = i.DurationSec,
            play_count = i.PlayCount,
            pubdate = i.Pubdate > 0 ? DateTimeOffset.FromUnixTimeSeconds(i.Pubdate).ToLocalTime().ToString("yyyy-MM-dd") : null,
            matches_all_terms = terms.All(t => i.Title.Contains(t, StringComparison.OrdinalIgnoreCase)),
            in_library = repo.HasVideo(i.Bvid),
            known_uploader = uploaders.Contains(i.UploaderMid),
            url = $"https://www.bilibili.com/video/{i.Bvid}",
        });
        return LibraryTools.Out(new { total = result.Total, page = result.Page, items });
    }

    [McpServerTool(Name = "import_from_bilibili", OpenWorld = true)]
    [Description("""
        把 B 站内容导入曲库：BV 号、视频链接、收藏夹、合集或 UP 主空间链接。已在曲库的视频不会重复导入。
        queue 决定导入后要不要放进播放器的队列（需要播放器在运行）：none 不放 / next 下一首播放 / end 加到队尾 / now 立刻播放。
        合集类视频有很多分P时，用 match 只挑标题包含该词的分P放进队列，例如 match="绝不认输"。
        返回导入（或已存在）的曲目，可以再用 update_tracks 修正歌名和主播。
        """)]
    public async Task<string> ImportFromBilibili(
        [Description("BV 号或链接")] string input,
        [Description("none / next / end / now")] string queue = "none",
        [Description("可选：只把标题包含这个词的分P放进队列")] string? match = null)
    {
        var importer = new LibraryImporter(Api.Value, new YtDlp(YtDlp.Locate(), () => Api.Value.Cookies), repo);
        ImportResult r;
        try
        {
            r = await importer.ImportAsync(input);
        }
        catch (Exception ex)
        {
            return LibraryTools.Out(new { ok = false, error = ex.Message });
        }

        // 收集这次涉及的曲目：新建的 + 单个视频链接对应的已有曲目
        var tracks = r.Bvids.SelectMany(b => repo.GetTracksByBvid(b)).ToList();
        if (tracks.Count == 0) tracks = repo.GetTracks(r.NewTrackIds).ToList();

        var toQueue = tracks;
        string? matchNote = null;
        if (!string.IsNullOrWhiteSpace(match))
        {
            var m = match.Trim();
            var hit = tracks.Where(t =>
                $"{t.SongTitle} {t.PartTitle} {(t.PageCount <= 1 ? t.VideoTitle : "")}".Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count > 0) toQueue = hit;
            else matchNote = $"没有分P标题包含「{m}」，按全部曲目处理";
        }

        try { await ControlClient.SendAsync("library_changed", timeoutMs: 1500); } catch { }

        object? queued = null;
        var q = queue.Trim().ToLowerInvariant();
        if (q != "none" && toQueue.Count > 0)
        {
            var ids = new JsonArray(toQueue.Select(t => (JsonNode)JsonValue.Create(t.Id)).ToArray());
            var resp = q switch
            {
                "now" => await ControlClient.SendAsync("play_now", new JsonObject { ["track_ids"] = ids }, 8000),
                "end" => await ControlClient.SendAsync("enqueue", new JsonObject { ["track_ids"] = ids, ["next"] = false }, 8000),
                _ => await ControlClient.SendAsync("enqueue", new JsonObject { ["track_ids"] = ids, ["next"] = true }, 8000),
            };
            queued = resp is null ? "播放器没有运行，已导入曲库但没有放进队列"
                : resp.Ok ? $"已放进队列（{q}）：{toQueue.Count} 首" : "放进队列失败：" + resp.Error;
        }

        return LibraryTools.Out(new
        {
            ok = true,
            summary = r.Summary,
            source = r.SourceTitle,
            failed = r.Failed.Count > 0 ? r.Failed : null,
            match_note = matchNote,
            queued,
            tracks = toQueue.Take(50).Select(t => new
            {
                id = t.Id,
                title = t.DisplayTitle,
                song_title = t.SongTitle,
                streamer = t.StreamerName,
                uploader = t.UpName,
                page = t.PageCount > 1 ? t.Page : (int?)null,
                duration_sec = t.DurationSec,
            }),
            more_tracks = toQueue.Count > 50 ? toQueue.Count - 50 : (int?)null,
        });
    }
}
