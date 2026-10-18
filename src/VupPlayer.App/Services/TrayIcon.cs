using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace VupPlayer.App.Services;

/// <summary>运行时画一个托盘图标：粉色圆角方块 + 白色音符。</summary>
internal static partial class TrayIcon
{
    public static Icon Create()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 16;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
            path.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
            path.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(Color.FromArgb(0xFB, 0x72, 0x99));
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI Symbol", 34, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("♪", font, Brushes.White, new RectangleF(0, 2, size, size), fmt);
        }
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);
}
