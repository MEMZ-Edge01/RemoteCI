using ClassIsland.Shared.Helpers;
using RemoteCI.Plugin.Settings;

namespace RemoteCI.Plugin.Views.SettingsPages;

internal static class SettingsPagePersistence
{
    public static void Save(PluginSettings settings)
    {
        if (Plugin.Current is not { } plugin) return;
        ConfigureFileHelper.SaveConfig(
            Path.Combine(plugin.PluginConfigFolder, "Settings.json"), settings);
    }
}
