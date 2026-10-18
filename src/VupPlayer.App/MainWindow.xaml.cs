using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VupPlayer.App.Services;
using VupPlayer.App.ViewModels;
using VupPlayer.App.Views;
using VupPlayer.Core;
using VupPlayer.Core.Data;
using VupPlayer.Core.Streams;
using VupPlayer.Playback;

namespace VupPlayer.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly AppSettings _settings;
    readonly App _app;
    bool _dragging;

    public bool AllowClose { get; set; }

    PlayerController Player => _vm.Player;

    public MainWindow(MainViewModel vm, AppSettings settings, App app)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _app = app;
        DataContext = vm;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        VideoHost.HandleCreated += OnVideoHandle;
        Player.PropertyChanged += Player_PropertyChanged;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---------------- 播放器初始化 ----------------

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.Initialize();
        UpdateMediaArea();
    }

    void OnVideoHandle(nint handle)
    {
        if (Player.IsAttached) return;
        try
        {
            var mpv = new MpvPlayer(handle, "warn");
            mpv.Log += (level, msg) => AppLog.Write($"mpv {level}: {msg}");
            Player.Attach(mpv);
        }
        catch (Exception ex)
        {
            AppLog.Write("mpv 初始化失败：" + ex);
            Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                "播放内核初始化失败：" + ex.Message + "\n\n请先运行 scripts\\fetch-deps.ps1 下载 libmpv-2.dll。",
                "VUP 切片播放器", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerController.Position):
                if (!_dragging) ProgressSlider.Value = Player.Position;
                break;
            case nameof(PlayerController.StreamMode):
            case nameof(PlayerController.Current):
                UpdateMediaArea();
                break;
            case nameof(PlayerController.PlayMode):
                _vm.NotifyModeChanged();
                break;
        }
    }

    /// <summary>画面模式显示 mpv 窗口，纯音频模式显示封面。HwndHost 总是盖在 WPF 内容之上，所以只能切换可见性。</summary>
    void UpdateMediaArea()
    {
        var hasTrack = Player.Current is not null;
        var video = Player.StreamMode == StreamMode.Video && hasTrack;
        VideoHost.Visibility = video ? Visibility.Visible : Visibility.Hidden;
        CoverImage.Visibility = !video && hasTrack ? Visibility.Visible : Visibility.Collapsed;
        NoTrackHint.Visibility = hasTrack ? Visibility.Collapsed : Visibility.Visible;
        // 画面模式下把画面区域放大一些
        MediaArea.Height = video ? 198 : 198;
    }

    // ---------------- 播放控制 ----------------

    void Play_Click(object sender, RoutedEventArgs e) => Player.TogglePause();
    async void Next_Click(object sender, RoutedEventArgs e) => await Player.NextAsync();
    async void Prev_Click(object sender, RoutedEventArgs e) => await Player.PreviousAsync();
    async void Stream_Click(object sender, RoutedEventArgs e) => await Player.ToggleStreamModeAsync();
    void Mode_Click(object sender, RoutedEventArgs e) => Player.CyclePlayMode();

    void Progress_MouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;

    void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // IsMoveToPointEnabled 会在 MouseDown 后先更新 Value，这里统一在松开时跳转
        Dispatcher.BeginInvoke(() =>
        {
            Player.Seek(ProgressSlider.Value);
            _dragging = false;
        });
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space:
                Player.TogglePause();
                e.Handled = true;
                break;
            case Key.V when !ctrl:
                await Player.ToggleStreamModeAsync();
                e.Handled = true;
                break;
            case Key.Right when ctrl:
                await Player.NextAsync();
                e.Handled = true;
                break;
            case Key.Left when ctrl:
                await Player.PreviousAsync();
                e.Handled = true;
                break;
            case Key.Right:
                Player.Seek(Player.Position + 5);
                e.Handled = true;
                break;
            case Key.Left:
                Player.Seek(Math.Max(0, Player.Position - 5));
                e.Handled = true;
                break;
            case Key.Up when ctrl:
                Player.Volume = Math.Min(100, Player.Volume + 5);
                e.Handled = true;
                break;
            case Key.Down when ctrl:
                Player.Volume = Math.Max(0, Player.Volume - 5);
                e.Handled = true;
                break;
        }
    }

    // ---------------- 导入 ----------------

    void ImportBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.ImportCommand.CanExecute(null))
        {
            _vm.ImportCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------------- 曲目列表 ----------------

    List<TrackView> SelectedTracks() => TrackList.SelectedItems.Cast<TrackView>().ToList();

    /// <summary>标题列占满剩余宽度，避免出现横向滚动条。</summary>
    void TrackList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TrackList.View is not GridView gv) return;
        var others = gv.Columns.Where(c => c != TitleColumn).Sum(c => c.ActualWidth > 0 ? c.ActualWidth : c.Width);
        TitleColumn.Width = Math.Max(160, TrackList.ActualWidth - others - 24);
    }

    async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackView t)
            await _vm.PlayFromListAsync(t);
    }

    async void TrackList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TrackList.SelectedItem is TrackView t)
        {
            await _vm.PlayFromListAsync(t);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && SelectedTracks() is { Count: > 0 } sel)
        {
            if (_vm.SelectedNav?.Kind == NavKind.Playlist) _vm.RemoveFromCurrentPlaylist(sel);
            e.Handled = true;
        }
    }

    async void Upcoming_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackView t)
            await Player.PlayTrackAsync(t.Id);
    }

    void TrackList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = SelectedTracks();
        var menu = TrackList.ContextMenu!;
        menu.Items.Clear();
        if (sel.Count == 0) { e.Handled = true; return; }

        Add(menu, "播放", async () => await _vm.PlayFromListAsync(sel[0]));
        Add(menu, "下一首播放", () => _vm.PlayNext(sel));
        Add(menu, "加入播放队列", () => _vm.Enqueue(sel));
        menu.Items.Add(new Separator());

        var pl = new MenuItem { Header = "加入歌单" };
        foreach (var p in _vm.Playlists)
        {
            var id = p.Id;
            Add(pl, p.Name, () => _vm.AddToPlaylist(id, sel));
        }
        if (_vm.Playlists.Count > 0) pl.Items.Add(new Separator());
        Add(pl, "新建歌单…", () =>
        {
            var name = InputDialog.Ask(this, "新建歌单", "歌单名称");
            if (!string.IsNullOrWhiteSpace(name)) _vm.AddToPlaylist(_vm.CreatePlaylist(name), sel);
        });
        menu.Items.Add(pl);
        if (_vm.SelectedNav?.Kind == NavKind.Playlist)
            Add(menu, "从此歌单移除", () => _vm.RemoveFromCurrentPlaylist(sel));

        Add(menu, sel.All(t => t.Favorite) ? "取消喜欢" : "喜欢", () => _vm.ToggleFavorite(sel));
        Add(menu, sel.Count > 1 ? $"编辑信息（{sel.Count} 首）…" : "编辑信息…", () =>
        {
            var update = EditTrackDialog.Show(this, sel, _vm.AllStreamers());
            if (update is not null) _vm.Update(sel, update);
        });

        if (_vm.SelectedNav?.Kind == NavKind.Suggestions)
        {
            menu.Items.Add(new Separator());
            var detail = string.Join("\n", sel.SelectMany(t => _vm.SuggestionsFor(t.Id)).Take(8)
                .Select(s => $"{FieldName(s.Field)} → {s.Value}"));
            menu.Items.Add(new MenuItem { Header = detail, IsEnabled = false });
            Add(menu, "采纳这些修改", () => _vm.ResolveSuggestionsFor(sel, accept: true));
            Add(menu, "拒绝这些修改", () => _vm.ResolveSuggestionsFor(sel, accept: false));
        }

        menu.Items.Add(new Separator());
        Add(menu, "在 B 站打开", () => Process.Start(new ProcessStartInfo(sel[0].Url) { UseShellExecute = true }));
        Add(menu, "复制链接", () => Clipboard.SetText(string.Join("\n", sel.Select(t => t.Url).Distinct())));
        Add(menu, "不再推荐（隐藏）", () => _vm.Hide(sel));
        Add(menu, "从曲库删除视频…", () =>
        {
            var n = sel.Select(t => t.Bvid).Distinct().Count();
            if (MessageBox.Show(this, $"从曲库删除 {n} 个视频及其播放记录？", "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                _vm.RemoveVideos(sel);
        });
    }

    static string FieldName(string f) => f switch { "song_title" => "歌名", "streamer" => "主播", "tag" => "标签", _ => f };

    // ---------------- 导航菜单 ----------------

    void NavList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as NavItem;
        var menu = NavList.ContextMenu!;
        menu.Items.Clear();
        Add(menu, "新建歌单…", () =>
        {
            var name = InputDialog.Ask(this, "新建歌单", "歌单名称");
            if (!string.IsNullOrWhiteSpace(name)) _vm.CreatePlaylist(name);
        });
        switch (item)
        {
            case { Kind: NavKind.Playlist, Id: long pid }:
                Add(menu, "重命名歌单…", () =>
                {
                    var name = InputDialog.Ask(this, "重命名歌单", "歌单名称", item.Name);
                    if (!string.IsNullOrWhiteSpace(name)) { _app.Repo.RenamePlaylist(pid, name); _vm.RebuildNav(); }
                });
                Add(menu, "删除歌单", () =>
                {
                    if (MessageBox.Show(this, $"删除歌单「{item.Name}」？曲目本身不会被删除。", "确认", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                        _vm.DeletePlaylist(pid);
                });
                break;
            case { Kind: NavKind.Source, Id: long sid }:
                Add(menu, "立即同步", async () => await _vm.SyncSourceAsync(sid));
                Add(menu, "移除订阅（保留曲目）", () => _vm.DeleteSource(sid));
                break;
            case { Kind: NavKind.Streamer, Id: long stid }:
                Add(menu, "设置别名…", () =>
                {
                    var s = _vm.AllStreamers().FirstOrDefault(x => x.Id == stid);
                    var current = s?.Aliases is null ? "" : string.Join("，", System.Text.Json.JsonSerializer.Deserialize<string[]>(s.Aliases) ?? []);
                    var text = InputDialog.Ask(this, $"「{item.Name}」的别名", "多个别名用逗号分隔，导入时标题里出现别名也会识别为这个主播", current);
                    if (text is not null)
                    {
                        _app.Repo.SetStreamerAliases(stid, text.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries));
                        _vm.RebuildNav();
                    }
                });
                break;
        }
    }

    // ---------------- 账号与设置 ----------------

    async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsLoggedIn)
        {
            if (LoginWindow.Show(this, _app.Api)) await _vm.RefreshLoginAsync();
            return;
        }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var fav = new MenuItem { Header = "导入我的收藏夹" };
        menu.Items.Add(fav);
        try
        {
            foreach (var f in await _vm.GetMyFavFoldersAsync())
            {
                var url = $"https://space.bilibili.com/0/favlist?fid={f.Id}";
                Add(fav, $"{f.Title}（{f.MediaCount}）", () =>
                {
                    _vm.ImportText = url;
                    _vm.ImportCommand.Execute(null);
                });
            }
        }
        catch (Exception ex)
        {
            fav.Items.Add(new MenuItem { Header = "获取失败：" + ex.Message, IsEnabled = false });
        }
        if (fav.Items.Count == 0) fav.Items.Add(new MenuItem { Header = "没有收藏夹", IsEnabled = false });
        menu.Items.Add(new Separator());
        Add(menu, "退出登录", () => _vm.Logout());
        menu.IsOpen = true;
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };

        var quality = new MenuItem { Header = "画质上限" };
        foreach (var (h, name) in new[] { (480, "480P"), (720, "720P"), (1080, "1080P"), (4320, "最高") })
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = _settings.MaxHeight == h };
            mi.Click += (_, _) => { _settings.MaxHeight = h; _app.StreamPrefs.MaxHeight = h; _app.Resolver.ClearAll(); };
            quality.Items.Add(mi);
        }
        menu.Items.Add(quality);

        var ytdlp = new MenuItem { Header = "优先用 yt-dlp 取流（较慢，接口失效时使用）", IsCheckable = true, IsChecked = _settings.PreferYtDlp };
        ytdlp.Click += (_, _) => { _settings.PreferYtDlp = ytdlp.IsChecked; _app.ApplyResolverOrder(); };
        menu.Items.Add(ytdlp);

        var tray = new MenuItem { Header = "关闭窗口时最小化到托盘", IsCheckable = true, IsChecked = _settings.CloseToTray };
        tray.Click += (_, _) => _settings.CloseToTray = tray.IsChecked;
        menu.Items.Add(tray);

        menu.Items.Add(new Separator());
        Add(menu, "复制 Claude Code 注册命令", () =>
        {
            Clipboard.SetText(App.McpRegisterCommand());
            _vm.ImportStatus = "已复制，在终端里粘贴运行即可注册曲库 MCP";
        });
        Add(menu, "备份曲库", () => _vm.ImportStatus = "已备份到 " + _app.Repo.Db.Backup());
        Add(menu, "打开数据目录", () => Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true }));
        menu.Items.Add(new Separator());
        Add(menu, "退出", () => _app.Quit());
        menu.IsOpen = true;
    }

    static void Add(ItemsControl parent, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        parent.Items.Add(mi);
    }

    // ---------------- 关闭 ----------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _app.ShowTrayTip();
            return;
        }
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        base.OnClosing(e);
        if (!AllowClose) _app.Quit();
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
