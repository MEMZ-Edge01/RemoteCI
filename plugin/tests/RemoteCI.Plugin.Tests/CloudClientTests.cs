using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CloudClientTests
{
    private sealed class FakeSocket : ICloudSocket
    {
        public WebSocketState State { get; set; } = WebSocketState.Open;
        public WebSocketCloseStatus? CloseStatus => null;
        public TimeSpan KeepAliveInterval { get; set; }
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public WebSocketReceiveResult? NextReceive { get; set; }
        public Exception? ReceiveError { get; set; }
        public Exception? SendError { get; set; }
        public TimeSpan SendDelay { get; set; }

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectCount++;
            return Task.CompletedTask;
        }

        public async Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            if (SendError is not null) throw SendError;
            if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, ct);
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct)
        {
            if (ReceiveError is not null) throw ReceiveError;
            if (NextReceive is not null) return NextReceive;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // 模拟健康空闲连接，阻塞到取消。
            throw new OperationCanceledException(ct);
        }

        public void Dispose()
        {
            DisposeCount++;
            State = WebSocketState.Closed;
        }
    }

    private static (CloudClient Client, List<FakeSocket> Sockets) Create(
        PluginSettings? settings = null,
        TimeSpan? reconnectDelay = null,
        CloudTokenStore? tokenStore = null,
        HttpMessageHandler? httpHandler = null,
        params FakeSocket[] preSeeded)
    {
        var sockets = new List<FakeSocket>(preSeeded);
        var next = 0;
        var mirror = new AccountMirror(Path.Combine(
            Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json"));
        var client = new CloudClient(
            settings ?? new PluginSettings { CloudToken = "test-token", EnableCloud = true },
            mirror,
            commands: null,
            requestFreshSchedule: () => { },
            NullLogger<CloudClient>.Instance,
            tokenStore: tokenStore,
            socketFactory: () =>
            {
                if (next < sockets.Count) return sockets[next++];
                var fresh = new FakeSocket();
                sockets.Add(fresh);
                next++;
                return fresh;
            },
            httpHandler: httpHandler,
            reconnectDelay: reconnectDelay ?? TimeSpan.FromMilliseconds(20));
        return (client, sockets);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("条件在超时前未满足");
    }

    [Fact]
    public async Task ReceiveFailure_ReleasesSocketAndReconnects()
    {
        var failing = new FakeSocket { ReceiveError = new WebSocketException("connection lost") };
        var (client, sockets) = Create(preSeeded: failing);
        using var cts = new CancellationTokenSource();
        try
        {
            _ = client.StartAsync(cts.Token);

            // 第一个连接接收失败后应释放并自动重连到第二个连接。
            await WaitUntilAsync(
                () => sockets.Count >= 2 && sockets[1].ConnectCount == 1 && failing.DisposeCount >= 1, 2000);

            Assert.True(sockets[0].KeepAliveInterval > TimeSpan.Zero, "每个连接都应配置 KeepAlive");
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
        }
    }

    [Fact]
    public async Task PolicyViolationClose_ClearsLongTermCredential()
    {
        var settings = new PluginSettings { CloudToken = "doomed-token", EnableCloud = true };
        var policy = new FakeSocket
        {
            NextReceive = new WebSocketReceiveResult(
                0, WebSocketMessageType.Close, true, WebSocketCloseStatus.PolicyViolation, "bye"),
        };
        var storePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(storePath);
        store.Save("doomed-token");
        var (client, _) = Create(settings, tokenStore: store, preSeeded: policy);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(() => settings.CloudToken is null, 2000);
            // 吊销后加密存储也同步删除。
            Assert.False(File.Exists(storePath));
        }
        finally
        {
            client.Dispose();
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }

    [Fact]
    public async Task SendFailureOnOpenSocket_DisposesSocketAndReconnects()
    {
        var failing = new FakeSocket { SendError = new WebSocketException("send failed") };
        var (client, sockets) = Create(preSeeded: failing);
        try
        {
            _ = client.StartAsync();
            // 连接建立后立即发送网络信息失败 → 释放该 socket → 外层重连到新连接。
            await WaitUntilAsync(() => failing.DisposeCount >= 1 && sockets.Count >= 2, 2000);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task MissingCredentials_DoesNotAttemptConnection()
    {
        var settings = new PluginSettings { EnableCloud = true }; // 无 token、无配对码。
        var (client, sockets) = Create(settings);
        try
        {
            _ = client.StartAsync();
            await Task.Delay(150);
            Assert.Empty(sockets);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentSendsDuringDispose_DoNotThrow()
    {
        // 慢速 socket 制造在途发送窗口;Dispose 与并发发送交错时不得抛 ObjectDisposedException。
        var socket = new FakeSocket { SendDelay = TimeSpan.FromMilliseconds(5) };
        var (client, _) = Create(preSeeded: socket);
        var snapshot = new ClassStateSnapshot();
        var sends = Enumerable.Range(0, 30)
            .Select(_ => client.SendStateAsync(snapshot))
            .ToArray();
        await Task.Delay(2);
        client.Dispose();
        await Task.WhenAll(sends); // 全部完成即通过（在途发送不抛异常）。
    }

    [Fact]
    public async Task PairingRequest_IssuesAndPersistsLongTermCredential()
    {
        var settings = new PluginSettings { PluginPairCode = "pair-code", EnableCloud = true }; // 无长期凭据。
        var storePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(storePath);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"paired-token","role":"plugin"}""", Encoding.UTF8, "application/json"),
        });
        var (client, _) = Create(settings, tokenStore: store, httpHandler: handler, preSeeded: new FakeSocket());
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => settings.CloudToken == "paired-token" && string.IsNullOrEmpty(settings.PluginPairCode), 3000);

            // 凭据经 Token 访问器同步写入加密存储。
            Assert.Equal("paired-token", store.Load());
        }
        finally
        {
            client.Dispose();
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }

    [Fact]
    public async Task PairingRejection_KeepsPairCodeAndDoesNotInventCredential()
    {
        var settings = new PluginSettings { PluginPairCode = "expired-code", EnableCloud = true };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        var (client, _) = Create(settings, httpHandler: handler);
        try
        {
            _ = client.StartAsync();
            await Task.Delay(150);

            // 409 视为凭据失效：不签发 token，也不清空用户重新填写的配对码。
            Assert.Null(settings.CloudToken);
            Assert.Equal("expired-code", settings.PluginPairCode);
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }
}
