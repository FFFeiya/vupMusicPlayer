using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace VupPlayer.Bilibili;

/// <summary>调用 yt-dlp.exe：作为取流兜底，也用于展开 UP 主投稿、系列等自己没实现的列表。</summary>
public sealed class YtDlp(string exePath, Func<System.Net.CookieContainer>? cookies = null)
{
    public string ExePath { get; } = exePath;
    public bool Available => File.Exists(ExePath);

    /// <summary>在常见位置查找 yt-dlp.exe：程序目录\tools、程序目录、PATH。</summary>
    public static string Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var p in new[] { Path.Combine(baseDir, "tools", "yt-dlp.exe"), Path.Combine(baseDir, "yt-dlp.exe") })
            if (File.Exists(p)) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "yt-dlp.exe");
            if (File.Exists(p)) return p;
        }
        return Path.Combine(baseDir, "tools", "yt-dlp.exe");
    }

    public async Task<JsonNode> DumpJsonAsync(string url, bool flatPlaylist, CancellationToken ct = default, int timeoutSec = 120)
    {
        if (!Available) throw new FileNotFoundException("找不到 yt-dlp.exe", ExePath);
        string? cookieFile = null;
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.ArgumentList.Add("-J");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--encoding");
            psi.ArgumentList.Add("utf-8");
            if (flatPlaylist) psi.ArgumentList.Add("--flat-playlist");
            else psi.ArgumentList.Add("--no-playlist");
            if (cookies is not null)
            {
                cookieFile = CookieStore.ExportNetscape(cookies());
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookieFile);
            }
            psi.ArgumentList.Add(url);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 yt-dlp");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                throw;
            }
            var output = await stdout.ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var line = err.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? err.Trim();
                throw new InvalidOperationException("yt-dlp 失败：" + (line.Length > 300 ? line[..300] : line));
            }
            return JsonNode.Parse(output) ?? throw new InvalidOperationException("yt-dlp 输出为空");
        }
        finally
        {
            if (cookieFile is not null) try { File.Delete(cookieFile); } catch { }
        }
    }

    /// <summary>展开列表链接，返回其中的 BV 号（按列表顺序）。</summary>
    public async Task<(string? Title, IReadOnlyList<string> Bvids)> ExpandListAsync(string url, CancellationToken ct = default)
    {
        var node = await DumpJsonAsync(url, flatPlaylist: true, ct, timeoutSec: 300).ConfigureAwait(false);
        var bvids = new List<string>();
        foreach (var e in node["entries"]?.AsArray() ?? [])
        {
            var text = (e?["url"]?.GetValue<string>() ?? "") + " " + (e?["id"]?.GetValue<string>() ?? "");
            var link = LinkParser.Parse(text);
            if (link.Bvid is not null && !bvids.Contains(link.Bvid)) bvids.Add(link.Bvid);
        }
        if (bvids.Count == 0 && node["id"]?.GetValue<string>() is string single && LinkParser.Parse(single).Bvid is string b)
            bvids.Add(b);
        return (node["title"]?.GetValue<string>(), bvids);
    }
}
