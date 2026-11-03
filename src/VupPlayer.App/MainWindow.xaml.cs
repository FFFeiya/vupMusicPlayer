using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VupPlayer.Bilibili;
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
    string? _liveCoverUrl;

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
        ApplyFullChrome();
        StateChanged += (_, _) => UpdateMaximizedLayout();

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
                if (!_dragging)
                {
                    ProgressSlider.Value = Player.Position;
                    if (IsWidget) WidgetProgress.Value = Player.Position;
                }
                break;
            case nameof(PlayerController.StreamMode):
            case nameof(PlayerController.Current):
            case nameof(PlayerController.Live):
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
        // 看直播时 Current 是空的，但一样要显示画面 / 直播间封面
        var live = Player.Live is not null;
        var hasMedia = Player.Current is not null || live;
        var video = Player.StreamMode == StreamMode.Video && hasMedia;
        VideoHost.Visibility = video ? Visibility.Visible : Visibility.Hidden;
        CoverImage.Visibility = !video && Player.Current is not null ? Visibility.Visible : Visibility.Collapsed;
        LiveCoverImage.Visibility = !video && live ? Visibility.Visible : Visibility.Collapsed;
        NoTrackHint.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        // 直播封面直接赋值，不走绑定：Live 变化时绑定不会重新求值（Live 的属性本身不发通知）
        var cover = Player.Live?.Cover;
        if (_liveCoverUrl != cover)
        {
            _liveCoverUrl = cover;
            LiveCoverImage.Source = null;
            if (cover is { Length: > 0 }) _ = LoadLiveCoverAsync(cover);
        }
    }

    static readonly HttpClient LiveCoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 自己下载直播间封面再解码。不用 BitmapImage 的 UriSource：它异步下载，
    /// 失败时会把 Image.Source 悄悄清成 null，既看不到图也没有任何提示。
    /// </summary>
    async Task LoadLiveCoverAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(BiliApi.UserAgent);
            req.Headers.Referrer = new Uri(BiliApi.LiveReferer);
            var bytes = await LiveCoverHttp.SendAsync(req).ConfigureAwait(false) is { IsSuccessStatusCode: true } resp
                ? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
                : null;
            if (bytes is not { Length: > 0 }) { AppLog.Write("直播封面下载失败：" + url); return; }
            await Dispatcher.InvokeAsync(() =>
            {
                // 期间可能已经换了直播间
                if (_liveCoverUrl != url) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.DecodePixelWidth = 480;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                LiveCoverImage.Source = bmp;
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"直播封面加载失败 {url}：{ex.Message}");
        }
    }

    // ---------------- 自绘标题栏 ----------------

    /// <summary>
    /// 完整界面不用系统标题栏：深色顶栏兼作标题栏（空白处拖动、双击最大化），右侧自绘最小化/最大化/关闭。
    /// 保留 SingleBorderWindow + 1px 玻璃边，系统的阴影、Win11 圆角、贴靠和最小化动画都还在。
    /// </summary>
    void ApplyFullChrome()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 56,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        UpdateMaximizedLayout();
    }

    /// <summary>无系统标题栏的窗口最大化时会超出屏幕一圈边框的宽度，要把内容往里收。</summary>
    void UpdateMaximizedLayout()
    {
        var max = WindowState == WindowState.Maximized && !IsWidget;
        RootGrid.Margin = max ? MaximizedOverhang() : new Thickness(0);
        CaptionMaxButton.Content = max ? "" : "";
        CaptionMaxButton.ToolTip = max ? "还原" : "最大化";
    }

    Thickness MaximizedOverhang()
    {
        const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var x = (GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / dpi.DpiScaleX;
        var y = (GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / dpi.DpiScaleY;
        return new Thickness(x, y, x, y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    void CaptionMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void CaptionMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- 挂件布局 ----------------

    /// <summary>当前是否是挂件布局：只保留右侧的画面 + 列表，加紧凑控制条和小搜索框，窗口置顶、无边框。</summary>
    public bool IsWidget { get; private set; }

    Rect _fullBounds;
    WindowState _fullState = WindowState.Normal;
    bool _applyingLayout;

    public void ApplyLayout(bool widget, bool offscreen = false)
    {
        if (widget == IsWidget && IsLoaded) return;
        _applyingLayout = true;
        try
        {
            if (widget && !IsWidget)
            {
                _fullState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                // 窗口还没显示过时 Left/Top 是 NaN，回到完整布局时改为居中
                _fullBounds = double.IsNaN(Left) || double.IsNaN(Top) ? Rect.Empty : new Rect(Left, Top, Width, Height);
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            IsWidget = widget;
            _settings.WidgetLayout = widget;

            var full = widget ? Visibility.Collapsed : Visibility.Visible;
            var only = widget ? Visibility.Visible : Visibility.Collapsed;
            TopBar.Visibility = full;
            PlayerBar.Visibility = full;
            NavPanel.Visibility = full;
            ListPanel.Visibility = full;
            UpcomingHeader.Visibility = full;
            NowPlayingExtra.Visibility = full;
            WidgetTitleBar.Visibility = only;
            WidgetControls.Visibility = only;
            WidgetSearchBox.Visibility = only;

            TopRow.Height = new GridLength(widget ? 30 : 56);
            BottomRow.Height = new GridLength(widget ? 0 : 88);
            NavColumn.Width = new GridLength(widget ? 0 : 210);
            ListColumn.Width = widget ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            RightColumn.Width = widget ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
            RightPanel.Margin = widget ? new Thickness(0) : new Thickness(0, 12, 12, 10);
            RightPanel.CornerRadius = new CornerRadius(widget ? 0 : 10);
            RightGrid.Margin = widget ? new Thickness(10, 6, 10, 8) : new Thickness(14);
            NowPlayingInfo.Margin = widget ? new Thickness(2, 8, 2, 0) : new Thickness(2, 12, 2, 0);
            NowTitle.FontSize = widget ? 14 : 17;

            if (widget)
            {
                // 无边框，但保留拖动标题栏和四边缩放
                WindowStyle = WindowStyle.None;
                System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 30,
                    ResizeBorderThickness = new Thickness(6),
                    GlassFrameThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    UseAeroCaptionButtons = false,
                });
                Topmost = true;
                MinWidth = 300;
                MinHeight = 420;
                Width = Math.Max(MinWidth, _settings.WidgetWidth);
                Height = Math.Max(MinHeight, _settings.WidgetHeight);
                PlaceWidget(offscreen);
            }
            else
            {
                ApplyFullChrome();
                Topmost = false;
                MinWidth = 980;
                MinHeight = 600;
                if (!_fullBounds.IsEmpty && _fullBounds.Width > 0)
                {
                    Left = _fullBounds.Left;
                    Top = _fullBounds.Top;
                    Width = Math.Max(MinWidth, _fullBounds.Width);
                    Height = Math.Max(MinHeight, _fullBounds.Height);
                }
                else
                {
                    Width = Math.Max(MinWidth, _settings.WindowWidth);
                    Height = Math.Max(MinHeight, _settings.WindowHeight);
                    var wa = SystemParameters.WorkArea;
                    Left = wa.Left + (wa.Width - Width) / 2;
                    Top = wa.Top + (wa.Height - Height) / 2;
                }
                WindowState = _fullState;
                MediaArea.Height = 198;
            }
        }
        finally
        {
            _applyingLayout = false;
        }
        UpdateWidgetMediaHeight();
    }

    void PlaceWidget(bool offscreen)
    {
        if (offscreen)
        {
            Left = -4000;
            Top = 0;
            return;
        }
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_settings.WidgetLeft is double l && _settings.WidgetTop is double t
            && vs.Contains(new Point(l + 40, t + 20)) && vs.Contains(new Point(l + Width - 40, t + 100)))
        {
            Left = l;
            Top = t;
            return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = Math.Max(wa.Top + 16, wa.Bottom - Height - 16);
    }

    /// <summary>挂件里的画面按 16:9 跟随宽度，但最多占窗口高度的一半，给列表留空间。</summary>
    void UpdateWidgetMediaHeight()
    {
        if (!IsWidget || RightGrid.ActualWidth <= 0) return;
        var h = RightGrid.ActualWidth * 9 / 16;
        MediaArea.Height = Math.Clamp(h, 120, Math.Max(120, ActualHeight * 0.5));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateWidgetMediaHeight();
        RememberWidgetBounds();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RememberWidgetBounds();
    }

    void RememberWidgetBounds()
    {
        if (!IsWidget || _applyingLayout || !IsVisible || Left < -3000) return;
        _settings.WidgetLeft = Left;
        _settings.WidgetTop = Top;
        _settings.WidgetWidth = Width;
        _settings.WidgetHeight = Height;
    }

    void Widget_Click(object sender, RoutedEventArgs e) => _app.SetWidgetLayout(true);
    void WidgetExit_Click(object sender, RoutedEventArgs e) => _app.SetWidgetLayout(false);

    void WidgetHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _app.ShowTrayTip();
    }

    void RightPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 挂件模式下在封面上滚轮调音量（画面模式时 mpv 子窗口自己吃掉滚轮，不会到这里）
        if (!IsWidget || !MediaArea.IsMouseOver) return;
        Player.Volume = Math.Clamp(Player.Volume + (e.Delta > 0 ? 5 : -5), 0, 100);
        e.Handled = true;
    }

    void WidgetSearch_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _vm.WidgetResults.Count > 0:
                _ = Player.PlayTrackAsync(_vm.WidgetResults[0].Id);
                e.Handled = true;
                break;
            case Key.Down when _vm.WidgetResults.Count > 0:
                WidgetResultList.SelectedIndex = 0;
                (WidgetResultList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape when _vm.WidgetSearch.Length > 0:
                _vm.WidgetSearch = "";
                WidgetSearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    async void WidgetResult_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackView t)
            await Player.PlayTrackAsync(t.Id);
    }

    async void WidgetResult_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WidgetResultList.SelectedItem is TrackView t)
        {
            await Player.PlayTrackAsync(t.Id);
            e.Handled = true;
        }
    }

    void WidgetResult_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = WidgetResultList.ContextMenu!;
        menu.Items.Clear();
        if (WidgetResultList.SelectedItem is not TrackView t) { e.Handled = true; return; }
        Add(menu, "播放", async () => await Player.PlayTrackAsync(t.Id));
        Add(menu, "下一首播放", () => _vm.PlayNext([t]));
        Add(menu, "加入播放队列", () => _vm.Enqueue([t]));
        Add(menu, t.Favorite ? "取消喜欢" : "喜欢", () => _vm.ToggleFavorite([t]));
    }

    // ---------------- 播放控制 ----------------

    void Play_Click(object sender, RoutedEventArgs e) => Player.TogglePause();
    async void Next_Click(object sender, RoutedEventArgs e) => await Player.NextAsync();
    async void Prev_Click(object sender, RoutedEventArgs e) => await Player.PreviousAsync();
    async void Stream_Click(object sender, RoutedEventArgs e) => await _app.ToggleStreamModeAsync();
    void Mode_Click(object sender, RoutedEventArgs e) => Player.CyclePlayMode();
    void Mini_Click(object sender, RoutedEventArgs e) => _app.EnterMiniMode();

    void Progress_MouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;

    void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // IsMoveToPointEnabled 会在 MouseDown 后先更新 Value，这里统一在松开时跳转
        var slider = (Slider)sender;
        Dispatcher.BeginInvoke(() =>
        {
            Player.Seek(slider.Value);
            _dragging = false;
        });
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.M)
        {
            _app.EnterMiniMode();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.G)
        {
            _app.SetWidgetLayout(!IsWidget);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.F && IsWidget)
        {
            WidgetSearchBox.Focus();
            WidgetSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Escape && IsWidget)
        {
            _app.SetWidgetLayout(false);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Space:
                Player.TogglePause();
                e.Handled = true;
                break;
            case Key.V when !ctrl:
                await _app.ToggleStreamModeAsync();
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

        // 「B站新发现」里的条目还没导入，菜单不一样
        if (sel.All(t => t.IsDiscovery))
        {
            Add(menu, "导入并播放", async () => await _vm.ImportAndPlayDiscoveryAsync(sel[0]));
            Add(menu, sel.Count > 1 ? $"导入到曲库（{sel.Count} 个）" : "导入到曲库", async () =>
            {
                foreach (var t in sel) await _vm.ImportAndPlayDiscoveryAsync(t, play: false);
            });
            Add(menu, "不感兴趣", () => _vm.RejectDiscovery(sel));
            menu.Items.Add(new Separator());
            Add(menu, "在 B 站打开", () => Process.Start(new ProcessStartInfo($"https://www.bilibili.com/video/{sel[0].DiscoveryBvid}") { UseShellExecute = true }));
            return;
        }

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

        var line = new MenuItem { Header = "播放线路" };
        foreach (var (host, name) in new (string?, string)[]
                 {
                     (null, "自动（分到海外节点时改走国内镜像，卡住自动换）"),
                     ("upos-sz-mirrorali.bilivideo.com", "阿里云"),
                     ("upos-sz-mirror08c.bilivideo.com", "08c"),
                     ("upos-sz-mirrorhw.bilivideo.com", "华为云"),
                     ("upos-sz-mirrorcos.bilivideo.com", "腾讯云"),
                     ("upos-sz-mirrorbd.bilivideo.com", "百度云"),
                 })
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = _settings.PreferredMirror == host };
            mi.Click += (_, _) => { _settings.PreferredMirror = host; _app.StreamPrefs.PreferredMirror = host; _app.Resolver.ClearAll(); };
            line.Items.Add(mi);
        }
        menu.Items.Add(line);
        menu.Items.Add(BuildRecommendMenu());

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

    MenuItem BuildRecommendMenu()
    {
        var root = new MenuItem { Header = "智能推荐" };

        void Radio<T>(MenuItem parent, string header, T value, T current, Action<T> apply)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            mi.Click += (_, _) => { apply(value); _app.ApplyRecommendSettings(); };
            parent.Items.Add(mi);
        }

        var scope = new MenuItem { Header = "推荐范围" };
        Radio(scope, "整个曲库", true, _settings.RecommendFromLibrary, v => _settings.RecommendFromLibrary = v);
        Radio(scope, "只在当前列表里", false, _settings.RecommendFromLibrary, v => _settings.RecommendFromLibrary = v);
        root.Items.Add(scope);

        var taste = new MenuItem { Header = "口味" };
        Radio(taste, "保守：多放爱听的", 0.6, _settings.RecommendTemperature, v => _settings.RecommendTemperature = v);
        Radio(taste, "均衡", 1.0, _settings.RecommendTemperature, v => _settings.RecommendTemperature = v);
        Radio(taste, "冒险：多翻没怎么听的", 1.6, _settings.RecommendTemperature, v => _settings.RecommendTemperature = v);
        root.Items.Add(taste);

        var disc = new MenuItem { Header = "B 站发现新切片" };
        Radio(disc, "关闭", 0.0, _settings.DiscoveryRate, v => _settings.DiscoveryRate = v);
        Radio(disc, "偶尔（约 1/10）", 0.1, _settings.DiscoveryRate, v => _settings.DiscoveryRate = v);
        Radio(disc, "经常（约 1/4）", 0.25, _settings.DiscoveryRate, v => _settings.DiscoveryRate = v);
        root.Items.Add(disc);

        var algo = new MenuItem { Header = "推荐算法" };
        foreach (var (name, display) in Core.Recommend.RecommenderRegistry.Available)
            Radio(algo, display, name, _settings.Recommender, v => _settings.Recommender = v);
        root.Items.Add(algo);

        root.Items.Add(new Separator());
        Add(root, "立即刷新 B 站发现池", async () =>
        {
            _vm.ImportStatus = "正在按常听的主播去 B 站找新切片…";
            var r = await _app.RefreshDiscoveryAsync(force: true);
            _vm.ImportStatus = r is null ? "发现池正在刷新中"
                : r.Error is not null && r.Added == 0 ? "刷新发现池失败：" + r.Error
                : $"发现池新增 {r.Added} 个候选（搜了 {string.Join("、", r.Streamers)}）";
        });
        Add(root, "看看接下来可能推荐什么…", () =>
        {
            var top = _vm.Player.PreviewScores().Take(12).ToList();
            var text = top.Count == 0 ? "当前推荐算法不支持预览，或者曲库是空的。"
                : string.Join("\n", top.Select((s, i) => $"{i + 1}. {s.Title}    分数 {s.Score:0.00}\n     {s.Reason}"));
            MessageBox.Show(this, text + "\n\n（实际播放时按分数加权随机抽取，不一定按这个顺序）", "推荐预览");
        });
        return root;
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
        if (WindowState == WindowState.Normal && !IsWidget)
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
