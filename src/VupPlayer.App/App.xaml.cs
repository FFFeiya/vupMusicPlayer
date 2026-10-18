using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VupPlayer.App.Services;
using VupPlayer.App.ViewModels;
using VupPlayer.Bilibili;
using VupPlayer.Core;
using VupPlayer.Core.Control;
using VupPlayer.Core.Data;
using VupPlayer.Core.Playback;
using VupPlayer.Core.Streams;
using Forms = System.Windows.Forms;

namespace VupPlayer.App;

public partial class App : Application
{
    Mutex? _singleInstance;
    ControlServer? _control;
    Forms.NotifyIcon? _tray;
    DispatcherTimer? _saveTimer;
    DispatcherTimer? _discoveryTimer;
    bool _discoveryRunning;
    MediaControls? _media;
    bool _trayTipShown;

    /// <summary>开发测试模式：窗口开在屏幕外、开放调试管道命令。</summary>
    static readonly bool DevMode = Environment.GetEnvironmentVariable("VUPPLAYER_OFFSCREEN") == "1";

    public LibraryRepository Repo { get; private set; } = null!;
    public BiliApi Api { get; private set; } = null!;
    public YtDlp YtDlp { get; private set; } = null!;
    public StreamPreferences StreamPrefs { get; } = new();
    public CachingStreamResolver Resolver { get; private set; } = null!;
    public LibraryImporter Importer { get; private set; } = null!;
    public DiscoveryService Discovery { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public PlayerController Player { get; private set; } = null!;
    public MainViewModel ViewModel { get; private set; } = null!;
    public MainWindow Shell { get; private set; } = null!;
    public MiniWindow Mini { get; private set; } = null!;
    public bool IsMiniMode { get; private set; }
    public bool IsQuitting { get; private set; }

    /// <summary>进入迷你模式前是画面模式：迷你窗口不显示画面，先切纯音频，退出时恢复。</summary>
    bool _restoreVideoAfterMini;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已经在运行时，把参数交给它，然后退出
        _singleInstance = new Mutex(true, AppPaths.InstanceMutexName, out var first);
        if (!first)
        {
            var args = new JsonObject();
            if (e.Args.Length > 0) args["input"] = string.Join(" ", e.Args);
            try { ControlClient.SendAsync("show", args).GetAwaiter().GetResult(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Write("未处理异常：" + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { AppLog.Write("后台任务异常：" + ex.Exception); ex.SetObserved(); };

        Settings = AppSettings.Load();
        StreamPrefs.MaxHeight = Settings.MaxHeight;
        StreamPrefs.PreferredMirror = Settings.PreferredMirror;

        var db = new Database();
        db.Migrate();
        Repo = new LibraryRepository(db);
        DailyBackup(db);

        Api = new BiliApi(CookieStore.Load());
        Api.CookiesChanged += () => { try { CookieStore.Save(Api.Cookies); } catch (Exception ex) { AppLog.Write("保存 cookie 失败：" + ex.Message); } };
        YtDlp = new YtDlp(YtDlp.Locate(), () => Api.Cookies);
        Resolver = new CachingStreamResolver([]);
        ApplyResolverOrder();

        Player = new PlayerController(Repo, Resolver, Settings, Dispatcher);
        Importer = new LibraryImporter(Api, YtDlp, Repo);
        Discovery = new DiscoveryService(Api, Repo);
        Player.DiscoveryImporter = ImportDiscoveryAsync;
        ViewModel = new MainViewModel(Repo, Api, Importer, Player);
        ViewModel.DiscoveryRefresher = () => RefreshDiscoveryAsync(force: true);
        Shell = new MainWindow(ViewModel, Settings, this);
        MainWindow = Shell;
        if (DevMode)
        {
            // 开发测试用：窗口开在屏幕外、不抢焦点
            Shell.WindowStartupLocation = WindowStartupLocation.Manual;
            Shell.Left = -4000;
            Shell.Top = 0;
            Shell.ShowActivated = false;
            Shell.ShowInTaskbar = false;
            _trayTipShown = true;
        }
        if (Settings.WidgetLayout) Shell.ApplyLayout(true, offscreen: DevMode);
        Shell.Show();
        Mini = new MiniWindow(ViewModel, Settings, this);

        // 上次停在迷你模式：主窗口必须先显示一次（mpv 依附在它的子窗口上），再切过去
        if (Settings.MiniMode)
            Dispatcher.BeginInvoke(() => EnterMiniMode(), DispatcherPriority.ApplicationIdle);

        SetupTray();
        try { _media = new MediaControls(Player, Dispatcher); }
        catch (Exception ex) { AppLog.Write("系统媒体控件不可用：" + ex.Message); }
        _control = new ControlServer(HandleControlAsync);
        _control.Start();

        // 定期保存设置
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _saveTimer.Tick += (_, _) => Settings.Save();
        _saveTimer.Start();

        // B 站发现池：启动 20 秒后检查一次，之后每 30 分钟检查是否需要刷新
        _discoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _discoveryTimer.Tick += async (_, _) =>
        {
            _discoveryTimer.Interval = TimeSpan.FromMinutes(30);
            if (Settings.DiscoveryRate > 0) await RefreshDiscoveryAsync(force: false);
        };
        _discoveryTimer.Start();

        if (e.Args.Length > 0)
        {
            ViewModel.ImportText = string.Join(" ", e.Args);
            ViewModel.ImportCommand.Execute(null);
        }
    }

    // ---------------- B 站发现 ----------------

    /// <summary>推荐器挑中发现池里的视频时调用：导入曲库、挂到「B站发现」订阅源，返回要播放的曲目。</summary>
    async Task<long?> ImportDiscoveryAsync(string bvid)
    {
        var r = await Task.Run(() => Importer.ImportAsync(bvid));
        var sourceId = Repo.UpsertSource("discovery", "auto", "B站发现");
        foreach (var b in r.Bvids) Repo.LinkSourceVideo(sourceId, b);
        Repo.SetDiscoveryState(bvid, "imported");
        ViewModel.AfterLibraryEdit();
        return Repo.GetTracksByBvid(bvid).FirstOrDefault()?.Id;
    }

    public async Task<DiscoveryRefreshResult?> RefreshDiscoveryAsync(bool force)
    {
        if (_discoveryRunning) return null;
        _discoveryRunning = true;
        try
        {
            var r = await Task.Run(() => Discovery.RefreshAsync(force));
            if (r.Queries > 0 || r.Error is not null)
                AppLog.Write($"发现池刷新：新增 {r.Added}，搜索 {r.Queries} 次，主播 {string.Join("、", r.Streamers)}{(r.Error is null ? "" : "，错误：" + r.Error)}");
            return r;
        }
        catch (Exception ex)
        {
            AppLog.Write("发现池刷新失败：" + ex.Message);
            return new DiscoveryRefreshResult(0, 0, [], ex.Message);
        }
        finally
        {
            _discoveryRunning = false;
        }
    }

    public void ApplyRecommendSettings()
    {
        Player.RecommendOptions.Temperature = Settings.RecommendTemperature;
        Player.RecommendOptions.DiscoveryRate = Settings.DiscoveryRate;
        Player.RecommendFromLibrary = Settings.RecommendFromLibrary;
        Player.SetRecommender(Settings.Recommender);
    }

    public void ApplyResolverOrder()
    {
        IStreamResolver api = new BiliApiResolver(Api, StreamPrefs);
        IStreamResolver ytdlp = new YtDlpResolver(YtDlp, StreamPrefs);
        Resolver.SetResolvers(Settings.PreferYtDlp ? [ytdlp, api] : [api, ytdlp]);
    }

    static void DailyBackup(Database db)
    {
        try
        {
            if (!File.Exists(db.Path) || new FileInfo(db.Path).Length < 8192) return;
            Directory.CreateDirectory(AppPaths.Backups);
            var latest = new DirectoryInfo(AppPaths.Backups).GetFiles("library-*.db").OrderByDescending(f => f.Name).FirstOrDefault();
            if (latest is null || DateTime.Now - latest.LastWriteTime > TimeSpan.FromDays(1)) db.Backup();
        }
        catch (Exception ex)
        {
            AppLog.Write("自动备份失败：" + ex.Message);
        }
    }

    /// <summary>MCP 可执行文件位置：发布目录里与主程序同目录；开发时在 Mcp 项目的输出目录。</summary>
    public static string McpExePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "VupPlayer.Mcp.exe");
        if (File.Exists(local)) return local;
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VupPlayer.Mcp", "bin", "Debug", "net9.0", "VupPlayer.Mcp.exe"));
        return File.Exists(dev) ? dev : local;
    }

    // ---------------- 迷你模式 ----------------

    public async void EnterMiniMode()
    {
        if (IsMiniMode) { ShowMini(); return; }
        IsMiniMode = true;
        Settings.MiniMode = true;
        if (Shell.WindowState == WindowState.Normal && !Shell.IsWidget)
        {
            Settings.WindowWidth = Shell.Width;
            Settings.WindowHeight = Shell.Height;
        }
        Shell.Hide();
        ShowMini();
        if (Player.StreamMode == StreamMode.Video)
        {
            _restoreVideoAfterMini = true;
            await Player.SetStreamModeAsync(StreamMode.Audio);
        }
    }

    public async void ExitMiniMode()
    {
        if (!IsMiniMode) { Shell.BringToFront(); return; }
        IsMiniMode = false;
        Settings.MiniMode = false;
        Mini.Hide();
        Shell.BringToFront();
        if (_restoreVideoAfterMini)
        {
            _restoreVideoAfterMini = false;
            await Player.SetStreamModeAsync(StreamMode.Video);
        }
    }

    /// <summary>切换画面 / 纯音频。迷你模式下要看画面，就先回到主界面。</summary>
    public async Task SetStreamModeAsync(StreamMode mode)
    {
        if (mode == StreamMode.Video && IsMiniMode)
        {
            _restoreVideoAfterMini = false;
            ExitMiniMode();
        }
        await Player.SetStreamModeAsync(mode);
    }

    public Task ToggleStreamModeAsync() =>
        SetStreamModeAsync(Player.StreamMode == StreamMode.Video ? StreamMode.Audio : StreamMode.Video);

    /// <summary>主窗口在完整布局和挂件布局之间切换。处于迷你条时先回到主窗口。</summary>
    public void SetWidgetLayout(bool widget)
    {
        if (IsMiniMode)
        {
            // 挂件能显示画面，所以进迷你条前的画面模式照常恢复
            ExitMiniMode();
        }
        Shell.ApplyLayout(widget, offscreen: DevMode);
        Shell.BringToFront();
    }

    public void ToggleMiniMode()
    {
        if (IsMiniMode) ExitMiniMode(); else EnterMiniMode();
    }

    void ShowMini()
    {
        if (DevMode)
        {
            Mini.Left = -4000;
            Mini.Top = 900;
            Mini.ShowActivated = false;
        }
        else if (!Mini.IsVisible)
        {
            Mini.PlaceWindow();
        }
        Mini.Show();
        if (!DevMode) Mini.Activate();
    }

    public static string McpRegisterCommand() => $"claude mcp add vupplayer --scope user -- \"{McpExePath()}\"";

    // ---------------- 托盘 ----------------

    void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "VUP 切片播放器",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => SetWidgetLayout(false));
        menu.Items.Add("挂件模式（画面 + 列表）", null, (_, _) => SetWidgetLayout(true));
        menu.Items.Add("迷你条", null, (_, _) => EnterMiniMode());
        menu.Items.Add("播放 / 暂停", null, (_, _) => Player.TogglePause());
        menu.Items.Add("下一首", null, async (_, _) => await Player.NextAsync());
        menu.Items.Add("上一首", null, async (_, _) => await Player.PreviousAsync());
        menu.Items.Add("切换画面 / 纯音频", null, async (_, _) => await ToggleStreamModeAsync());
        menu.Items.Add("切换播放模式", null, (_, _) => Player.CyclePlayMode());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (IsMiniMode)
            {
                // 迷你模式下：点托盘显示 / 隐藏迷你窗口
                if (Mini.IsVisible) Mini.Hide(); else ShowMini();
                return;
            }
            if (Shell.IsVisible && Shell.WindowState != WindowState.Minimized) Shell.Hide();
            else Shell.BringToFront();
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerController.Current) || _tray is null) return;
            var title = Player.Current?.DisplayTitle;
            var text = title is null ? "VUP 切片播放器" : "♪ " + title;
            _tray.Text = text.Length > 63 ? text[..63] : text;
        };
    }

    public void ShowTrayTip()
    {
        if (_trayTipShown || _tray is null) return;
        _trayTipShown = true;
        _tray.ShowBalloonTip(3000, "VUP 切片播放器", "播放器在托盘里继续运行。右键托盘图标可以退出。", Forms.ToolTipIcon.Info);
    }

    public void Quit()
    {
        IsQuitting = true;
        Shell.AllowClose = true;
        Settings.Save();
        Mini?.Close();
        Shell.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Settings?.Save();
            _control?.Dispose();
            _media?.Dispose();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            Player?.Dispose();
            Api?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("退出时出错：" + ex.Message);
        }
        _singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }

    // ---------------- 控制管道（MCP 进程通过它控制播放） ----------------

    Task<ControlProtocol.Response> HandleControlAsync(ControlProtocol.Request req) =>
        Dispatcher.InvokeAsync(() => HandleOnUiAsync(req)).Task.Unwrap();

    async Task<ControlProtocol.Response> HandleOnUiAsync(ControlProtocol.Request req)
    {
        var a = req.Args ?? [];
        static ControlProtocol.Response Ok(JsonNode? data = null) => new(true, data, null);
        static ControlProtocol.Response Fail(string error) => new(false, null, error);

        switch (req.Cmd)
        {
            case "ping":
                return Ok(new JsonObject { ["app"] = "VupPlayer" });

            case "show":
                if (IsMiniMode) ShowMini(); else Shell.BringToFront();
                if (a["input"]?.GetValue<string>() is string input)
                {
                    ViewModel.ImportText = input;
                    ViewModel.ImportCommand.Execute(null);
                }
                return Ok();

            case "now_playing":
                return Ok(NowPlaying());

            case "queue":
            {
                var ids = Player.Queue.Upcoming(a["limit"]?.GetValue<int>() ?? 30);
                var arr = new JsonArray();
                foreach (var t in Repo.GetTracks(ids)) arr.Add(Brief(t));
                return Ok(new JsonObject { ["mode"] = Player.PlayMode.ToString(), ["count"] = Player.Queue.Count, ["upcoming"] = arr });
            }

            case "play":
            {
                var ids = Ids(a["track_ids"]);
                if (ids.Count == 0) return Fail("track_ids 为空");
                var start = a["start_id"]?.GetValue<long>();
                if (a["mode"]?.GetValue<string>() is string m && Enum.TryParse<PlayMode>(m, true, out var pm)) Player.PlayMode = pm;
                await Player.PlayListAsync(ids, start, a["context"]?.GetValue<string>() ?? "Claude");
                ViewModel.NotifyModeChanged();
                return Ok(NowPlaying());
            }

            case "play_mix":
            {
                var kind = a["kind"]?.GetValue<string>()?.ToLowerInvariant() ?? "daily";
                if (kind == "radio")
                {
                    await Player.StartRadioAsync();
                    return Ok(NowPlaying());
                }
                var mix = new Core.Recommend.DailyMix(Repo);
                var items = kind switch { "rediscover" => mix.Rediscover(), "fresh" => mix.Fresh(), _ => mix.GetDaily() };
                if (items.Count == 0) return Fail("这个推荐歌单现在是空的");
                var name = kind switch { "rediscover" => "好久不见", "fresh" => "新歌速递", _ => "每日推荐" };
                var picker = kind switch { "rediscover" => "rediscover", "fresh" => "fresh", _ => "daily" };
                await Player.PlayListAsync(items.Select(i => i.TrackId).ToList(), null, "推荐：" + name, picker,
                    items.ToDictionary(i => i.TrackId, i => i.Reason));
                ViewModel.NotifyModeChanged();
                return Ok(NowPlaying());
            }

            case "play_now":
            {
                // 插到当前曲目之后并立刻播放第一首，不打乱原来的队列
                var ids = Ids(a["track_ids"]);
                if (ids.Count == 0) return Fail("track_ids 为空");
                if (Player.Queue.Count == 0) await Player.PlayListAsync(ids, ids[0], "Claude");
                else
                {
                    Player.Queue.PlayNext(ids);
                    await Player.PlayTrackAsync(ids[0]);
                }
                return Ok(NowPlaying());
            }

            case "enqueue":
            {
                var ids = Ids(a["track_ids"]);
                if (a["next"]?.GetValue<bool>() == true) Player.Queue.PlayNext(ids);
                else Player.Queue.Enqueue(ids);
                if (Player.Current is null && ids.Count > 0) await Player.PlayTrackAsync(ids[0]);
                return Ok(new JsonObject { ["queued"] = ids.Count });
            }

            case "control":
            {
                var action = a["action"]?.GetValue<string>() ?? "";
                var value = a["value"];
                switch (action)
                {
                    case "pause": Player.SetPaused(true); break;
                    case "resume": Player.SetPaused(false); break;
                    case "toggle": Player.TogglePause(); break;
                    case "next": await Player.NextAsync(); break;
                    case "previous": await Player.PreviousAsync(); break;
                    case "stop": Player.Stop(); break;
                    case "seek": Player.Seek(value?.GetValue<double>() ?? 0); break;
                    case "volume": Player.Volume = Math.Clamp(value?.GetValue<double>() ?? Player.Volume, 0, 100); break;
                    case "mode":
                        if (!Enum.TryParse<PlayMode>(value?.GetValue<string>(), true, out var pm)) return Fail("mode 取值：Sequential / RepeatAll / RepeatOne / Shuffle / Random");
                        Player.PlayMode = pm;
                        break;
                    case "stream_mode":
                        if (!Enum.TryParse<StreamMode>(value?.GetValue<string>(), true, out var sm)) return Fail("stream_mode 取值：Video / Audio");
                        await SetStreamModeAsync(sm);
                        break;
                    default:
                        return Fail("未知 action：" + action);
                }
                return Ok(NowPlaying());
            }

            case "library_changed":
                ViewModel.AfterLibraryEdit();
                return Ok();

            case "view_mode":
            {
                // {"mode": "full" | "widget" | "mini"}
                switch (a["mode"]?.GetValue<string>()?.ToLowerInvariant())
                {
                    case "full": SetWidgetLayout(false); break;
                    case "widget": SetWidgetLayout(true); break;
                    case "mini": EnterMiniMode(); break;
                    default: return Fail("mode 取值：full / widget / mini");
                }
                await Task.Delay(300);
                return Ok(NowPlaying());
            }

            case "mini_mode":
            {
                // {"on": true/false}，不传就切换
                var on = a["on"]?.GetValue<bool>() ?? !IsMiniMode;
                if (on) EnterMiniMode(); else ExitMiniMode();
                await Task.Delay(300);   // 等模式切换（可能涉及重新加载音频流）开始
                return Ok(NowPlaying());
            }

            case "debug_screenshot" or "debug_select_nav" or "debug_mpv" or "debug_widget_search" when !DevMode:
                return Fail("调试命令只在开发模式（VUPPLAYER_OFFSCREEN=1）下可用");

            case "debug_screenshot":
            {
                // 开发用：把窗口内容（不含 mpv 画面）渲染成 PNG
                var path = a["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path)) return Fail("path 为空");
                Window target = a["window"]?.GetValue<string>() == "mini" ? Mini : Shell;
                var root = (FrameworkElement)target.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(target == Mini ? System.Windows.Media.Brushes.White : Shell.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                rtb.Render(dv);
                rtb.Render(root);   // 直接渲染根元素，避免 VisualBrush 按内容边界拉伸
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                return Ok(new JsonObject { ["path"] = path });
            }

            case "debug_widget_search":
                ViewModel.WidgetSearch = a["text"]?.GetValue<string>() ?? "";
                return Ok(new JsonObject { ["results"] = ViewModel.WidgetResults.Count });

            case "debug_mpv":
            {
                var o = new JsonObject();
                foreach (var name in new[] { "vo-configured", "vid", "aid", "width", "height", "video-codec", "hwdec-current", "audio-codec-name", "wid", "demuxer-cache-duration" })
                    o[name] = Player.DebugProperty(name);
                o["video_host_visible"] = Shell.VideoHost.IsVisible;
                o["video_host_size"] = $"{Shell.VideoHost.ActualWidth}x{Shell.VideoHost.ActualHeight}";
                return Ok(o);
            }

            case "debug_select_nav":
            {
                var name = a["name"]?.GetValue<string>();
                var nav = ViewModel.NavItems.FirstOrDefault(n => n.Name == name);
                if (nav is null) return Fail("找不到导航项：" + name);
                ViewModel.SelectedNav = nav;
                return Ok();
            }

            case "import":
            {
                var link = a["input"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(link)) return Fail("input 为空");
                ViewModel.ImportText = link;
                await ViewModel.ImportCommand.ExecuteAsync(null);
                return Ok(new JsonObject { ["status"] = ViewModel.ImportStatus });
            }

            default:
                return Fail("未知命令：" + req.Cmd);
        }
    }

    JsonObject NowPlaying()
    {
        var o = new JsonObject
        {
            ["playing"] = Player.IsPlaying,
            ["position_sec"] = Math.Round(Player.Position, 1),
            ["duration_sec"] = Math.Round(Player.Duration, 1),
            ["mode"] = Player.PlayMode.ToString(),
            ["stream_mode"] = Player.StreamMode.ToString(),
            ["mini_mode"] = IsMiniMode,
            ["view_mode"] = IsMiniMode ? "mini" : Shell.IsWidget ? "widget" : "full",
            ["volume"] = Math.Round(Player.Volume),
            ["queue_count"] = Player.Queue.Count,
            ["status"] = Player.StatusText,
            ["recommend_reason"] = Player.RecommendReason,
        };
        if (Player.Current is { } t) o["track"] = Brief(t);
        return o;
    }

    static JsonObject Brief(TrackView t) => new()
    {
        ["id"] = t.Id,
        ["title"] = t.DisplayTitle,
        ["song_title"] = t.SongTitle,
        ["streamer"] = t.StreamerName,
        ["uploader"] = t.UpName,
        ["video_title"] = t.VideoTitle,
        ["bvid"] = t.Bvid,
        ["page"] = t.Page,
        ["duration_sec"] = t.DurationSec,
    };

    static List<long> Ids(JsonNode? node) =>
        node is JsonArray arr ? arr.Where(x => x is not null).Select(x => x!.GetValue<long>()).ToList() : [];
}
