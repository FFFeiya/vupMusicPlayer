using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VupPlayer.Core;

namespace VupPlayer.Bilibili;

/// <summary>cookie 持久化：JSON 后用 Windows DPAPI（当前用户）加密。</summary>
public static class CookieStore
{
    sealed record Item(string Name, string Value, string Domain, string Path, DateTime Expires, bool Secure, bool HttpOnly);

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VupPlayer.cookies.v1");

    public static void Save(CookieContainer container, string? path = null)
    {
        path ??= AppPaths.Cookies;
        var items = container.GetAllCookies()
            .Where(c => !c.Expired)
            .Select(c => new Item(c.Name, c.Value, c.Domain, c.Path, c.Expires, c.Secure, c.HttpOnly))
            .ToList();
        var json = JsonSerializer.SerializeToUtf8Bytes(items);
        var enc = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser)
            : json;
        File.WriteAllBytes(path, enc);
    }

    public static CookieContainer Load(string? path = null)
    {
        path ??= AppPaths.Cookies;
        var container = new CookieContainer();
        if (!File.Exists(path)) return container;
        try
        {
            var enc = File.ReadAllBytes(path);
            var json = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser)
                : enc;
            foreach (var i in JsonSerializer.Deserialize<List<Item>>(json) ?? [])
            {
                if (i.Expires != DateTime.MinValue && i.Expires < DateTime.Now) continue;
                container.Add(new Cookie(i.Name, i.Value, i.Path, i.Domain)
                {
                    Expires = i.Expires, Secure = i.Secure, HttpOnly = i.HttpOnly,
                });
            }
        }
        catch
        {
            // 文件损坏或换了用户：当作未登录
        }
        return container;
    }

    public static void Delete(string? path = null)
    {
        path ??= AppPaths.Cookies;
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>导出 Netscape 格式（给 yt-dlp 用）。返回临时文件路径，调用方用完删除。</summary>
    public static string ExportNetscape(CookieContainer container)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (Cookie c in container.GetAllCookies())
        {
            if (c.Expired) continue;
            var domain = c.Domain.StartsWith('.') ? c.Domain : "." + c.Domain;
            var expires = c.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(c.Expires).ToUnixTimeSeconds();
            sb.Append($"{domain}\tTRUE\t{c.Path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{expires}\t{c.Name}\t{c.Value}\n");
        }
        var file = Path.Combine(Path.GetTempPath(), $"vupplayer-cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, sb.ToString());
        return file;
    }
}
