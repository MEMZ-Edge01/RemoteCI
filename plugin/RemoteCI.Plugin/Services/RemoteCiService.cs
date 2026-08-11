using Microsoft.Extensions.Logging;
using Avalonia.Threading;
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
    private readonly AccountMirror _accounts;
    private readonly ILoggerFactory _loggerFactory;
    private LanServer? _lanServer;
    private CloudClient? _cloudClient;
    private CancellationTokenSource? _cts;
    private ClassStateSnapshot? _latestSnapshot;
    private ScheduleBundle? _latestSchedule;

    public RemoteCiService(
        StateCollector collector,
        CommandHandler commandHandler,
        PluginSettings settings,
        AccountMirror accounts,
        ILoggerFactory loggerFactory)
    {
        _collector = collector;
        _commandHandler = commandHandler;
        _settings = settings;
        _accounts = accounts;
        _loggerFactory = loggerFactory;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        _collector.SnapshotPushed += OnSnapshotPushed;
        _collector.SchedulePushed += OnSchedulePushed;
        _collector.EventOccurred += OnEventOccurred;
        _commandHandler.NotificationSent += OnEventOccurred;
        _commandHandler.ScheduleChanged += OnScheduleChanged;
        _commandHandler.HostStateChanged += OnHostStateChanged;

        if (_settings.EnableLanServer)
        {
            _lanServer = new LanServer(
                _settings,
                _accounts,
                _commandHandler,
                () => _latestSnapshot,
                () => _latestSchedule,
                _loggerFactory.CreateLogger<LanServer>());
            _lanServer.Start();
        }

        if (_settings.EnableCloud)
        {
            _cloudClient = new CloudClient(_settings, _accounts, _commandHandler, _loggerFactory.CreateLogger<CloudClient>());
            _ = _cloudClient.StartAsync(_cts.Token);
        }

        _collector.Start();
    }

    public void Stop()
    {
        _collector.Stop();
        _collector.SnapshotPushed -= OnSnapshotPushed;
        _collector.SchedulePushed -= OnSchedulePushed;
        _collector.EventOccurred -= OnEventOccurred;
        _commandHandler.NotificationSent -= OnEventOccurred;
        _commandHandler.ScheduleChanged -= OnScheduleChanged;
        _commandHandler.HostStateChanged -= OnHostStateChanged;
        _cts?.Cancel();
        _cloudClient?.Dispose();
        _cloudClient = null;
        _lanServer?.Dispose();
        _lanServer = null;
    }

    private void OnSnapshotPushed(ClassStateSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _lanServer?.BroadcastState(snapshot);
        if (_cloudClient is { } cloud)
        {
            _ = cloud.SendStateAsync(snapshot); // 异步发送，不阻塞收集线程
        }
    }

    private void OnSchedulePushed(ScheduleBundle schedule)
    {
        _latestSchedule = schedule;
        _lanServer?.BroadcastSchedule(schedule);
        if (_cloudClient is { } cloud)
            _ = cloud.SendScheduleAsync(schedule);
    }

    private void OnScheduleChanged() => Dispatcher.UIThread.Post(_collector.ForceSchedulePush);

    private void OnHostStateChanged() => Dispatcher.UIThread.Post(_collector.ForceSnapshotPush);

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
