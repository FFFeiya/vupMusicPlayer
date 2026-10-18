using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using VupPlayer.Core.Playback;
using VupPlayer.Core.Streams;

namespace VupPlayer.App;

/// <summary>true → Visible。参数为 "invert" 时取反。也接受 null / 空字符串（视为 false）和数字（非零为 true）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>秒 → m:ss 或 h:mm:ss。</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value switch { double d => d, int i => i, long l => l, _ => 0 };
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PlayPauseGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PlayModeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter as string == "text") return value is PlayMode m ? m.DisplayName() : "";
        return value switch
        {
            PlayMode.Sequential => "",
            PlayMode.RepeatAll => "",
            PlayMode.RepeatOne => "",
            PlayMode.Shuffle => "",
            PlayMode.Random => "",
            _ => "",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StreamModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var video = value is StreamMode.Video;
        return parameter switch
        {
            "glyph" => video ? "" : "",
            "text" => video ? "画面" : "纯音频",
            "video" => video ? Visibility.Visible : Visibility.Collapsed,
            "audio" => video ? Visibility.Collapsed : Visibility.Visible,
            _ => video,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FavoriteGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>封面 URL → 缩小解码的位图，避免大图占内存。参数为解码宽度。</summary>
public sealed class CoverConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var width = int.TryParse(parameter as string, out var w) ? w : 320;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.DecodePixelWidth = width;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Slider 用：曲目没有时长时给一个默认最大值，防止除零。</summary>
public sealed class SafeMaxConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d > 0 ? d : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
