using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ControlModel(
    UserManager<AppUser> users,
    PeerRegistry peers,
    IStateStore store,
    IdentityCoordinator identities) : WebPageModel(users)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    [BindProperty]
    public int VolumeLevel { get; set; }

    [BindProperty]
    public NoticeInput Input { get; set; } = new();

    [BindProperty]
    public bool ForceSenderInTitle { get; set; }

    [BindProperty]
    public List<ExtensionInput> ExtensionInputs { get; set; } = [];

    public bool PluginOnline => peers.HasPlugin;
    public ClassStateSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<ExtensionDefinition> VisibleExtensions { get; private set; } = [];
    public bool CanTeacherComing => Permissions.HasFlag(UserPermissions.TeacherComing);
    public bool CanSendNotifications => Permissions.HasFlag(UserPermissions.SendNotifications);
    public bool CanControlSystem => Permissions.HasFlag(UserPermissions.SystemControl);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } denied) return denied;
        return Page();
    }

    public async Task<IActionResult> OnPostTeacherComingAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.TeacherComing) is { } denied) return denied;
        return RedirectWithResult(await SendAsync(
            new CommandMessage { Command = CommandKind.TeacherComing }, ct));
    }

    public async Task<IActionResult> OnPostNotificationAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        if (!ModelState.IsValid)
        {
            if (await LoadAsync() is { } loadDenied) return loadDenied;
            return Page();
        }

        var result = await SendAsync(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest
            {
                Title = string.IsNullOrWhiteSpace(Input.Title) ? "RemoteCI 通知" : Input.Title.Trim(),
                Message = Input.Message?.Trim() ?? string.Empty,
                ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct),
            },
        }, ct);
        return RedirectWithResult(result);
    }

    public async Task<IActionResult> OnPostNotificationSettingsAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        var settings = await identities.SetForceSenderInTitleAsync(ForceSenderInTitle, ct);
        await peers.SendSettingsToWatchesAsync(settings, ct);
        TempData["Message"] = ForceSenderInTitle ? "已开启强制显示发送人" : "已关闭强制显示发送人";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearNotificationsAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        return RedirectWithResult(await SendAsync(
            new CommandMessage { Command = CommandKind.ClearNotifications }, ct));
    }

    public async Task<IActionResult> OnPostMainMenuAsync(bool visible, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SystemControl) is { } denied) return denied;
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.SetMainMenuVisibility,
            MainMenuVisible = visible,
        }, ct));
    }

    public async Task<IActionResult> OnPostVolumeAsync(bool unmute, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SystemControl) is { } denied) return denied;
        if (VolumeLevel is < 0 or > 100)
            return VolumeResult(CommandResult.Failure(
                CommandResultCodes.InvalidRequest, "音量必须在 0 到 100 之间"));

        var result = await SendAsync(new CommandMessage
        {
            Command = CommandKind.Volume,
            // 静音状态下向高调节时，把取消静音与音量变更合并为同一条插件命令。
            Volume = CreateVolumeRequest(VolumeLevel, unmute),
        }, ct);
        return VolumeResult(result, unmute);
    }

    public async Task<IActionResult> OnPostMuteAsync(bool muted, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SystemControl) is { } denied) return denied;
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.Volume,
            Volume = new VolumeControlRequest { Muted = muted },
        }, ct));
    }

    public async Task<IActionResult> OnPostPowerAsync(PowerActionKind action, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SystemControl) is { } denied) return denied;
        if (!Enum.IsDefined(action))
        {
            TempData["Error"] = "未知电源操作";
            return RedirectToPage();
        }
        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.Power,
            PowerAction = action,
        }, ct));
    }

    public async Task<IActionResult> OnPostExtensionAsync(string extensionId, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        var definition = store.GetLatestExtensions()?.FirstOrDefault(extension =>
            string.Equals(extension.Id, extensionId, StringComparison.Ordinal));
        if (definition is null)
        {
            TempData["Error"] = "扩展功能不存在或尚未同步";
            return RedirectToPage();
        }
        if (!Permissions.HasFlag(definition.RequiredPermission)) return RedirectToPage("/Denied");

        var submitted = ExtensionInputs
            .Where(input => !string.IsNullOrWhiteSpace(input.Key))
            .GroupBy(input => input.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var args = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters ?? [])
        {
            submitted.TryGetValue(parameter.Key, out var value);
            value ??= parameter.Type == ExtensionParameterType.Switch
                ? (string.Equals(parameter.DefaultValue, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false")
                : parameter.DefaultValue;
            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                TempData["Error"] = $"请填写“{parameter.Label}”";
                return RedirectToPage();
            }
            args[parameter.Key] = value;
        }

        return RedirectWithResult(await SendAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = definition.Id,
            ExtensionArgs = args,
        }, ct));
    }

    private async Task<IActionResult?> LoadAsync()
    {
        if (await RequireAsync() is { } denied) return denied;
        Snapshot = store.GetLatestSnapshot();
        VolumeLevel = Snapshot?.VolumePercent ?? 0;
        if (CanSendNotifications) ForceSenderInTitle = await identities.GetForceSenderInTitleAsync();
        VisibleExtensions = (store.GetLatestExtensions() ?? [])
            .Where(extension => Permissions.HasFlag(extension.RequiredPermission))
            .ToArray();
        return !CanTeacherComing && !CanSendNotifications && !CanControlSystem && VisibleExtensions.Count == 0
            ? RedirectToPage("/Denied")
            : null;
    }

    internal static VolumeControlRequest CreateVolumeRequest(int level, bool unmute) => new()
    {
        Level = level,
        Muted = unmute ? false : null,
    };

    private async Task<CommandResult> SendAsync(CommandMessage command, CancellationToken ct)
    {
        command.RequestedBy = new UserProfile
        {
            Id = CurrentUser.Id,
            Username = CurrentUser.UserName!,
            DisplayName = CurrentUser.DisplayName,
            Role = CurrentUser.Role,
            GrantedPermissions = CurrentUser.GrantedPermissions,
            Permissions = Permissions,
            Version = CurrentUser.Version,
        };
        return await peers.SendCommandAndWaitAsync(command, CommandTimeout, ct);
    }

    private IActionResult VolumeResult(CommandResult result, bool unmuted = false)
    {
        if (!string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            return RedirectWithResult(result);
        return new JsonResult(new
        {
            success = result.Success,
            message = result.Message,
            volumeLevel = VolumeLevel,
            unmuted = result.Success && unmuted,
        });
    }

    private IActionResult RedirectWithResult(CommandResult result, string? successMessage = null)
    {
        TempData[result.Success ? "Message" : "Error"] = result.Success && !string.IsNullOrWhiteSpace(successMessage)
            ? successMessage
            : result.Message;
        return RedirectToPage();
    }

    public sealed class NoticeInput
    {
        [StringLength(60)] public string? Title { get; set; }
        [StringLength(500)] public string? Message { get; set; }
    }

    public sealed class ExtensionInput
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
