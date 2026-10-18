using System.IO;
using VupPlayer.Core;

namespace VupPlayer.App.Services;

/// <summary>极简文件日志：%APPDATA%\VupPlayer\logs\app-yyyyMMdd.log。</summary>
public static class AppLog
{
    static readonly Lock Gate = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var file = Path.Combine(AppPaths.Logs, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate) File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响程序
        }
    }
}
