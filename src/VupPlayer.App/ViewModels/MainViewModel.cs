using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VupPlayer.App.Services;
using VupPlayer.Bilibili;
using VupPlayer.Core;
using VupPlayer.Core.Data;
using VupPlayer.Core.Streams;

namespace VupPlayer.App.ViewModels;

public enum NavKind { All, Favorites, Untagged, Recent, Playlist, Streamer, Uploader, Tag, Source, Suggestions }

public sealed partial class NavItem : ObservableObject
{
    public required string Section { get; init; }
    public required NavKind Kind { get; init; }
    public required string Name { get; init; }
    public long? Id { get; init; }
    [ObservableProperty] public partial int Count { get; set; }
    public string Icon => Kind switch
    {
        NavKind.All => "", NavKind.Favorites => "", NavKind.Untagged => "", NavKind.Recent => "",
        NavKind.Playlist => "", NavKind.Streamer => "", NavKind.Uploader => "", NavKind.Tag => "",
        NavKind.Source => "", NavKind.Suggestions => "", _ => "",
    };
}

public sealed partial class MainViewModel : ObservableObject
{
    readonly LibraryRepository _repo;
    readonly BiliApi _api;
    readonly LibraryImporter _importer;
    CancellationTokenSource? _importCts;

    public PlayerController Player { get; }

