using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 编排器：连接状态收集、局域网服务与云端客户端，负责消息路由。
/// 后续新增传输通道（如未来 watchOS 直连）只需在此注册，不改协议。
/// </summary>
public sealed class RemoteCiService : IDisposable
{
    private readonly StateCollector _collector;
    private readonly CommandHandler _commandHandler;
    private readonly PluginSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private LanServer? _lanServer;
    private CloudClient? _cloudClient;
    private CancellationTokenSource? _cts;

    public RemoteCiService(
        StateCollector collector,
        CommandHandler commandHandler,
        PluginSettings settings,
        ILoggerFactory loggerFactory)
    {
        _collector = collector;
        _commandHandler = commandHandler;
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        _collector.SnapshotPushed += OnSnapshotPushed;
        _collector.EventOccurred += OnEventOccurred;

        if (_settings.EnableLanServer)
        {
            _lanServer = new LanServer(
                _settings,
                _commandHandler,
                () => _collector.BuildSnapshot(),
                _loggerFactory.CreateLogger<LanServer>());
            _lanServer.Start();
        }

        if (_settings.EnableCloud)
        {
            _cloudClient = new CloudClient(_settings, _commandHandler, _loggerFactory.CreateLogger<CloudClient>());
            _ = _cloudClient.StartAsync(_cts.Token);
        }

        _collector.Start();
    }

    public void Stop()
    {
        _collector.Stop();
        _collector.SnapshotPushed -= OnSnapshotPushed;
        _collector.EventOccurred -= OnEventOccurred;
        _cts?.Cancel();
        _cloudClient?.Dispose();
        _cloudClient = null;
        _lanServer?.Dispose();
        _lanServer = null;
    }

    private void OnSnapshotPushed(ClassStateSnapshot snapshot)
    {
        _lanServer?.BroadcastState(snapshot);
        if (_cloudClient is { } cloud)
        {
            _ = cloud.SendStateAsync(snapshot); // 异步发送，不阻塞收集线程
        }
    }

    private void OnEventOccurred(ClassEvent @event)
    {
        _lanServer?.BroadcastEvent(@event);
        if (_cloudClient is { } cloud)
        {
            _ = cloud.SendEventAsync(@event);
        }
    }

    public void Dispose() => Stop();
}
