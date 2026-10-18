using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VupPlayer.Core.Data;

// VupPlayer 曲库 MCP 服务（stdio）。
// 注册：claude mcp add vupplayer --scope user -- "<路径>\VupPlayer.Mcp.exe"
var builder = Host.CreateApplicationBuilder(args);
// stdout 留给 MCP 协议，日志只写 stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var db = new Database();
db.Migrate();
builder.Services.AddSingleton(new LibraryRepository(db));
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "vupplayer", Version = "0.1.0" };
        o.ServerInstructions = """
            这是用户本地的 B 站 VUP 切片曲库（VupPlayer）。
            - 每条曲目是一个 B 站视频的一个分P（或其中一段）。字段：歌名 song_title、原唱主播 streamer、切片 UP 主 uploader、标签 tags。
            - 整理元数据时，先用 search_tracks（例如 missing_song_title=true）找出待整理曲目，必要时用 get_tracks 看视频简介。
            - 不确定的修改用 propose_metadata 提交为「待确认」，让用户在播放器里确认；确定的修改用 update_tracks 直接写入。
            - 播放控制类工具（now_playing / play / enqueue / player_control / get_queue / import_from_bilibili）需要播放器正在运行。
            - 不提供删除曲目的工具；不想听的曲目用 update_tracks 设置 hidden=true。
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
