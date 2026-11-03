using System.Net;
using VupPlayer.Core.Streams;

namespace VupPlayer.Core.Tests;

public class LineProberTests
{
    /// <summary>按主机名返回预设结果的假 handler。</summary>
    sealed class FakeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        /// <summary>按主机名设置回应前的延迟，用来测超时。</summary>
        public Dictionary<string, TimeSpan> Delays { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            Requested.Add(host);
            if (Delays.TryGetValue(host, out var d)) await Task.Delay(d, ct);
            return respond(host);
        }
    }

    static ResolvedStream Stream(params string[] hosts)
    {
        var urls = hosts.Select(h => $"https://{h}/live/x.m4s").ToList();
        return new ResolvedStream(urls[0], urls[0], new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(30), "测试", null, urls, urls);
    }

    static HttpResponseMessage Ok() => new(HttpStatusCode.PartialContent);
    static HttpResponseMessage Code(HttpStatusCode c) => new(c);

    [Fact]
    public async Task 只有一条线路时不探测()
    {
        var fake = new FakeHandler(_ => Ok());
        using var prober = new LineProber(fake);
        var one = new ResolvedStream("https://a.com/x", "https://a.com/x", new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(30), "测试");

        var r = await prober.PickAsync(one, 0, audioOnly: false, new LineHealth());

        Assert.Equal(0, r.Line);
        Assert.Equal(0, r.Probed);
        Assert.Empty(fake.Requested);
    }

    [Fact]
    public async Task 第一条通就用第一条()
    {
        var fake = new FakeHandler(_ => Ok());
        using var prober = new LineProber(fake);

        var r = await prober.PickAsync(Stream("a.com", "b.com", "c.com"), 0, false, new LineHealth());

        Assert.Equal(0, r.Line);
        Assert.Null(r.Error);
        Assert.Equal(["a.com"], fake.Requested);
    }

    [Fact]
    public async Task 跳过连不上的线路并记为坏线路()
    {
        var fake = new FakeHandler(h => h == "a.com" ? throw new HttpRequestException("连不上") : Ok());
        using var prober = new LineProber(fake);
        var health = new LineHealth();

        var r = await prober.PickAsync(Stream("a.com", "b.com", "c.com"), 0, false, health);

        Assert.Equal(1, r.Line);
        Assert.Equal(2, r.Probed);
        Assert.True(health.IsBad("a.com", DateTimeOffset.UtcNow));
        Assert.False(health.IsBad("b.com", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 都探不通时回到起始线路交给mpv再试()
    {
        var fake = new FakeHandler(_ => throw new HttpRequestException("连不上"));
        using var prober = new LineProber(fake);

        var r = await prober.PickAsync(Stream("a.com", "b.com", "c.com"), 0, false, new LineHealth());

        // 探测本身也可能不准（例如只拦了 Range 请求），不能因此放弃播放
        Assert.Equal(0, r.Line);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task 最多只探三条()
    {
        var fake = new FakeHandler(_ => throw new HttpRequestException("连不上"));
        using var prober = new LineProber(fake);

        await prober.PickAsync(Stream("a.com", "b.com", "c.com", "d.com", "e.com"), 0, false, new LineHealth());

        Assert.Equal(3, fake.Requested.Count);
    }

    [Fact]
    public async Task 从指定线路开始并回绕()
    {
        var fake = new FakeHandler(h => h == "a.com" ? Ok() : throw new HttpRequestException("连不上"));
        using var prober = new LineProber(fake);

        // 从最后一条开始，b、c 不通，绕回 a
        var r = await prober.PickAsync(Stream("a.com", "b.com", "c.com"), 1, false, new LineHealth());

        Assert.Equal(0, r.Line);
        Assert.Equal(["b.com", "c.com", "a.com"], fake.Requested);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task 地址失效的状态码算失败(HttpStatusCode code)
    {
        var fake = new FakeHandler(h => h == "a.com" ? Code(code) : Ok());
        using var prober = new LineProber(fake);

        var r = await prober.PickAsync(Stream("a.com", "b.com"), 0, false, new LineHealth());

        Assert.Equal(1, r.Line);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 服务器有回应就算主机可达(HttpStatusCode code)
    {
        // 重点是确认主机可达、顺便焐热 DNS，不是校验内容
        var fake = new FakeHandler(_ => Code(code));
        using var prober = new LineProber(fake);

        var r = await prober.PickAsync(Stream("a.com", "b.com"), 0, false, new LineHealth());

        Assert.Equal(0, r.Line);
        Assert.Single(fake.Requested);
    }

    [Fact]
    public async Task 超时算失败()
    {
        var fake = new FakeHandler(_ => Ok());
        fake.Delays["a.com"] = TimeSpan.FromSeconds(5);
        using var prober = new LineProber(fake) { Timeout = TimeSpan.FromMilliseconds(80) };

        var r = await prober.PickAsync(Stream("a.com", "b.com"), 0, false, new LineHealth());

        Assert.Equal(1, r.Line);
        Assert.Contains("连不上", r.Error ?? "");
    }

    [Fact]
    public async Task 刚探通过的主机不再重复探测()
    {
        var fake = new FakeHandler(_ => Ok());
        using var prober = new LineProber(fake);
        var health = new LineHealth();
        var stream = Stream("a.com", "b.com");

        await prober.PickAsync(stream, 0, false, health);
        Assert.Single(fake.Requested);

        // 连着听歌时后面几首都在同一个 CDN 上，不该每首都多付一次探测往返
        var again = await prober.PickAsync(stream, 0, false, health);
        Assert.Equal(0, again.Line);
        Assert.Equal(0, again.Probed);
        Assert.Single(fake.Requested);
    }

    [Fact]
    public async Task 探通的记录会被后来的失败清掉()
    {
        var fail = false;
        var fake = new FakeHandler(h => h == "a.com" && fail ? throw new HttpRequestException("连不上") : Ok());
        using var prober = new LineProber(fake);
        var health = new LineHealth(goodTtl: TimeSpan.Zero);
        var stream = Stream("a.com", "b.com");

        await prober.PickAsync(stream, 0, false, health);
        fail = true;
        var r = await prober.PickAsync(stream, 0, false, health);

        Assert.Equal(1, r.Line);
        Assert.True(health.IsBad("a.com", DateTimeOffset.UtcNow));
        Assert.False(health.IsRecentlyGood("a.com", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 纯音频模式探音频地址()
    {
        // 要有两条以上线路才会触发探测
        var video = new[] { "https://v1.com/x.m4s", "https://v2.com/x.m4s" };
        var audio = new[] { "https://a1.com/x.m4s", "https://a2.com/x.m4s" };
        var stream = new ResolvedStream(video[0], audio[0], new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(30), "测试", null, video, audio);
        var fake = new FakeHandler(_ => Ok());
        using var prober = new LineProber(fake);

        await prober.PickAsync(stream, 0, audioOnly: true, new LineHealth());

        Assert.Equal(["a1.com"], fake.Requested);
    }
}

public class MirrorListTests
{
    [Fact]
    public void 镜像列表里没有已知的死域名()
    {
        // upos-sz-mirror08c.bilivideo.com 已经不存在，日志里 46 次 DNS 失败全是它
        Assert.DoesNotContain("upos-sz-mirror08c.bilivideo.com", VupPlayer.Bilibili.StreamPreferences.Mirrors);
    }

    [Fact]
    public void 镜像都是可替换的upos域名()
    {
        Assert.All(VupPlayer.Bilibili.StreamPreferences.Mirrors, m =>
        {
            Assert.StartsWith("upos-", m);
            Assert.EndsWith(".bilivideo.com", m);
        });
    }
}
