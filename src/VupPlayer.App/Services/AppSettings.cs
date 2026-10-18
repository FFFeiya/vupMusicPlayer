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
    public long? LastTrackId { get; set; }
    /// <summary>上次退出时是否处于迷你模式（下次启动直接进入）。</summary>
    public bool MiniMode { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
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
