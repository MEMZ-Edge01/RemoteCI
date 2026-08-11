using ClassIsland.Shared.Models.Notification;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class RemoteNotificationProviderTests
{
    [Fact]
    public void PerMessageOptionsOverrideEnabledProviderDefaults()
    {
        var providerSettings = new NotificationSettings
        {
            IsSettingsEnabled = true,
            IsNotificationEffectEnabled = false,
            IsNotificationSoundEnabled = false,
            IsSpeechEnabled = false,
        };

        var request = RemoteNotificationProvider.BuildNotificationRequest(
            providerSettings,
            "标题",
            "正文",
            isNotificationEffectEnabled: true,
            isNotificationSoundEnabled: true,
            isSpeechEnabled: true);

        Assert.False(providerSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsSettingsEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationEffectEnabled);
        Assert.True(request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.True(request.RequestNotificationSettings.IsSpeechEnabled);
        Assert.True(request.MaskContent.IsSpeechEnabled);
        Assert.True(request.OverlayContent!.IsSpeechEnabled);
    }
}
