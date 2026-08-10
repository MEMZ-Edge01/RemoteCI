using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemoteCI.Plugin.Settings;

/// <summary>
/// 插件配置模型，保存在 ClassIsland 插件配置目录的 Settings.json。
/// 修改任何属性后会自动触发保存（见 Plugin.cs）。
/// </summary>
public sealed class PluginSettings : INotifyPropertyChanged
{
    private bool _enableLanServer = true;
    private int _lanServerPort = 8765;
    private bool _enableCloud = true;
    private string _cloudServerUrl = "http://localhost:8080";
    private string _pairCode = "remoteci-demo";
    private string? _cloudToken;

    /// <summary>是否启用局域网直连服务（手表同 WiFi 直连插件）。</summary>
    public bool EnableLanServer
    {
        get => _enableLanServer;
        set => SetField(ref _enableLanServer, value);
    }

    /// <summary>局域网 WebSocket 服务端口。</summary>
    public int LanServerPort
    {
        get => _lanServerPort;
        set => SetField(ref _lanServerPort, value);
    }

    /// <summary>是否启用云端中转（经服务端与手表通信）。</summary>
    public bool EnableCloud
    {
        get => _enableCloud;
        set => SetField(ref _enableCloud, value);
    }

    /// <summary>云端服务端地址（如 http://nas:8080）。</summary>
    public string CloudServerUrl
    {
        get => _cloudServerUrl;
        set => SetField(ref _cloudServerUrl, value);
    }

    /// <summary>配对码：局域网与云端统一使用。</summary>
    public string PairCode
    {
        get => _pairCode;
        set => SetField(ref _pairCode, value);
    }

    /// <summary>云端配对后缓存的 token（服务端重启后失效，需重新配对）。</summary>
    public string? CloudToken
    {
        get => _cloudToken;
        set => SetField(ref _cloudToken, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
