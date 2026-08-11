using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared.Helpers;
using RemoteCI.Plugin.Settings;

namespace RemoteCI.Plugin.Views.SettingsPages;

/// <summary>
/// RemoteCI 插件设置页（代码构建 UI，避免引入 Avalonia XAML 编译依赖）。
/// 修改后需重启 ClassIsland 生效。
/// </summary>
[SettingsPageInfo("remoteci.plugin.settings", "RemoteCI 设置")]
public sealed class RemoteCiSettingsPage : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly CheckBox _lanCheck;
    private readonly TextBox _portBox;
    private readonly CheckBox _cloudCheck;
    private readonly TextBox _cloudUrlBox;
    private readonly TextBox _pairCodeBox;
    private readonly TextBlock _hint;

    public RemoteCiSettingsPage(PluginSettings settings)
    {
        _settings = settings;

        _lanCheck = new CheckBox { Content = "启用局域网直连服务", IsChecked = settings.EnableLanServer };
        _portBox = new TextBox { Text = settings.LanServerPort.ToString(), Watermark = "端口（默认 8765）" };
        _cloudCheck = new CheckBox { Content = "启用云端中转", IsChecked = settings.EnableCloud };
        _cloudUrlBox = new TextBox { Text = settings.CloudServerUrl, Watermark = "云端地址，如 http://nas:8080" };
        _pairCodeBox = new TextBox { Text = settings.PluginPairCode, Watermark = "WebUI 生成的一次性插件配对码" };

        var saveButton = new Button { Content = "保存设置" };
        saveButton.Click += OnSaveClick;
        _hint = new TextBlock { Text = string.Empty, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var panel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = "RemoteCI 课表手表联动", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = "把当前状态和七日课表推送到 Wear OS，支持按权限换课与发送通知。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _lanCheck,
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "局域网端口" }, _portBox },
                },
                _cloudCheck,
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "云端服务端地址" }, _cloudUrlBox },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "一次性插件配对码" }, _pairCodeBox },
                },
                saveButton,
                _hint,
            },
        };

        Content = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.EnableLanServer = _lanCheck.IsChecked == true;
        _settings.LanServerPort = int.TryParse(_portBox.Text, out var port) ? port : 8765;
        _settings.EnableCloud = _cloudCheck.IsChecked == true;
        _settings.CloudServerUrl = string.IsNullOrWhiteSpace(_cloudUrlBox.Text)
            ? "http://localhost:8080"
            : _cloudUrlBox.Text.Trim();
        _settings.PluginPairCode = _pairCodeBox.Text?.Trim() ?? string.Empty;

        if (Plugin.Current is { } plugin)
        {
            ConfigureFileHelper.SaveConfig(
                Path.Combine(plugin.PluginConfigFolder, "Settings.json"), _settings);
        }

        _hint.Text = "已保存。重启 ClassIsland 后生效。";
    }
}
