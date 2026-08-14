using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;

namespace RemoteCI.Plugin.Services;

/// <summary>在固定 UDP 端口响应局域网扫描；实际 WebSocket 端口仍可由用户修改。</summary>
internal sealed class LanDiscoveryResponder(
    PluginSettings settings,
    ILogger logger) : IDisposable
{
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.LanDiscoveryPort));
            _ = ReceiveLoopAsync(_udp, _cts.Token);
            logger.LogInformation("局域网设备扫描已启动：UDP {Port}", Protocol.LanDiscoveryPort);
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "无法监听局域网扫描端口 UDP {Port}，仍可手动填写插件地址", Protocol.LanDiscoveryPort);
            Dispose();
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(ct);
                var response = LanDiscoveryProtocol.CreateResponse(packet.Buffer, settings, Environment.MachineName);
                if (response is null) continue;
                var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options);
                await udp.SendAsync(payload, packet.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (SocketException ex)
        {
            // 网卡热插拔或瞬时故障会中断监听；退避后自动重启，而不是永久失效。
            logger.LogWarning(ex, "局域网设备扫描监听已中断，10 秒后自动重启");
            await RestartAsync(ct);
        }
    }

    private async Task RestartAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            _udp?.Dispose();
            if (ct.IsCancellationRequested) return;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.LanDiscoveryPort));
            await ReceiveLoopAsync(_udp, ct);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                logger.LogWarning(ex, "局域网设备扫描重启失败，已停用");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }
}
