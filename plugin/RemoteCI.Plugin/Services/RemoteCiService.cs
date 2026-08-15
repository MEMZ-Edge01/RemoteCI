using Microsoft.Extensions.Logging;
using Avalonia.Threading;
using RemoteCI.Plugin.Extensions;
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
    private readonly ClassIslandNotificationBridge _notificationBridge;
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CloudTokenStore _tokenStore;
    private readonly IRemoteCiExtensionRegistry _extensions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteCiService> _logger;
    private LanServer? _lanServer;
    private CloudClient? _cloudClient;
    private CancellationTokenSource? _cts;
    private ClassStateSnapshot? _latestSnapshot;
    private ScheduleBundle? _latestSchedule;

    public RemoteCiService(
        StateCollector collector,
        CommandHandler commandHandler,
        ClassIslandNotificationBridge notificationBridge,
        PluginSettings settings,
        AccountMirror accounts,
        CloudTokenStore tokenStore,
        IRemoteCiExtensionRegistry extensions,
        ILoggerFactory loggerFactory)
    {
        _collector = collector;
        _commandHandler = commandHandler;
        _notificationBridge = notificationBridge;
        _settings = settings;
        _accounts = accounts;
        _tokenStore = tokenStore;
        _extensions = extensions;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RemoteCiService>();
    }

    public void Start()
    {
        if (_cts is not null)
        {
            // 重入保护：重复调用（如插件重载竞态）不能重复订阅事件与重复监听端口。
            _logger.LogWarning("RemoteCI 服务已在运行，忽略重复的启动调用");
            return;
        }
        _cts = new CancellationTokenSource();
        // ACL 收紧失败从此处记 Warning（静态防护类无法自行获取日志管道）。
        FileProtection.Logger = _loggerFactory.CreateLogger("RemoteCI.FileProtection");

        _collector.SnapshotPushed += OnSnapshotPushed;
        _collector.SchedulePushed += OnSchedulePushed;
        _collector.EventOccurred += OnEventOccurred;
        _commandHandler.NotificationSent += OnEventOccurred;
        _commandHandler.ScheduleChanged += OnScheduleChanged;
        _commandHandler.HostStateChanged += OnHostStateChanged;
        _notificationBridge.NotificationCaptured += OnEventOccurred;
        _extensions.ExtensionsChanged += OnExtensionsChanged;
        _notificationBridge.Start();

        if (_settings.EnableLanServer)
        {
            _lanServer = new LanServer(
                _settings,
                _accounts,
                _commandHandler,
                RequestFreshSchedule,
                () => _latestSnapshot,
                () => _latestSchedule,
                _loggerFactory.CreateLogger<LanServer>());
            _lanServer.Start();
        }

        if (_settings.EnableCloud)
        {
            _cloudClient = new CloudClient(
                _settings,
                _accounts,
                _commandHandler,
                RequestFreshSchedule,
                _loggerFactory.CreateLogger<CloudClient>(),
                tokenStore: _tokenStore);
            _ = _cloudClient.StartAsync(_cts.Token);
        }

        _collector.Start();
        PublishExtensions(); // 注册表可能在连接建立前已就绪，启动时先推送一次当前快照。
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
        _notificationBridge.NotificationCaptured -= OnEventOccurred;
        _extensions.ExtensionsChanged -= OnExtensionsChanged;
        _notificationBridge.Stop();
        _cts?.Cancel();
        _commandHandler.CancelPendingPowerActions();
        _cloudClient?.Dispose();
        _cloudClient = null;
        _lanServer?.Dispose();
        _lanServer = null;
        // 置空释放重入标记，允许 Stop 后重新 Start；CTS 不紧跟 Dispose（令牌仍被
        // 后台任务引用），无原生资源交由 GC 回收。
        _cts = null;
    }

    private void OnSnapshotPushed(ClassStateSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _lanServer?.BroadcastState(snapshot);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendStateAsync(snapshot), "状态快照"); // 异步发送，不阻塞收集线程
        }
    }

    private void OnSchedulePushed(ScheduleBundle schedule)
    {
        _latestSchedule = schedule;
        _lanServer?.BroadcastSchedule(schedule);
        if (_cloudClient is { } cloud)
            Observe(cloud.SendScheduleAsync(schedule), "七日课表");
    }

    private void OnScheduleChanged() => Dispatcher.UIThread.Post(_collector.ForceSchedulePush);

    private void RequestFreshSchedule() => Dispatcher.UIThread.Post(_collector.RequestSchedulePush);

    private void OnHostStateChanged() => Dispatcher.UIThread.Post(_collector.ForceSnapshotPush);

    private void OnEventOccurred(ClassEvent @event)
    {
        _lanServer?.BroadcastEvent(@event);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendEventAsync(@event), "课程事件");
        }
    }

    private void OnExtensionsChanged(object? sender, EventArgs e) => PublishExtensions();

    private void PublishExtensions()
    {
        var definitions = _extensions.GetExtensions()
            .Select(ToDefinition)
            .ToList();
        _lanServer?.BroadcastExtensions(definitions);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendExtensionsAsync(definitions), "扩展清单");
        }
    }

    /// <summary>fire-and-forget 发送统一挂异常观察器，避免未观察异常在重连竞态下丢失。</summary>
    private void Observe(Task send, string what) =>
        _ = send.ContinueWith(
            task => _logger.LogDebug(task.Exception, "云端发送失败（{What}）", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static ExtensionDefinition ToDefinition(IRemoteCiExtension extension) => new()
    {
        Id = extension.Id,
        DisplayName = extension.DisplayName,
        Icon = extension.Icon,
        RequiredPermission = extension.RequiredPermission,
        Parameters = extension.Parameters.Count == 0 ? null : extension.Parameters.ToList(),
    };

    public void Dispose() => Stop();
}
