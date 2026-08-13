using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using RemoteCI.Plugin.Settings;

namespace RemoteCI.Plugin.Views.SettingsPages;

/// <summary>集中放置可能破坏远程可用性的高级连接开关，避免普通设置页误操作。</summary>
[SettingsPageInfo("remoteci.plugin.developer", "RemoteCI 开发者设置")]
public sealed class RemoteCiDeveloperSettingsPage : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly CheckBox _cloudCheck;
    private readonly TextBlock _hint;

    public RemoteCiDeveloperSettingsPage(PluginSettings settings)
    {
        _settings = settings;
        _cloudCheck = new CheckBox
        {
            Content = "启用云端中转",
            IsChecked = settings.EnableCloud,
        };
        _hint = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var saveButton = new Button { Content = "保存开发者设置" };
        saveButton.Click += OnSaveClick;

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = "RemoteCI 开发者设置",
                        FontSize = 20,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = "关闭云端中转会使插件无法向 WebUI 同步状态、账号与课表。仅在调试局域网直连时使用。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    _cloudCheck,
                    saveButton,
                    _hint,
                },
            },
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.EnableCloud = _cloudCheck.IsChecked == true;
        SettingsPagePersistence.Save(_settings);
        _hint.Text = "已保存。重启 ClassIsland 后生效。";
    }
}
