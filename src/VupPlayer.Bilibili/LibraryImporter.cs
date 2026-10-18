using System.Text.Json;
using VupPlayer.Core;
using VupPlayer.Core.Data;
using VupPlayer.Core.Metadata;

namespace VupPlayer.Bilibili;

public sealed record ImportProgress(string Message, int Done, int Total);

public sealed class ImportResult
{
    public int VideosAdded { get; set; }
    public int TracksAdded { get; set; }
    public int AlreadyInLibrary { get; set; }
    public List<string> Failed { get; } = [];
    public long? SourceId { get; set; }
    public string? SourceTitle { get; set; }
    public List<long> NewTrackIds { get; } = [];
    /// <summary>本次写入（新建或更新）过的视频。</summary>
    public List<string> Bvids { get; } = [];

    public string Summary =>
        $"新增 {VideosAdded} 个视频 / {TracksAdded} 首曲目" +
        (AlreadyInLibrary > 0 ? $"，已存在 {AlreadyInLibrary} 个" : "") +
        (Failed.Count > 0 ? $"，失败 {Failed.Count} 个" : "");
}

/// <summary>把 B 站链接导入本地曲库。</summary>
public sealed class LibraryImporter(BiliApi api, YtDlp ytdlp, LibraryRepository repo)
{
    public async Task<ImportResult> ImportAsync(string input, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var link = LinkParser.Parse(input);
        if (link.Kind == LinkKind.ShortLink)
        {
            progress?.Report(new ImportProgress("展开短链…", 0, 1));
            var full = await api.ExpandShortLinkAsync(link.Url!, ct).ConfigureAwait(false);
            link = LinkParser.Parse(full);
        }
        var result = new ImportResult();
        var known = LoadKnownStreamers();
        switch (link.Kind)
        {
            case LinkKind.Video:
                progress?.Report(new ImportProgress("获取视频信息…", 0, 1));
                var v = link.Bvid is not null
                    ? await api.GetVideoAsync(link.Bvid, ct).ConfigureAwait(false)
                    : await api.GetVideoByAidAsync(link.Aid!.Value, ct).ConfigureAwait(false);
                if (repo.HasVideo(v.Bvid)) result.AlreadyInLibrary++;
                Save(v, result, known);
                progress?.Report(new ImportProgress(v.Title, 1, 1));
                break;

            case LinkKind.Fav:
                await ImportPagedAsync("fav", link.MediaId!.Value.ToString(),
                    p => api.GetFavPageAsync(link.MediaId!.Value, p, ct), result, known, progress, ct).ConfigureAwait(false);
                break;

            case LinkKind.Season:
                await ImportPagedAsync("season", JsonSerializer.Serialize(new[] { link.Mid!.Value, link.SeasonId!.Value }),
                    p => api.GetSeasonPageAsync(link.Mid!.Value, link.SeasonId!.Value, p, ct), result, known, progress, ct).ConfigureAwait(false);
                break;

            case LinkKind.Space:
                await ImportViaYtDlpAsync("space", link.Mid!.Value.ToString(), $"https://space.bilibili.com/{link.Mid}/video",
                    result, known, progress, ct).ConfigureAwait(false);
                break;

            case LinkKind.Other:
                await ImportViaYtDlpAsync("url", link.Url!, link.Url!, result, known, progress, ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentException("无法识别的链接：" + input);
        }
        return result;
    }

    /// <summary>重新同步一个订阅源，只导入新视频。</summary>
    public Task<ImportResult> SyncSourceAsync(Source source, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var known = LoadKnownStreamers();
        var result = new ImportResult();
        switch (source.Kind)
        {
            case "fav":
                var mediaId = long.Parse(source.RemoteId);
                return Run(ImportPagedAsync("fav", source.RemoteId, p => api.GetFavPageAsync(mediaId, p, ct), result, known, progress, ct, incremental: true));
            case "season":
                var ids = JsonSerializer.Deserialize<long[]>(source.RemoteId)!;
                return Run(ImportPagedAsync("season", source.RemoteId, p => api.GetSeasonPageAsync(ids[0], ids[1], p, ct), result, known, progress, ct, incremental: true));
            case "space":
                return Run(ImportViaYtDlpAsync("space", source.RemoteId, $"https://space.bilibili.com/{source.RemoteId}/video", result, known, progress, ct));
            case "discovery":
                // 「B站发现」由推荐器自动导入，没有可同步的远端列表
                return Task.FromResult(result);
            default:
                return Run(ImportViaYtDlpAsync(source.Kind, source.RemoteId, source.RemoteId, result, known, progress, ct));
        }

        async Task<ImportResult> Run(Task t) { await t.ConfigureAwait(false); return result; }
    }

    async Task ImportPagedAsync(string kind, string remoteId, Func<int, Task<ListPage>> fetch, ImportResult result,
        List<string> known, IProgress<ImportProgress>? progress, CancellationToken ct, bool incremental = false)
    {
        var page = 1;
        var done = 0;
        long? sourceId = null;
        var consecutiveKnown = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lp = await fetch(page).ConfigureAwait(false);
            if (sourceId is null)
            {
                sourceId = repo.UpsertSource(kind, remoteId, lp.Title);
                result.SourceId = sourceId;
                result.SourceTitle = lp.Title;
            }
            foreach (var item in lp.Items)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new ImportProgress(item.Title, done, lp.Total));
                if (item.Invalid) { result.Failed.Add($"{item.Bvid}（已失效）"); continue; }
                if (repo.HasVideo(item.Bvid))
                {
                    result.AlreadyInLibrary++;
                    repo.LinkSourceVideo(sourceId.Value, item.Bvid);
                    consecutiveKnown++;
                    continue;
                }
                consecutiveKnown = 0;
                try
                {
                    if (item.PageCount == 1 && item.FirstCid is long cid && item.UpMid != 0)
                    {
                        // 单P视频：收藏夹里的信息已经够用，省一次请求
                        var v = new BiliVideo(item.Bvid, 0, item.Title, item.Cover, item.Desc, item.Pubdate,
                            item.UpMid, item.UpName, [new BiliPage(cid, 1, item.Title, item.Duration)], null, null);
                        Save(v, result, known);
                    }
                    else
                    {
                        Save(await api.GetVideoAsync(item.Bvid, ct).ConfigureAwait(false), result, known);
                    }
                    repo.LinkSourceVideo(sourceId.Value, item.Bvid);
                }
                catch (BiliApiException ex) when (!ex.IsRiskControl)
                {
                    result.Failed.Add($"{item.Bvid}（{ex.Message}）");
                }
            }
            // 增量同步：按时间倒序排列的列表，连续 40 个都已在库里就停止
            if (incremental && kind == "fav" && consecutiveKnown >= 40) break;
            if (!lp.HasMore || lp.Items.Count == 0) break;
            page++;
        }
        if (sourceId is long sid) repo.MarkSourceSynced(sid);
    }

    async Task ImportViaYtDlpAsync(string kind, string remoteId, string url, ImportResult result,
        List<string> known, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ImportProgress("用 yt-dlp 展开列表（可能需要几十秒）…", 0, 0));
        var (title, bvids) = await ytdlp.ExpandListAsync(url, ct).ConfigureAwait(false);
        var sourceId = repo.UpsertSource(kind, remoteId, title);
        result.SourceId = sourceId;
        result.SourceTitle = title;
        var done = 0;
        foreach (var bvid in bvids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(bvid, ++done, bvids.Count));
            if (repo.HasVideo(bvid)) { result.AlreadyInLibrary++; repo.LinkSourceVideo(sourceId, bvid); continue; }
            try
            {
                Save(await api.GetVideoAsync(bvid, ct).ConfigureAwait(false), result, known);
                repo.LinkSourceVideo(sourceId, bvid);
            }
            catch (BiliApiException ex) when (!ex.IsRiskControl)
            {
                result.Failed.Add($"{bvid}（{ex.Message}）");
            }
        }
        repo.MarkSourceSynced(sourceId);
    }

    void Save(BiliVideo v, ImportResult result, List<string> known)
    {
        var isNew = !repo.HasVideo(v.Bvid);
        var video = new Video
        {
            Bvid = v.Bvid, Title = v.Title, UpMid = v.OwnerMid, UpName = v.OwnerName,
            CoverUrl = NormalizeCover(v.Cover), Description = v.Desc, Pubdate = v.Pubdate,
        };
        var pages = v.Pages.Select(p => new Track
        {
            Bvid = v.Bvid, Cid = p.Cid, Page = p.Page, PartTitle = p.Part, DurationSec = p.Duration,
        }).ToList();
        var created = repo.ImportVideo(video, pages, t =>
        {
            var (song, streamer) = TitleParser.Guess(v.Title, t.PartTitle, pages.Count, known);
            if (streamer is not null && !known.Contains(streamer, StringComparer.OrdinalIgnoreCase)) known.Add(streamer);
            return (song, streamer);
        });
        if (isNew) result.VideosAdded++;
        if (!result.Bvids.Contains(v.Bvid)) result.Bvids.Add(v.Bvid);
        result.TracksAdded += created.Count;
        result.NewTrackIds.AddRange(created);
    }

    List<string> LoadKnownStreamers()
    {
        var list = new List<string>();
        foreach (var s in repo.GetStreamers())
        {
            list.Add(s.Name);
            if (s.Aliases is not null) list.AddRange(JsonSerializer.Deserialize<string[]>(s.Aliases) ?? []);
        }
        return list;
    }

    static string? NormalizeCover(string? url) =>
        url is null ? null : url.StartsWith("//") ? "https:" + url : url.Replace("http://", "https://");
}
