using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VupPlayer.Core.Control;

/// <summary>
/// 主程序与 MCP 进程之间的控制协议：每行一个 JSON。
/// 请求：{"cmd":"now_playing","args":{...}}；响应：{"ok":true,"data":{...}} 或 {"ok":false,"error":"..."}。
/// </summary>
public static class ControlProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Request(string Cmd, JsonObject? Args);
    public sealed record Response(bool Ok, JsonNode? Data, string? Error);
}

/// <summary>MCP 进程使用的客户端。每次调用新建连接，简单可靠。</summary>
public static class ControlClient
{
    /// <summary>发送命令。主程序未运行时返回 null。</summary>
    public static async Task<ControlProtocol.Response?> SendAsync(string cmd, JsonObject? args = null, int timeoutMs = 3000, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", AppPaths.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(Math.Min(timeoutMs, 1500), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var line = JsonSerializer.Serialize(new ControlProtocol.Request(cmd, args), ControlProtocol.Json);
        await writer.WriteLineAsync(line.AsMemory(), cts.Token).ConfigureAwait(false);
        var resp = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        return resp is null ? null : JsonSerializer.Deserialize<ControlProtocol.Response>(resp, ControlProtocol.Json);
    }
}

/// <summary>主程序使用的服务端：循环接受连接，把请求交给 handler。</summary>
public sealed class ControlServer(Func<ControlProtocol.Request, Task<ControlProtocol.Response>> handler) : IDisposable
{
    readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(AppPaths.ControlPipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(p, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                // 管道名被占用（例如残留实例）：稍后重试
                await Task.Delay(1000, ct).ContinueWith(_ => { }).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                ControlProtocol.Response resp;
                try
                {
                    var req = JsonSerializer.Deserialize<ControlProtocol.Request>(line, ControlProtocol.Json)
                              ?? throw new InvalidOperationException("空请求");
                    resp = await handler(req).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    resp = new ControlProtocol.Response(false, null, ex.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, ControlProtocol.Json).AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* 客户端断开 */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
