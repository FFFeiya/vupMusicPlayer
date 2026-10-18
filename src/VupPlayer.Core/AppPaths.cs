namespace VupPlayer.Core;

/// <summary>用户数据目录：%APPDATA%\VupPlayer。可用环境变量 VUPPLAYER_HOME 覆盖（测试或便携模式）。</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string Database => Path.Combine(Root, "library.db");
    public static string Cookies => Path.Combine(Root, "cookies.dat");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Backups => Path.Combine(Root, "backups");

    /// <summary>
    /// 主程序与 MCP 进程之间的命名管道名。设置了 VUPPLAYER_HOME（测试或便携模式）时带上目录的哈希，
    /// 这样测试实例不会和正在使用的播放器互相干扰。
    /// </summary>
    public static string ControlPipeName { get; } = "vupplayer-control" + InstanceSuffix();

    /// <summary>单实例锁的名字，规则同上。</summary>
    public static string InstanceMutexName { get; } = "VupPlayer.SingleInstance" + InstanceSuffix();

    static string InstanceSuffix()
    {
        var env = Environment.GetEnvironmentVariable("VUPPLAYER_HOME");
        if (string.IsNullOrWhiteSpace(env)) return "";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(env).ToLowerInvariant()));
        return "-" + Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("VUPPLAYER_HOME");
        var root = string.IsNullOrWhiteSpace(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VupPlayer")
            : env;
        Directory.CreateDirectory(root);
        return root;
    }
}
