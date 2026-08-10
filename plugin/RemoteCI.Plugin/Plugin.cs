using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Views.SettingsPages;

namespace RemoteCI.Plugin;

/// <summary>
/// RemoteCI 插件入口。职责：加载/保存配置、注册服务与设置页、
/// 在应用启动完成后启动 RemoteCiService。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>当前插件实例（供设置页访问）。</summary>
    public static Plugin? Current { get; private set; }

    /// <summary>插件配置。</summary>
    public PluginSettings Settings { get; private set; } = new();

    private RemoteCiService? _service;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Current = this;

        // 加载配置；配置目录由 ClassIsland 提供，保存在插件安装目录之外，更新插件不会丢失。
        var settingsPath = Path.Combine(PluginConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);
        Settings.PropertyChanged += (_, _) => ConfigureFileHelper.SaveConfig(settingsPath, Settings);

        services.AddSingleton(Settings);
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<StateCollector>();
        services.AddSingleton<RemoteCiService>();
        services.AddSettingsPage<RemoteCiSettingsPage>();

        var app = AppBase.Current;
        app.AppStarted += (_, _) =>
        {
            _service = IAppHost.GetService<RemoteCiService>();
            _service.Start();
        };
        app.AppStopping += (_, _) => _service?.Stop();
    }
}
