using System.IO;
using System.Text.Json;
using VupPlayer.Core;
using VupPlayer.Core.Playback;
using VupPlayer.Core.Streams;

namespace VupPlayer.App.Services;

/// <summary>用户设置，保存在 %APPDATA%\VupPlayer\settings.json。</summary>
public sealed class AppSettings
{
    public double Volume { get; set; } = 70;
    public PlayMode PlayMode { get; set; } = PlayMode.RepeatAll;
    public StreamMode StreamMode { get; set; } = StreamMode.Audio;
    public bool Normalize { get; set; }
    public int MaxHeight { get; set; } = 1080;
    public bool CloseToTray { get; set; } = true;
    public bool PreferYtDlp { get; set; }
    /// <summary>优先使用的 CDN 镜像；为空 = B 站分配的地址，卡住时自动换。</summary>
    public string? PreferredMirror { get; set; }
    public long? LastTrackId { get; set; }
    // ---- 推荐 ----
    /// <summary>推荐器：weighted-v1 / random。</summary>
    public string Recommender { get; set; } = "weighted-v1";
    /// <summary>推荐范围：true 整个曲库，false 当前列表。</summary>
    public bool RecommendFromLibrary { get; set; } = true;
    /// <summary>口味：0.6 保守 / 1.0 均衡 / 1.6 冒险。</summary>
    public double RecommendTemperature { get; set; } = 1.0;
    /// <summary>从 B 站发现池挑新歌的比例：0 关闭 / 0.1 偶尔 / 0.25 经常。</summary>
    public double DiscoveryRate { get; set; } = 0.1;

    // ---- 直播 ----
    /// <summary>主播开播时弹 Windows 通知。</summary>
    public bool LiveNotify { get; set; } = true;
    /// <summary>开播通知静音（只弹不响）。</summary>
    public bool LiveNotifySilent { get; set; }
    /// <summary>查开播状态的间隔（分钟）。0 表示不查。一次请求查完所有订阅的主播，2 分钟很安全。</summary>
    public int LiveCheckMinutes { get; set; } = 2;
    /// <summary>直播画质：10000 原画 / 400 蓝光 / 250 超清 / 150 高清 / 80 流畅。</summary>
    public int LiveQuality { get; set; } = 10000;

    /// <summary>上次退出时是否处于迷你模式（下次启动直接进入）。</summary>
    public bool MiniMode { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    /// <summary>主窗口是否处于挂件布局（画面 + 列表 + 搜索的置顶小窗）。</summary>
    public bool WidgetLayout { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public double WidgetWidth { get; set; } = 380;
    public double WidgetHeight { get; set; } = 640;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new AppSettings();
        }
        catch
        {
            // 设置损坏时使用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 忽略写入失败
        }
    }
}
