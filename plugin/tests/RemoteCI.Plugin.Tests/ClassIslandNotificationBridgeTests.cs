using ClassIsland.Core.Models.Notification;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ClassIslandNotificationBridgeTests
{
    [Fact]
    public void ClassifiesAutomationAndThirdPartyProvidersWithoutRelayingRemoteCiOrBuiltIns()
    {
        var hostAssembly = typeof(NotificationContent).Assembly;
        var pluginAssembly = typeof(ClassIslandNotificationBridgeTests).Assembly;

        Assert.Equal(
            ClassEventKind.AutomationNotification,
            ClassIslandNotificationBridge.ClassifyProvider(
                ClassIslandNotificationBridge.AutomationProviderGuid,
                hostAssembly,
                hostAssembly));
        Assert.Equal(
            ClassEventKind.PluginNotification,
            ClassIslandNotificationBridge.ClassifyProvider(Guid.NewGuid(), pluginAssembly, hostAssembly));
        Assert.Null(ClassIslandNotificationBridge.ClassifyProvider(Guid.NewGuid(), hostAssembly, hostAssembly));
        Assert.Null(ClassIslandNotificationBridge.ClassifyProvider(
            ClassIslandNotificationBridge.RemoteCiProviderGuid,
            pluginAssembly,
            hostAssembly));
    }

    [Fact]
    public void ExtractTextPrefersSpeechAndSupportsTemplateText()
    {
        var withSpeech = NotificationContent.CreateSimpleTextContent("正文");
        var withTemplate = new NotificationContent(new TemplateData { Text = "模板正文" });

        Assert.Equal("正文", ClassIslandNotificationBridge.ExtractText(withSpeech));
        Assert.Equal("模板正文", ClassIslandNotificationBridge.ExtractText(withTemplate));
    }

    private sealed class TemplateData
    {
        public string Text { get; init; } = string.Empty;
    }
}
