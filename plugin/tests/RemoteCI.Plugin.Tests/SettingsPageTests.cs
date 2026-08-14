using Avalonia.Controls;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Views.SettingsPages;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SettingsPageTests
{
    [Fact]
    public void StandardSettings_DoesNotExposeConnectionDisableToggles()
    {
        var page = new RemoteCiSettingsPage(new PluginSettings());

        Assert.DoesNotContain("启用云端中转", CheckBoxLabels(page.Content));
        Assert.DoesNotContain("启用局域网直连服务", CheckBoxLabels(page.Content));
    }

    [Fact]
    public void DeveloperSettings_ExposesConnectionDisableToggles()
    {
        var page = new RemoteCiDeveloperSettingsPage(new PluginSettings { EnableCloud = false });

        Assert.Contains("启用云端中转", CheckBoxLabels(page.Content));
        Assert.Contains("启用局域网直连服务", CheckBoxLabels(page.Content));
    }

    [Theory]
    [InlineData("8765", "http://nas:8080", true, true)]
    [InlineData("0", "http://nas:8080", false, true)]
    [InlineData("70000", "http://nas:8080", false, true)]
    [InlineData("abc", "http://nas:8080", false, true)]
    [InlineData("8765", "nas:8080", true, false)]
    [InlineData("8765", "javascript:alert(1)", true, false)]
    [InlineData("8765", "", true, true)]
    [InlineData("8765", "https://ci.example.com", true, true)]
    public void ValidateInput_GuardsPortRangeAndUrlScheme(
        string portText, string urlText, bool expectedPortValid, bool expectedUrlValid)
    {
        var (portValid, urlValid) = RemoteCiSettingsPage.ValidateInput(portText, urlText);

        Assert.Equal(expectedPortValid, portValid);
        Assert.Equal(expectedUrlValid, urlValid);
    }

    private static IReadOnlyList<string> CheckBoxLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is CheckBox { Content: not null } checkBox)
                labels.Add(checkBox.Content.ToString()!);
            if (node is ContentControl contentControl) Visit(contentControl.Content);
            if (node is Panel panel)
            {
                foreach (var child in panel.Children) Visit(child);
            }
        }
    }
}
