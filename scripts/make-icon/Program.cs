using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 生成 VupPlayer 图标：粉→紫渐变圆角方块 + 白色双八分音符 + 小星光
var outDir = args[0];
Directory.CreateDirectory(outDir);

Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    var s = size / 256f;
    g.ScaleTransform(s, s);

    // 底板
    var inset = size <= 20 ? 2f : 8f;
    var rect = new RectangleF(inset, inset, 256 - inset * 2, 256 - inset * 2);
    using var plate = RoundRect(rect, size <= 20 ? 48 : 58);
    using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256), Color.FromArgb(0xFF, 0x8A, 0xB0), Color.FromArgb(0x9A, 0x6B, 0xFF)))
    {
        bg.InterpolationColors = new ColorBlend
        {
            Colors = [Color.FromArgb(0xFF, 0x9A, 0xB8), Color.FromArgb(0xFB, 0x72, 0x99), Color.FromArgb(0x9C, 0x6C, 0xFF)],
            Positions = [0f, 0.45f, 1f],
        };
        g.FillPath(bg, plate);
    }
    if (size >= 48)
    {
        // 顶部一点高光
        using var hi = new LinearGradientBrush(new PointF(0, inset), new PointF(0, 140), Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        g.SetClip(plate);
        g.FillRectangle(hi, 0, 0, 256, 140);
        g.ResetClip();
    }

    // 音符
    using var white = new SolidBrush(Color.White);
    var bold = size <= 24 ? 1.25f : 1f;   // 小尺寸加粗
    void Head(float cx, float cy)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(-22);
        g.FillEllipse(white, -31 * bold, -23 * bold, 62 * bold, 46 * bold);
        g.Restore(st);
    }
    var stem = 15f * bold;
    // 左音符杆在 x≈118，右音符杆在 x≈204
    Head(94, 182);
    Head(180, 162);
    g.FillRectangle(white, 112 - (stem - 15), 82, stem, 98);
    g.FillRectangle(white, 197 - (stem - 15), 62, stem, 98);
    using (var beam = new GraphicsPath())
    {
        var t = 30 * bold;
        beam.AddPolygon(new[] { new PointF(112 - (stem - 15), 82), new PointF(212, 60), new PointF(212, 60 + t), new PointF(112 - (stem - 15), 82 + t) });
        g.FillPath(white, beam);
    }

    if (size >= 32)
    {
        // 星光
        using var star = new GraphicsPath();
        float x = 74, y = 76, r = 26, w = 6;
        star.AddPolygon(new[]
        {
            new PointF(x, y - r), new PointF(x + w, y - w), new PointF(x + r, y), new PointF(x + w, y + w),
            new PointF(x, y + r), new PointF(x - w, y + w), new PointF(x - r, y), new PointF(x - w, y - w),
        });
        using var sb = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.FillPath(sb, star);
    }
    return bmp;
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    var p = new GraphicsPath();
    var d = radius * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static byte[] Dib(Bitmap bmp)
{
    int n = bmp.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = n - 1; y >= 0; y--)
        for (int x = 0; x < n; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    int maskRow = ((n + 31) / 32) * 4;
    w.Write(new byte[maskRow * n]);
    return ms.ToArray();
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
var images = new List<byte[]>();
foreach (var sz in sizes)
{
    using var b = Render(sz);
    if (sz == 256)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        images.Add(ms.ToArray());
        b.Save(Path.Combine(outDir, "logo.png"), ImageFormat.Png);
    }
    else images.Add(Dib(b));
    if (sz is 32 or 64) b.Save(Path.Combine(outDir, $"preview-{sz}.png"), ImageFormat.Png);
}
using (var fs = File.Create(Path.Combine(outDir, "app.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        var sz = sizes[i];
        w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}
Console.WriteLine("ok");
