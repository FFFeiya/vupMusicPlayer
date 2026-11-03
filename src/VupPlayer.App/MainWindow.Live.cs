using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VupPlayer.Core;

namespace VupPlayer.App;

/// <summary>直播页的交互。</summary>
public partial class MainWindow
{
    void LeaveLive_Click(object sender, RoutedEventArgs e) => _app.Player.StopLive();

    void LiveQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Player.Live is not { } live) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var list = live.Qualities.Count > 0
            ? live.Qualities
            : [(10000, "原画"), (400, "蓝光"), (250, "超清"), (150, "高清"), (80, "流畅")];
        foreach (var (qn, desc) in list)
        {
            var mi = new MenuItem { Header = desc, IsCheckable = true, IsChecked = live.Qn == qn };
            var value = qn;
            mi.Click += async (_, _) =>
            {
                _settings.LiveQuality = value;
                await _app.Player.SetLiveQualityAsync(value);
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    async void LiveList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LiveRoom room)
            await _vm.WatchLiveAsync(room);
    }

    void LiveList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var room = (e.OriginalSource as FrameworkElement)?.DataContext as LiveRoom;
        var menu = LiveList.ContextMenu!;
        menu.Items.Clear();

        Add(menu, "立即刷新开播状态", () => _vm.CheckLiveCommand.Execute(null));
        Add(menu, "从曲库导入主播", async () => await _vm.ImportStreamersToLiveAsync());

        if (room is null)
        {
            e.Handled = false;
            return;
        }

        menu.Items.Add(new Separator());
        if (room.IsLive) Add(menu, $"观看 {room.DisplayName} 的直播", async () => await _vm.WatchLiveAsync(room));
        else menu.Items.Add(new MenuItem { Header = room.StatusText, IsEnabled = false });

        Add(menu, room.Notify ? "关掉开播提醒" : "打开开播提醒", () => _vm.ToggleLiveNotify(room));
        menu.Items.Add(new Separator());
        Add(menu, "在浏览器打开直播间", () => Process.Start(new ProcessStartInfo(room.Url) { UseShellExecute = true }));
        Add(menu, "复制直播间链接", () => Clipboard.SetText(room.Url));
        if (room.TrackCount > 0 && room.StreamerId is long sid)
            Add(menu, $"看这位主播的 {room.TrackCount} 首切片", () =>
            {
                if (_vm.NavItems.FirstOrDefault(n => n.Kind == ViewModels.NavKind.Streamer && n.Id == sid) is { } nav)
                    _vm.SelectedNav = nav;
            });
        menu.Items.Add(new Separator());
        Add(menu, "移除这个直播间", () =>
        {
            if (MessageBox.Show(this, $"不再关注「{room.DisplayName}」的直播？曲库里的切片不会被删除。",
                    "确认", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                _vm.RemoveLiveRoom(room);
        });
    }
}
