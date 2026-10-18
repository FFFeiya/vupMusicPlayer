using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VupPlayer.App.Views;

/// <summary>深色小对话框的公共构件。</summary>
internal static class DialogKit
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Window Create(Window owner, string title, double width)
    {
        return new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("PanelBrush"),
            Foreground = Res("TextBrush"),
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            ShowInTaskbar = false,
        };
    }

    public static TextBlock Label(string text, double top = 12) => new()
    {
        Text = text,
        Foreground = Res("SubTextBrush"),
        FontSize = 12,
        Margin = new Thickness(0, top, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBox Input(string? text = null, string? placeholder = null) => new()
    {
        Text = text ?? "",
        Tag = placeholder ?? "",
        Style = (Style)Application.Current.Resources["DarkTextBox"],
        Height = 34,
    };

    public static Button Button(string text, bool accent, RoutedEventHandler onClick, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources[accent ? "AccentButton" : "FlatButton"],
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 80,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        b.Click += onClick;
        return b;
    }

    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}

/// <summary>单行文本输入框。</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string? initial = null)
    {
        var win = DialogKit.Create(owner, title, 420);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(DialogKit.Label(prompt, 0));
        var box = DialogKit.Input(initial);
        panel.Children.Add(box);
        string? result = null;
        panel.Children.Add(DialogKit.ButtonRow(
            DialogKit.Button("取消", false, (_, _) => win.Close(), isCancel: true),
            DialogKit.Button("确定", true, (_, _) => { result = box.Text.Trim(); win.DialogResult = true; }, isDefault: true)));
        win.Content = panel;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }
}
