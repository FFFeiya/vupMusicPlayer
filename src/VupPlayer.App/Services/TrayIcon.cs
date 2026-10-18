using System.Drawing;

namespace VupPlayer.App.Services;

/// <summary>托盘图标：和程序图标同一个 Assets/app.ico，按系统小图标尺寸取最合适的一帧。</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