    public ObservableCollection<NavItem> NavItems { get; } = [];
    public ObservableCollection<TrackView> Tracks { get; } = [];
    public ObservableCollection<TrackView> Upcoming { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    [ObservableProperty] public partial NavItem? SelectedNav { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string ImportText { get; set; } = "";
    [ObservableProperty] public partial string? ImportStatus { get; set; }
    [ObservableProperty] public partial bool IsImporting { get; set; }
    [ObservableProperty] public partial string? UserName { get; set; }
    [ObservableProperty] public partial bool IsLoggedIn { get; set; }
    [ObservableProperty] public partial string ListTitle { get; set; } = "全部曲目";
    [ObservableProperty] public partial string? ListSubtitle { get; set; }

    public MainViewModel(LibraryRepository repo, BiliApi api, LibraryImporter importer, PlayerController player)
    {
        _repo = repo;
        _api = api;
        _importer = importer;
        Player = player;
        Player.Queue.Changed += () => Application.Current.Dispatcher.BeginInvoke(RefreshUpcoming);
        Player.PlayRecorded += () => Application.Current.Dispatcher.BeginInvoke(RefreshNavCounts);
    }

    // ---------------- 导航 ----------------

    public void Initialize()
    {
        RebuildNav();
        SelectedNav = NavItems.FirstOrDefault();
        _ = RefreshLoginAsync();
    }

    public void RebuildNav()
    {
        var selectedKey = SelectedNav is { } s ? (s.Kind, s.Id) : (NavKind.All, (long?)null);
        NavItems.Clear();
        NavItems.Add(new NavItem { Section = "资料库", Kind = NavKind.All, Name = "全部曲目" });
        NavItems.Add(new NavItem { Section = "资料库", Kind = NavKind.Favorites, Name = "我喜欢" });
        NavItems.Add(new NavItem { Section = "资料库", Kind = NavKind.Recent, Name = "最常播放" });
        NavItems.Add(new NavItem { Section = "资料库", Kind = NavKind.Untagged, Name = "待整理" });
        var pending = _repo.ListPendingSuggestions().Count;
        if (pending > 0) NavItems.Add(new NavItem { Section = "资料库", Kind = NavKind.Suggestions, Name = "待确认的修改", Count = pending });

        Playlists.Clear();
        foreach (var p in _repo.ListPlaylists())
        {
            Playlists.Add(p);
            NavItems.Add(new NavItem { Section = "歌单", Kind = NavKind.Playlist, Name = p.Name, Id = p.Id, Count = p.ItemCount });
        }
        foreach (var s2 in _repo.ListStreamers().Where(x => x.Count > 0))
            NavItems.Add(new NavItem { Section = "主播", Kind = NavKind.Streamer, Name = s2.Name, Id = s2.Id, Count = s2.Count });
        foreach (var u in _repo.ListUploaders())
            NavItems.Add(new NavItem { Section = "切片 man", Kind = NavKind.Uploader, Name = u.Name, Id = u.Id, Count = u.Count });
        foreach (var t in _repo.ListTags().Where(x => x.Count > 0))
            NavItems.Add(new NavItem { Section = "标签", Kind = NavKind.Tag, Name = t.Name, Id = t.Id, Count = t.Count });
        foreach (var src in _repo.ListSources())
            NavItems.Add(new NavItem { Section = "订阅源", Kind = NavKind.Source, Name = (src.Kind switch { "fav" => "收藏夹 · ", "season" => "合集 · ", "space" => "UP主 · ", _ => "" }) + (src.Title ?? src.RemoteId), Id = src.Id, Count = src.VideoCount });

        RefreshNavCounts();
        var again = NavItems.FirstOrDefault(n => n.Kind == selectedKey.Item1 && n.Id == selectedKey.Item2) ?? NavItems[0];
        if (!ReferenceEquals(SelectedNav, again)) SelectedNav = again;
        else RefreshTracks();
    }

    void RefreshNavCounts()
    {
        foreach (var n in NavItems)
        {
            n.Count = n.Kind switch
            {
                NavKind.All => _repo.CountTracks(new TrackQuery()),
                NavKind.Favorites => _repo.CountTracks(new TrackQuery { Favorite = true }),
                NavKind.Untagged => CountUntagged(),
                NavKind.Recent => _repo.GetPlayStats(null, 100).Count,
                _ => n.Count,
            };
        }
    }

    int CountUntagged() =>
        _repo.CountTracks(new TrackQuery { MissingSongTitle = true }) + _repo.CountTracks(new TrackQuery { MissingStreamer = true, MissingSongTitle = false });

    partial void OnSelectedNavChanged(NavItem? value) => RefreshTracks();

    partial void OnSearchTextChanged(string value) => RefreshTracks();

    public void RefreshTracks()
    {
        var nav = SelectedNav;
        var q = new TrackQuery { Keyword = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText };
        IReadOnlyList<TrackView> list;
        ListTitle = nav?.Name ?? "全部曲目";
        ListSubtitle = null;
        switch (nav?.Kind)
        {
            case NavKind.Favorites: q.Favorite = true; break;
            case NavKind.Playlist: q.PlaylistId = nav.Id; break;
            case NavKind.Streamer: q.StreamerId = nav.Id; break;
            case NavKind.Uploader: q.UpMid = nav.Id; break;
            case NavKind.Tag: q.Tag = nav.Name; break;
            case NavKind.Recent: q.OrderBy = "plays_desc"; break;
        }
        if (nav?.Kind == NavKind.Untagged)
        {
            var a = _repo.QueryTracks(new TrackQuery { Keyword = q.Keyword, MissingSongTitle = true });
            var b = _repo.QueryTracks(new TrackQuery { Keyword = q.Keyword, MissingStreamer = true });
            list = a.Concat(b).DistinctBy(t => t.Id).OrderByDescending(t => t.AddedAt).ToList();
            ListSubtitle = "缺少歌名或主播的曲目，可以手动编辑，或者让 Claude Code 帮你整理";
        }
        else if (nav?.Kind == NavKind.Source && nav.Id is long sid)
        {
            using var c = _repo.Db.Open();
            var ids = Dapper.SqlMapper.Query<long>(c,
                "SELECT t.id FROM tracks t JOIN source_videos sv ON sv.bvid = t.bvid WHERE sv.source_id = @sid", new { sid }).ToList();
            list = _repo.GetTracks(ids).Where(t => !t.Hidden && Matches(t, q.Keyword)).OrderByDescending(t => t.AddedAt).ToList();
            var src = _repo.GetSource(sid);
            ListSubtitle = src?.LastSynced is long ls ? $"上次同步：{DateTimeOffset.FromUnixTimeSeconds(ls).LocalDateTime:yyyy-MM-dd HH:mm}" : null;
        }
        else if (nav?.Kind == NavKind.Recent)
        {
            q.OrderBy = "plays_desc";
            list = _repo.QueryTracks(q).Where(t => t.PlayCount > 0).ToList();
        }
        else if (nav?.Kind == NavKind.Suggestions)
        {
            var sugg = _repo.ListPendingSuggestions();
            list = _repo.GetTracks(sugg.Select(s => s.TrackId)).ToList();
            ListSubtitle = "Claude 提出的修改，右键可以采纳或拒绝";
        }
        else list = _repo.QueryTracks(q);

        Tracks.Clear();
        foreach (var t in list) Tracks.Add(t);
        ListSubtitle ??= $"{Tracks.Count} 首";
    }

    static bool Matches(TrackView t, string? kw) =>
        string.IsNullOrWhiteSpace(kw) ||
        $"{t.VideoTitle} {t.PartTitle} {t.SongTitle} {t.StreamerName} {t.UpName} {t.Tags}".Contains(kw, StringComparison.OrdinalIgnoreCase);

    void RefreshUpcoming()
    {
        Upcoming.Clear();
        if (Player.Queue.NeedsRecommender)
        {
            // 随机模式没有确定的「接下来」，展示最近播放
            foreach (var t in _repo.GetTracks(Player.Queue.History.Reverse().Distinct().Take(15))) Upcoming.Add(t);
            return;
        }
        foreach (var t in _repo.GetTracks(Player.Queue.Upcoming(30).Skip(1))) Upcoming.Add(t);
    }

    public string UpcomingTitle => Player.Queue.NeedsRecommender ? "最近播放" : "接下来";

    // ---------------- 播放 ----------------

    public async Task PlayFromListAsync(TrackView? start)
    {
        var ids = Tracks.Select(t => t.Id).ToList();
        if (ids.Count == 0) return;
        await Player.PlayListAsync(ids, start?.Id, SelectedNav?.Name ?? "全部曲目");
        RefreshUpcoming();
        OnPropertyChanged(nameof(UpcomingTitle));
    }

    [RelayCommand]
    async Task PlayAll() => await PlayFromListAsync(null);

    [RelayCommand]
    async Task ShuffleAll()
    {
        Player.PlayMode = Core.Playback.PlayMode.Shuffle;
        await PlayFromListAsync(null);
    }

    public void PlayNext(IEnumerable<TrackView> tracks)
    {
        Player.Queue.PlayNext(tracks.Select(t => t.Id));
    }

    public void Enqueue(IEnumerable<TrackView> tracks) => Player.Queue.Enqueue(tracks.Select(t => t.Id));

    public void NotifyModeChanged()
    {
        RefreshUpcoming();
        OnPropertyChanged(nameof(UpcomingTitle));
    }

    // ---------------- 编辑 ----------------

    public void ToggleFavorite(IReadOnlyList<TrackView> tracks)
    {
        if (tracks.Count == 0) return;
        var fav = !tracks.All(t => t.Favorite);
        _repo.UpdateTracks(tracks.Select(t => t.Id).ToList(), new TrackUpdate { Favorite = fav });
        AfterLibraryEdit();
    }

    public void Hide(IReadOnlyList<TrackView> tracks)
    {
        _repo.UpdateTracks(tracks.Select(t => t.Id).ToList(), new TrackUpdate { Hidden = true });
        foreach (var t in tracks) Player.Queue.Remove(t.Id);
        AfterLibraryEdit();
    }

    public void Update(IReadOnlyList<TrackView> tracks, TrackUpdate update)
    {
        _repo.UpdateTracks(tracks.Select(t => t.Id).ToList(), update);
        AfterLibraryEdit();
    }

    public void RemoveVideos(IReadOnlyList<TrackView> tracks)
    {
        foreach (var bvid in tracks.Select(t => t.Bvid).Distinct())
        {
            foreach (var t in _repo.QueryTracks(new TrackQuery { Keyword = bvid, IncludeHidden = true }).Where(x => x.Bvid == bvid))
                Player.Queue.Remove(t.Id);
            _repo.DeleteVideo(bvid);
        }
        AfterLibraryEdit();
    }

    public void AddToPlaylist(long playlistId, IReadOnlyList<TrackView> tracks)
    {
        var n = _repo.AddToPlaylist(playlistId, tracks.Select(t => t.Id));
        ImportStatus = $"已加入歌单 {n} 首";
        AfterLibraryEdit();
    }

    public void RemoveFromCurrentPlaylist(IReadOnlyList<TrackView> tracks)
    {
        if (SelectedNav is { Kind: NavKind.Playlist, Id: long pid })
        {
            _repo.RemoveFromPlaylist(pid, tracks.Select(t => t.Id));
            AfterLibraryEdit();
        }
    }

    public long CreatePlaylist(string name)
    {
        var id = _repo.CreatePlaylist(name);
        RebuildNav();
        return id;
    }

    public void DeletePlaylist(long id)
    {
        _repo.DeletePlaylist(id);
        SelectedNav = NavItems[0];
        RebuildNav();
    }

    public void ResolveSuggestionsFor(IReadOnlyList<TrackView> tracks, bool accept)
    {
        var ids = tracks.Select(t => t.Id).ToHashSet();
        var sugg = _repo.ListPendingSuggestions().Where(s => ids.Contains(s.TrackId)).Select(s => s.Id).ToList();
        _repo.ResolveSuggestions(sugg, accept);
        AfterLibraryEdit();
    }

    public IReadOnlyList<Suggestion> SuggestionsFor(long trackId) =>
        _repo.ListPendingSuggestions().Where(s => s.TrackId == trackId).ToList();

    public IReadOnlyList<Streamer> AllStreamers() => _repo.GetStreamers();

    /// <summary>曲库被修改后（本地编辑或 MCP）刷新界面。</summary>
    public void AfterLibraryEdit()
    {
        RebuildNav();
        Player.RefreshCurrent();
        RefreshUpcoming();
    }

    // ---------------- 导入 ----------------

    [RelayCommand]
    async Task Import()
    {
        if (IsImporting)
        {
            _importCts?.Cancel();
            return;
        }
        var inputs = LinkParser.SplitInputs(ImportText);
        if (inputs.Count == 0)
        {
            ImportStatus = "粘贴 BV 号、视频链接、收藏夹、合集或 UP 主空间链接";
            return;
        }
        IsImporting = true;
        _importCts = new CancellationTokenSource();
        var total = new ImportResult();
        var progress = new Progress<ImportProgress>(p =>
            ImportStatus = p.Total > 0 ? $"导入中 {p.Done}/{p.Total}：{p.Message}" : p.Message);
        try
        {
            foreach (var input in inputs)
            {
                var r = await Task.Run(() => _importer.ImportAsync(input, progress, _importCts.Token));
                total.VideosAdded += r.VideosAdded;
                total.TracksAdded += r.TracksAdded;
                total.AlreadyInLibrary += r.AlreadyInLibrary;
                total.Failed.AddRange(r.Failed);
                total.NewTrackIds.AddRange(r.NewTrackIds);
            }
            ImportStatus = total.Summary;
            ImportText = "";
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "已取消导入。" + total.Summary;
        }
        catch (Exception ex)
        {
            ImportStatus = "导入失败：" + ex.Message;
        }
        finally
        {
            IsImporting = false;
            RebuildNav();
        }
    }

    public async Task SyncSourceAsync(long sourceId)
    {
        if (_repo.GetSource(sourceId) is not { } src || IsImporting) return;
        IsImporting = true;
        _importCts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p => ImportStatus = $"同步中 {p.Done}/{p.Total}：{p.Message}");
        try
        {
            var r = await Task.Run(() => _importer.SyncSourceAsync(src, progress, _importCts.Token));
            ImportStatus = "同步完成：" + r.Summary;
        }
        catch (Exception ex)
        {
            ImportStatus = "同步失败：" + ex.Message;
        }
        finally
        {
            IsImporting = false;
            RebuildNav();
        }
    }

    public void DeleteSource(long sourceId)
    {
        _repo.DeleteSource(sourceId);
        SelectedNav = NavItems[0];
        RebuildNav();
    }

    // ---------------- 登录 ----------------

    public async Task RefreshLoginAsync()
    {
        try
        {
            var me = await Task.Run(() => _api.GetMyselfAsync());
            IsLoggedIn = me.IsLogin;
            UserName = me.IsLogin ? me.Name : null;
        }
        catch
        {
            IsLoggedIn = _api.HasLoginCookie;
        }
    }

    public async Task<IReadOnlyList<FavFolder>> GetMyFavFoldersAsync()
    {
        var me = await Task.Run(() => _api.GetMyselfAsync());
        if (!me.IsLogin) return [];
        return await Task.Run(() => _api.GetFavFoldersAsync(me.Mid));
    }

    public void Logout()
    {
        _api.Logout();
        IsLoggedIn = false;
        UserName = null;
    }
}
