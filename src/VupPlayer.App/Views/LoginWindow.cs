using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using VupPlayer.Bilibili;

namespace VupPlayer.App.Views;

/// <summary>扫码登录：显示二维码，每 2 秒轮询一次状态。</summary>
public static class LoginWindow
{
    public static bool Show(Window owner, BiliApi api)
    {
        var win = DialogKit.Create(owner, "扫码登录 B 站", 340);
        var panel = new StackPanel { Margin = new Thickness(24) };
        var image = new Image { Width = 220, Height = 220, Margin = new Thickness(0, 4, 0, 0) };
        var border = new Border { Background = System.Windows.Media.Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = image };
        var status = new TextBlock { Text = "正在获取二维码…", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Foreground = DialogKit.Res("TextBrush") };
        var hint = DialogKit.Label("用 B 站手机客户端扫码。登录后可以导入自己的收藏夹（包括私密收藏夹），并获得 1080P 画质。cookie 用 Windows DPAPI 加密保存在本机。");
        hint.TextAlignment = TextAlignment.Center;
        panel.Children.Add(border);
        panel.Children.Add(status);
        panel.Children.Add(hint);
        var refresh = DialogKit.Button("刷新二维码", false, (_, _) => { });
        panel.Children.Add(DialogKit.ButtonRow(refresh, DialogKit.Button("关闭", false, (_, _) => win.Close(), isCancel: true)));
        win.Content = panel;

        string? key = null;
        var success = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        async Task NewCodeAsync()
        {
            try
            {
                status.Text = "正在获取二维码…";
                var (url, k) = await Task.Run(() => api.CreateQrLoginAsync());
                key = k;
                image.Source = RenderQr(url);
                status.Text = "请扫码";
                timer.Start();
            }
            catch (Exception ex)
            {
                status.Text = "获取二维码失败：" + ex.Message;
            }
        }

        timer.Tick += async (_, _) =>
        {
            if (key is null) return;
            timer.Stop();
            try
            {
                var state = await Task.Run(() => api.PollQrLoginAsync(key));
                switch (state)
                {
                    case QrState.Success:
                        success = true;
                        status.Text = "登录成功";
                        win.DialogResult = true;
                        return;
                    case QrState.Scanned:
                        status.Text = "已扫码，请在手机上确认";
                        break;
                    case QrState.Expired:
                        status.Text = "二维码已过期，请刷新";
                        return;
                }
            }
            catch (Exception ex)
            {
                status.Text = "查询状态失败：" + ex.Message;
            }
            timer.Start();
        };
        refresh.Click += async (_, _) => { timer.Stop(); await NewCodeAsync(); };
        win.Loaded += async (_, _) => await NewCodeAsync();
        win.Closed += (_, _) => timer.Stop();
        win.ShowDialog();
        return success;
    }

    static BitmapImage RenderQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(png);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
