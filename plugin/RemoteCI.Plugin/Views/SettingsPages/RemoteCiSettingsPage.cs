using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
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
    private readonly TextBox _portBox;
    private readonly TextBox _cloudUrlBox;
    private readonly TextBox _pairCodeBox;
    private readonly TextBlock _httpWarning;
    private readonly TextBlock _hint;

    public RemoteCiSettingsPage(PluginSettings settings)
    {
        _settings = settings;

        _portBox = new TextBox { Text = settings.LanServerPort.ToString(), Watermark = "端口（默认 8765）" };
        _cloudUrlBox = new TextBox { Text = settings.CloudServerUrl, Watermark = "云端地址，如 https://nas:8080" };
        _pairCodeBox = new TextBox { Text = settings.PluginPairCode, Watermark = "WebUI 生成的一次性插件配对码" };

        // 明文 HTTP 会把配对码、密码与课表数据暴露给同网段任何设备，必须醒目提示。
        _httpWarning = new TextBlock
        {
            Text = "⚠ 当前为明文 HTTP 地址：配对码、密码与课表数据在网络中明文传输，可被同网段设备窃听。生产环境请改用 HTTPS。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            IsVisible = IsHttpUrl(settings.CloudServerUrl),
        };
        _cloudUrlBox.TextChanged += (_, _) => _httpWarning.IsVisible = IsHttpUrl(_cloudUrlBox.Text);

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
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "局域网端口" }, _portBox },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "云端服务端地址" }, _cloudUrlBox, _httpWarning },
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

    /// <summary>端口必须在 1-65535；云端地址留空或为合法 http/https URI 才有效。</summary>
    internal static (bool PortValid, bool UrlValid) ValidateInput(string? portText, string? urlText)
    {
        var portValid = int.TryParse(portText, out var port) && port is >= 1 and <= 65535;
        var url = urlText?.Trim() ?? string.Empty;
        // 必须给出可解析的 http/https 地址，否则 UriBuilder 会生成畸形目标导致连接永远失败。
        var urlValid = string.IsNullOrWhiteSpace(url) ||
            Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) &&
            parsedUrl.Scheme is "http" or "https";
        return (portValid, urlValid);
    }

    /// <summary>地址是否为明文 HTTP；非法或留空地址不触发警告（由保存校验拦截）。</summary>
    internal static bool IsHttpUrl(string? urlText)
    {
        var url = urlText?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == "http";
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var (portValid, urlValid) = ValidateInput(_portBox.Text, _cloudUrlBox.Text);
        var urlText = _cloudUrlBox.Text?.Trim() ?? string.Empty;
        if (!portValid || !urlValid)
        {
            _hint.Text = !portValid
                ? "端口必须是 1-65535 的数字，未保存。"
                : "云端地址必须是以 http:// 或 https:// 开头的完整地址，未保存。";
            return;
        }

        _settings.LanServerPort = int.Parse(_portBox.Text!.Trim());
        _settings.CloudServerUrl = string.IsNullOrWhiteSpace(urlText)
            ? "http://localhost:8080"
            : urlText;
        _settings.PluginPairCode = _pairCodeBox.Text?.Trim() ?? string.Empty;
        // 属性变更已由 Plugin.cs 的 PropertyChanged 订阅自动落盘，无需重复写 Settings.json。

        _hint.Text = "已保存。连接与端口设置在重启 ClassIsland 后生效，云端配对码即时生效。";
    }
}
