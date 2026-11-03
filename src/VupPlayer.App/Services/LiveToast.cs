using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using VupPlayer.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace VupPlayer.App.Services;

/// <summary>开播通知被点击时带回来的信息。</summary>
public sealed record LiveToastAction(string Action, long RoomId, long Mid);

/// <summary>
/// Windows 原生通知（toast）。非打包程序要自己在注册表登记 AUMID，决定通知上显示的程序名和图标；
/// 不注册 COM 激活器，所以点击回调只在程序运行时有效——播放器常驻托盘，够用。
/// 另一个限制：非打包程序的通知不支持 http 图片，封面必须先下载到本地再按文件路径引用。
/// </summary>
public sealed class LiveToast
{
    static readonly string Aumid =
        Environment.GetEnvironmentVariable("VUPPLAYER_HOME") is { Length: > 0 } ? "VupPlayer.Dev" : "VupPlayer";

    const string Group = "live";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>保持已弹出通知的引用，否则回调可能被 GC 掉。</summary>
    readonly Dictionary<long, ToastNotification> _active = [];

    public bool Available { get; private set; }

    /// <summary>通知被点击（点通知本体或按钮）。</summary>
    public event Action<LiveToastAction>? Activated;

    /// <summary>在显示任何窗口之前调用一次。</summary>
    public void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key.SetValue("DisplayName", "VUP 切片播放器");
            if (ExtractIcon() is { } icon) key.SetValue("IconUri", icon);
            SetCurrentProcessExplicitAppUserModelID(Aumid);
            Available = true;
        }
        catch (Exception ex)
        {
            AppLog.Write("注册通知失败（开播通知将不可用）：" + ex.Message);
        }
    }

    /// <summary>通知上的程序图标需要一个真实文件，把打包进程序的 logo 落盘一次。</summary>
    static string? ExtractIcon()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Cache);
            var path = Path.Combine(AppPaths.Cache, "app-icon.png");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            using var src = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png")).Stream;
            using var dst = File.Create(path);
            src.CopyTo(dst);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Write("导出通知图标失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>弹一条开播通知。</summary>
    public async Task ShowLiveAsync(LiveRoom room, bool silent)
    {
        if (!Available) return;
        try
        {
            var cover = await CacheImageAsync(room.LastCover, $"cover-{room.Mid}").ConfigureAwait(false);
            var launch = Args("open", room);
            var hero = cover is null ? "" : $"""<image placement="hero" src="{Esc(cover)}"/>""";
            var area = room.LastArea is { Length: > 0 } a
                ? $"""<text placement="attribution">{Esc(a)}</text>"""
                : "";
            var xml = $"""
                <toast launch="{Esc(launch)}" scenario="default">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{Esc(room.DisplayName)} 开播了</text>
                      <text>{Esc(room.LastTitle ?? "")}</text>
                      {area}
                      {hero}
                    </binding>
                  </visual>
                  <actions>
                    <action content="立即观看" arguments="{Esc(Args("watch", room))}" activationType="foreground"/>
                    <action content="不再提醒" arguments="{Esc(Args("mute", room))}" activationType="foreground"/>
                  </actions>
                  <audio silent="{(silent ? "true" : "false")}"/>
                </toast>
                """;

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc) { Tag = Tag(room.RoomId), Group = Group };
            toast.Activated += (_, e) =>
            {
                var args = (e as ToastActivatedEventArgs)?.Arguments;
                if (Parse(args) is { } parsed) Activated?.Invoke(parsed);
            };
            _active[room.RoomId] = toast;
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
            AppLog.Write($"开播通知：{room.DisplayName} room={room.RoomId}");
        }
        catch (Exception ex)
        {
            AppLog.Write("弹开播通知失败：" + ex.Message);
        }
    }

    /// <summary>主播下播或已经点过了：把通知从通知中心里撤回。</summary>
    public void Retract(long roomId)
    {
        _active.Remove(roomId);
        if (!Available) return;
        try { ToastNotificationManager.History.Remove(Tag(roomId), Group, Aumid); }
        catch { /* 通知可能已经被用户清掉了 */ }
    }

    static string Tag(long roomId) => "live-" + roomId;

    static string Args(string action, LiveRoom room) => $"action={action}&room={room.RoomId}&mid={room.Mid}";

    internal static LiveToastAction? Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        string? action = null;
        long room = 0, mid = 0;
        foreach (var part in arguments.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "action": action = kv[1]; break;
                case "room": _ = long.TryParse(kv[1], out room); break;
                case "mid": _ = long.TryParse(kv[1], out mid); break;
            }
        }
        return action is null ? null : new LiveToastAction(action, room, mid);
    }

    /// <summary>把封面下载到本地缓存（非打包程序的通知不能用 http 图片）。</summary>
    async Task<string?> CacheImageAsync(string? url, string name)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        try
        {
            Directory.CreateDirectory(Path.Combine(AppPaths.Cache, "live"));
            var ext = Path.GetExtension(uri.AbsolutePath) is { Length: > 1 and <= 5 } e ? e : ".jpg";
            var path = Path.Combine(AppPaths.Cache, "live", name + ext);
            // 封面每场直播都会变，缓存 1 小时
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1))
                return path;
            var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Write("下载封面失败：" + ex.Message);
            return null;
        }
    }

    static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
