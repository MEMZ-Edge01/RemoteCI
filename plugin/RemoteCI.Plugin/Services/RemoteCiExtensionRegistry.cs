using RemoteCI.Plugin.Extensions;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// <see cref="IRemoteCiExtensionRegistry"/> 的线程安全实现。
/// 注册发生在 ClassIsland 插件加载阶段，广播由 RemoteCiService 订阅 ExtensionsChanged 完成。
/// </summary>
internal sealed class RemoteCiExtensionRegistry : IRemoteCiExtensionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IRemoteCiExtension> _extensions = new(StringComparer.Ordinal);

    public event EventHandler? ExtensionsChanged;

    public IReadOnlyList<IRemoteCiExtension> GetExtensions()
    {
        lock (_lock)
        {
            return _extensions.Values.ToList();
        }
    }

    public void Register(IRemoteCiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (string.IsNullOrWhiteSpace(extension.Id))
            throw new ArgumentException("扩展 Id 不能为空", nameof(extension));

        lock (_lock)
        {
            if (!_extensions.TryAdd(extension.Id, extension))
                throw new InvalidOperationException($"扩展 Id 已存在：{extension.Id}");
        }
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _extensions.Remove(id);
        }
        if (removed) ExtensionsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}
