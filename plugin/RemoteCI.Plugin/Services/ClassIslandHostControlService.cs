using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using RemoteCI.Shared;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 集中封装 ClassIsland 宿主控制与 Windows 电源操作。
/// ClassIsland 2.x 尚未为插件公开清除提醒和主界面显隐的写接口，相关兼容访问只保留在此处。
/// </summary>
public sealed class ClassIslandHostControlService(
    INotificationHostService notificationHost,
    IServiceProvider services,
    ILogger<ClassIslandHostControlService> logger)
{
    public bool IsNotificationPlaying => notificationHost.IsNotificationsPlaying;

    public bool IsMainMenuVisible => TryGetMainMenuVisibility(out var visible) ? visible : true;

    public bool IsSleepAvailable => OperatingSystem.IsWindows();

    public bool IsHibernateAvailable => OperatingSystem.IsWindows() && NativeMethods.IsPwrHibernateAllowed();

    public bool TryGetVolumeState(out int volumePercent, out bool muted)
    {
        volumePercent = 0;
        muted = false;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            volumePercent = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            muted = device.AudioEndpointVolume.Mute;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "无法读取 Windows 默认播放设备音量");
            return false;
        }
    }

    public void SetVolume(int? level, bool? muted)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("音量控制仅支持 Windows");
        if (level is null && muted is null)
            throw new ArgumentException("缺少音量或静音状态");
        if (level is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(level), "音量必须在 0-100 之间");

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (level is { } value) device.AudioEndpointVolume.MasterVolumeLevelScalar = value / 100f;
        if (muted is { } isMuted) device.AudioEndpointVolume.Mute = isMuted;
    }

    public async Task ClearNotificationsAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var method = notificationHost.GetType().GetMethod(
                "CancelAllNotifications",
                BindingFlags.Instance | BindingFlags.Public);
            if (method is null)
                throw new NotSupportedException("当前 ClassIsland 版本不支持由插件清除提醒");
            method.Invoke(notificationHost, null);
        });
    }

    public async Task SetMainMenuVisibilityAsync(bool visible)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!TryGetSettings(out var settings, out var visibilityProperty))
                throw new NotSupportedException("当前 ClassIsland 版本不支持由插件切换主界面");
            visibilityProperty.SetValue(settings, visible);
        });
    }

    public void SchedulePowerAction(PowerActionKind action)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("电源控制仅支持 Windows");
        if (action == PowerActionKind.Hibernate && !IsHibernateAvailable)
            throw new InvalidOperationException("Windows 未启用休眠");

        if (action is PowerActionKind.Shutdown or PowerActionKind.Restart)
        {
            var arguments = action == PowerActionKind.Shutdown ? "/s /t 1" : "/r /t 1";
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("无法启动 Windows 电源命令");
            return;
        }

        // 先留出回传命令结果的时间；睡眠后连接会立即中断，不能同步等待系统恢复。
        _ = Task.Run(async () =>
        {
            await Task.Delay(750);
            var succeeded = NativeMethods.SetSuspendState(
                action == PowerActionKind.Hibernate,
                force: false,
                disableWakeEvent: false);
            if (!succeeded)
                logger.LogError("Windows 电源操作 {Action} 失败，错误码 {Error}", action, Marshal.GetLastWin32Error());
        });
    }

    private bool TryGetMainMenuVisibility(out bool visible)
    {
        visible = true;
        if (!TryGetSettings(out var settings, out var visibilityProperty)) return false;
        visible = visibilityProperty.GetValue(settings) as bool? ?? true;
        return true;
    }

    private bool TryGetSettings(out object settings, out PropertyInfo visibilityProperty)
    {
        settings = null!;
        visibilityProperty = null!;
        var serviceType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(x => x.GetType("ClassIsland.Services.SettingsService", throwOnError: false))
            .FirstOrDefault(x => x is not null);
        if (serviceType is null || services.GetService(serviceType) is not { } settingsService) return false;
        settings = serviceType.GetProperty("Settings", BindingFlags.Instance | BindingFlags.Public)?.GetValue(settingsService)!;
        if (settings is null) return false;
        visibilityProperty = settings.GetType().GetProperty("IsMainWindowVisible", BindingFlags.Instance | BindingFlags.Public)!;
        return visibilityProperty is { CanRead: true, CanWrite: true };
    }

    private static class NativeMethods
    {
        [DllImport("PowrProf.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool IsPwrHibernateAllowed();

        [DllImport("PowrProf.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.U1)] bool hibernate,
            [MarshalAs(UnmanagedType.U1)] bool force,
            [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
    }
}
