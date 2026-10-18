using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VupPlayer.App.Services;
using VupPlayer.App.ViewModels;

namespace VupPlayer.App;

/// <summary>
/// 迷你模式：置顶小窗，只有封面、标题和播放控制。拖动任意空白处移动，滚轮调音量。
/// </summary>
public partial class MiniWindow : Window
{
    readonly MainViewModel _vm;
    readonly AppSettings _settings;
    readonly App _app;
    bool _dragging;

    PlayerController Player => _vm.Player;

    public MiniWindow(MainViewModel vm, AppSettings settings, App app)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _app = app;
        DataContext = vm;
        Player.PropertyChanged += Player_PropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        LocationChanged += (_, _) => RememberPosition();
    }

    /// <summary>放到上次的位置；没有记录或位置已经不在任何屏幕内时，放到主屏右下角。</summary>
    public void PlaceWindow()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_settings.MiniLeft is double l && _settings.MiniTop is double t
            && vs.Contains(new Point(l + 40, t + 40)) && vs.Contains(new Point(l + Width - 40, t + Height - 40)))
        {
            Left = l;
            Top = t;
            return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - Height - 16;
    }

    void RememberPosition()
    {
        if (!IsVisible || Left < -3000) return;   // 开发模式下窗口在屏幕外，不记
        _settings.MiniLeft = Left;
        _settings.MiniTop = Top;
    }

    void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerController.Position) && !_dragging && IsVisible)
            ProgressSlider.Value = Player.Position;
    }

    // ---------------- 交互 ----------------

    void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 按钮和进度条自己处理点击，其余空白处拖动窗口
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is null && FindParent<Slider>(d) is null)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    void Cover_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _app.ExitMiniMode();
            e.Handled = true;
        }
    }

    void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Player.Volume = Math.Clamp(Player.Volume + (e.Delta > 0 ? 5 : -5), 0, 100);
        e.Handled = true;
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space: Player.TogglePause(); break;
            case Key.Escape: _app.ExitMiniMode(); break;
            case Key.M when ctrl: _app.ExitMiniMode(); break;
            case Key.Right when ctrl: await Player.NextAsync(); break;
            case Key.Left when ctrl: await Player.PreviousAsync(); break;
            case Key.Right: Player.Seek(Player.Position + 5); break;
            case Key.Left: Player.Seek(Math.Max(0, Player.Position - 5)); break;
            case Key.Up: Player.Volume = Math.Min(100, Player.Volume + 5); break;
            case Key.Down: Player.Volume = Math.Max(0, Player.Volume - 5); break;
            default: return;
        }
        e.Handled = true;
    }

    void Play_Click(object sender, RoutedEventArgs e) => Player.TogglePause();
    async void Next_Click(object sender, RoutedEventArgs e) => await Player.NextAsync();
    async void Prev_Click(object sender, RoutedEventArgs e) => await Player.PreviousAsync();
    void Mode_Click(object sender, RoutedEventArgs e) => Player.CyclePlayMode();
    void Expand_Click(object sender, RoutedEventArgs e) => _app.ExitMiniMode();

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        // 留在迷你模式，只是先藏到托盘；点托盘图标会再出来
        Hide();
        _app.ShowTrayTip();
    }

    void Progress_MouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;

    void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Player.Seek(ProgressSlider.Value);
            _dragging = false;
        });
    }

    static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d is not null;
             d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                 : LogicalTreeHelper.GetParent(d))
        {
            if (d is T t) return t;
        }
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 等关闭操作：只隐藏，由 App 统一管理生命周期
        if (!_app.IsQuitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
