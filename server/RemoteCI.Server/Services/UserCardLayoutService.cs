using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed partial class UserCardLayoutService(AppDbContext db)
{
    private const int MaxItems = 100;
    private const int MaxJsonLength = 32_768;
    private static readonly HashSet<string> SupportedPages = new(StringComparer.Ordinal)
    {
        "index", "control", "schedule", "users", "system-config", "account",
    };

    public async Task<CardLayoutDocument> GetAsync(Guid userId, string pageKey, CancellationToken ct = default)
    {
        pageKey = ValidatePageKey(pageKey);
        var json = await db.UserCardLayouts.AsNoTracking()
            .Where(layout => layout.UserId == userId && layout.PageKey == pageKey)
            .Select(layout => layout.LayoutJson)
            .SingleOrDefaultAsync(ct);
        return json is null
            ? CardLayoutDocument.Empty()
            : DeserializeAndValidate(json);
    }

    public async Task<CardLayoutDocument> SaveAsync(
        Guid userId,
        string pageKey,
        string json,
        CancellationToken ct = default)
    {
        pageKey = ValidatePageKey(pageKey);
        var document = ValidateStoredLayout(pageKey, json);
        var normalized = JsonSerializer.Serialize(document, JsonDefaults.Options);
        var layout = await db.UserCardLayouts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.PageKey == pageKey, ct);
        if (layout is null)
        {
            layout = new UserCardLayout { UserId = userId, PageKey = pageKey };
            db.UserCardLayouts.Add(layout);
        }
        layout.LayoutJson = normalized;
        layout.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return document;
    }

    public async Task ResetAsync(Guid userId, string pageKey, CancellationToken ct = default)
    {
        pageKey = ValidatePageKey(pageKey);
        await db.UserCardLayouts
            .Where(layout => layout.UserId == userId && layout.PageKey == pageKey)
            .ExecuteDeleteAsync(ct);
    }

    internal static CardLayoutDocument ValidateStoredLayout(string pageKey, string json)
    {
        ValidatePageKey(pageKey);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "布局数据无效或过大");
        return DeserializeAndValidate(json);
    }

    private static CardLayoutDocument DeserializeAndValidate(string json)
    {
        CardLayoutDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CardLayoutDocument>(json, JsonDefaults.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "布局数据格式无效");
        }

        if (document.Version != CardLayoutDocument.CurrentVersion || document.Items.Count > MaxItems)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "布局版本或卡片数量无效");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Items)
        {
            if (!LayoutTokenRegex().IsMatch(item.CardId) ||
                !LayoutTokenRegex().IsMatch(item.GroupId) ||
                item.Order is < 0 or >= MaxItems ||
                item.Span is < 1 or > 3 ||
                !keys.Add($"{item.GroupId}\n{item.CardId}") ||
                !orders.Add($"{item.GroupId}\n{item.Order}"))
            {
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "布局卡片数据无效");
            }
        }

        document.Items = document.Items
            .OrderBy(item => item.GroupId, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ToList();
        return document;
    }

    private static string ValidatePageKey(string value)
    {
        var pageKey = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedPages.Contains(pageKey))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "不支持的布局页面");
        return pageKey;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutTokenRegex();
}

public sealed class CardLayoutDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public List<CardLayoutItem> Items { get; set; } = [];
    public static CardLayoutDocument Empty() => new();
}

public sealed class CardLayoutItem
{
    public string CardId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public int Order { get; set; }
    public int Span { get; set; } = 1;
}
