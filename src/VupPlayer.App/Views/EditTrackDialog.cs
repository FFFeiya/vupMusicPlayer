using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VupPlayer.Core;
using VupPlayer.Core.Data;

namespace VupPlayer.App.Views;

/// <summary>编辑曲目信息。多选时，留空的字段不修改，标签为追加。</summary>
public static class EditTrackDialog
{
    public static TrackUpdate? Show(Window owner, IReadOnlyList<TrackView> tracks, IReadOnlyList<Streamer> streamers)
    {
        var single = tracks.Count == 1 ? tracks[0] : null;
        var win = DialogKit.Create(owner, single is null ? $"编辑 {tracks.Count} 首曲目" : "编辑曲目信息", 460);
        var panel = new StackPanel { Margin = new Thickness(22, 14, 22, 20) };

        if (single is not null)
        {
            panel.Children.Add(new TextBlock { Text = single.VideoTitle, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
            if (single.PageCount > 1)
                panel.Children.Add(DialogKit.Label($"P{single.Page}：{single.PartTitle}", 4));
        }
        else
        {
            panel.Children.Add(DialogKit.Label("留空的字段保持不变；标签会追加到每一首。", 6));
        }

        string? Common(Func<TrackView, string?> f)
        {
            var values = tracks.Select(f).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }

        panel.Children.Add(DialogKit.Label("歌名"));
        var song = DialogKit.Input(Common(t => t.SongTitle));
        panel.Children.Add(song);

        panel.Children.Add(DialogKit.Label("主播（原唱的 VUP）"));
        var streamer = new ComboBox
        {
            IsEditable = true,
            Text = Common(t => t.StreamerName) ?? "",
            ItemsSource = streamers.Select(s => s.Name).ToList(),
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(streamer);

        panel.Children.Add(DialogKit.Label(single is null ? "追加标签（逗号分隔）" : "标签（逗号分隔）"));
        var tags = DialogKit.Input(single?.Tags?.Replace(",", "，"));
        panel.Children.Add(tags);

        TextBox? start = null, end = null, note = null;
        if (single is not null)
        {
            panel.Children.Add(DialogKit.Label("只播放其中一段（可选，格式 分:秒，例如 1:23）"));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            start = DialogKit.Input(Fmt(single.StartSec), "开始");
            end = DialogKit.Input(Fmt(single.EndSec), "结束");
            Grid.SetColumn(end, 2);
            row.Children.Add(start);
            row.Children.Add(end);
            panel.Children.Add(row);

            panel.Children.Add(DialogKit.Label("备注"));
            note = DialogKit.Input(single.Note);
            panel.Children.Add(note);
        }

        TrackUpdate? result = null;
        panel.Children.Add(DialogKit.ButtonRow(
            DialogKit.Button("取消", false, (_, _) => win.Close(), isCancel: true),
            DialogKit.Button("保存", true, (_, _) =>
            {
                var u = new TrackUpdate();
                var tagList = tags.Text.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (single is not null)
                {
                    u.SongTitle = song.Text.Trim();
                    u.StreamerName = streamer.Text.Trim();
                    var old = (single.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    u.AddTags = tagList.Except(old, StringComparer.OrdinalIgnoreCase).ToList();
                    u.RemoveTags = old.Except(tagList, StringComparer.OrdinalIgnoreCase).ToList();
                    u.Note = note!.Text.Trim();
                    if (!TryParse(start!.Text, out var s) || !TryParse(end!.Text, out var e))
                    {
                        MessageBox.Show(win, "时间格式不对，应为 分:秒，例如 1:23", "提示");
                        return;
                    }
                    u.StartSec = s ?? -1;
                    u.EndSec = e ?? -1;
                }
                else
                {
                    if (song.Text.Trim().Length > 0) u.SongTitle = song.Text.Trim();
                    if (streamer.Text.Trim().Length > 0) u.StreamerName = streamer.Text.Trim();
                    u.AddTags = tagList;
                }
                result = u;
                win.DialogResult = true;
            }, isDefault: true)));

        win.Content = panel;
        win.Loaded += (_, _) => song.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    static string Fmt(double? s) => s is double v ? $"{(int)(v / 60)}:{v % 60:00.#}" : "";

    static bool TryParse(string text, out double? seconds)
    {
        seconds = null;
        text = text.Trim().Replace('：', ':');
        if (text.Length == 0) return true;
        var parts = text.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
