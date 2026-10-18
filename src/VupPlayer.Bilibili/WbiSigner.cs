using System.Security.Cryptography;
using System.Text;

namespace VupPlayer.Bilibili;

/// <summary>B 站 WBI 签名：对参数排序、过滤特殊字符、拼接 mixin key 后取 MD5，得到 w_rid。</summary>
public static class WbiSigner
{
    static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52,
    ];

    /// <summary>由 img_key 和 sub_key 生成 32 位 mixin key。</summary>
    public static string MixinKey(string imgKey, string subKey)
    {
        var raw = imgKey + subKey;
        var sb = new StringBuilder(32);
        foreach (var i in MixinKeyEncTab)
        {
            if (i < raw.Length) sb.Append(raw[i]);
            if (sb.Length == 32) break;
        }
        return sb.ToString();
    }

    /// <summary>从 wbi_img 的 URL 中取文件名（不含扩展名）作为 key。</summary>
    public static string KeyFromUrl(string url)
    {
        var name = url[(url.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>返回带 wts 和 w_rid 的完整查询串。</summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string mixinKey, long? wts = null)
    {
        var dict = parameters.ToDictionary(p => p.Key, p => Filter(p.Value));
        dict["wts"] = (wts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var query = string.Join("&", dict.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey));
        return query + "&w_rid=" + Convert.ToHexStringLower(hash);
    }

    static string Filter(string v) => new(v.Where(c => c is not ('!' or '\'' or '(' or ')' or '*')).ToArray());

    /// <summary>等价于 JavaScript 的 encodeURIComponent（大写十六进制）。</summary>
    static string Encode(string s) => Uri.EscapeDataString(s);
}
