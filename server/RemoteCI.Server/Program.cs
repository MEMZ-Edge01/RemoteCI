using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

if (await UpdateInstaller.TryRunAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configured = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.DatabasePath;
    var environment = services.GetRequiredService<IHostEnvironment>();
    var path = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    options.UseSqlite($"Data Source={path}");
});
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "RemoteCI.Web";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/Denied";
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages(options => options.Conventions.AllowAnonymousToPage("/Login"));
builder.Services.AddScoped<IdentityCoordinator>();
builder.Services.AddScoped<SchedulePullSettings>();
builder.Services.AddSingleton<IStateStore, StateStore>();
builder.Services.AddSingleton<PeerRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SchedulePullWorker>();
builder.Services.AddSingleton(new UpdateService(args));

var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().BootstrapAsync();

app.Map("/ws", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketHub");
    await WebSocketHub.HandleAsync(
        context,
        context.RequestServices.GetRequiredService<IdentityCoordinator>(),
        context.RequestServices.GetRequiredService<PeerRegistry>(),
        context.RequestServices.GetRequiredService<IStateStore>(),
        logger);
});

app.MapPost("/api/plugin/pair", async (PairRequest request, IdentityCoordinator identities, CancellationToken ct) =>
{
    if (!string.Equals(request.Role, "plugin", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "此端点仅用于插件配对"));
    try { return Results.Ok(await identities.PairPluginAsync(request, ct)); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapPost("/api/auth/login", async (
    LoginRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    try
    {
        var response = await identities.LoginAsync(request, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.Ok(response);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapPost("/api/auth/refresh", async (
    RefreshSessionRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    try
    {
        var response = await identities.RefreshAsync(request, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.Ok(response);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapPost("/api/auth/logout", async (
    HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null || principal.DeviceSessionId is null) return Unauthorized();
    await identities.RevokeSessionAsync(principal.User.Id, principal.DeviceSessionId.Value, ct);
    await SyncAccountsAsync(identities, peers, ct);
    return Results.NoContent();
});

app.MapGet("/api/me", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
    await AuthorizeAsync(ctx, identities, ct) is { User: not null } principal
        ? Results.Ok(principal.User)
        : Unauthorized());

app.MapPost("/api/me/password", async (
    HttpContext ctx, ChangePasswordRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    try
    {
        await identities.ChangePasswordAsync(principal.User.Id, request, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/me/sessions", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    return principal?.User is null
        ? Unauthorized()
        : Results.Ok(await identities.ListSessionsAsync(principal.User.Id, principal.DeviceSessionId, ct));
});

app.MapDelete("/api/me/sessions/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    try
    {
        await identities.RevokeSessionAsync(principal.User.Id, id, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/state", async (HttpContext ctx, IdentityCoordinator identities, IStateStore store, CancellationToken ct) =>
{
    if (await AuthorizeAsync(ctx, identities, ct) is null) return Unauthorized();
    return store.GetLatestSnapshot() is { } snapshot
        ? Results.Ok(snapshot)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无课程状态"), statusCode: StatusCodes.Status404NotFound);
});

app.MapGet("/api/schedule", async (HttpContext ctx, IdentityCoordinator identities, IStateStore store, CancellationToken ct) =>
{
    if (await AuthorizeAsync(ctx, identities, ct) is null) return Unauthorized();
    return store.GetLatestSchedule() is { } schedule
        ? Results.Ok(schedule)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无七日课表"), statusCode: StatusCodes.Status404NotFound);
});

app.MapPost("/api/commands", async (
    HttpContext ctx, CommandMessage command, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    var required = CommandPermissions.Required(command.Command);
    if (required == UserPermissions.None) return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "未知命令"));
    if (!principal.User.Permissions.HasFlag(required)) return Forbidden();
    command.RequestedBy = principal.User;
    var result = await peers.SendCommandAndWaitAsync(command, TimeSpan.FromSeconds(15), ct);
    return Results.Json(result, statusCode: CommandStatus(result));
});

var usersApi = app.MapGroup("/api/users");
usersApi.MapGet("/", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    return HasPermission(principal, UserPermissions.ManageUsers)
        ? Results.Ok(await identities.ListUsersAsync(ct))
        : Forbidden();
});
usersApi.MapPost("/", async (
    HttpContext ctx, CreateUserRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    try
    {
        var created = await identities.CreateUserAsync(request, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.Created($"/api/users/{created.Id}", created);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapPut("/{id:guid}", async (
    Guid id, HttpContext ctx, UpdateUserRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    try
    {
        var updated = await identities.UpdateUserAsync(id, request, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.Ok(updated);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapPost("/{id:guid}/password", async (
    Guid id, HttpContext ctx, ResetPasswordRequest request, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    try
    {
        await identities.ResetPasswordAsync(id, request.Password, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapDelete("/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    try
    {
        await identities.DeleteUserAsync(id, ct);
        await SyncAccountsAsync(identities, peers, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapPost("/api/plugin/pairing-code", async (
    HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    return HasPermission(principal, UserPermissions.ManageUsers)
        ? Results.Ok(new { pairCode = await identities.CreatePluginPairingCodeAsync(ct) })
        : Forbidden();
});

app.MapGet("/api/admin/status", async (
    HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (!HasPermission(principal, UserPermissions.AccessWebUi)) return Forbidden();
    return Results.Ok(new
    {
        pluginOnline = peers.HasPlugin,
        pluginConnections = peers.PluginCount,
        watchConnections = peers.WatchCount,
        accountCount = (await identities.ListUsersAsync(ct)).Count,
        latestStateAt = store.GetLatestSnapshot()?.GeneratedAt,
        latestScheduleAt = store.GetLatestSchedule()?.GeneratedAt,
        protocolVersion = Protocol.Version,
    });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocolVersion = Protocol.Version }));
app.MapRazorPages();
app.Run();

static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)) return null;
    return await identities.ValidateAccessTokenAsync(header[(Protocol.BearerScheme.Length + 1)..].Trim(), ct);
}

static bool HasPermission(AuthPrincipal? principal, UserPermissions permission) =>
    principal?.User?.Permissions.HasFlag(permission) == true;

static async Task SyncAccountsAsync(IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct)
{
    await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
    await peers.RefreshWatchAuthorizationsAsync(ct);
}

static int CommandStatus(CommandResult result) => result.Code switch
{
    CommandResultCodes.PluginOffline => StatusCodes.Status503ServiceUnavailable,
    CommandResultCodes.Timeout => StatusCodes.Status504GatewayTimeout,
    CommandResultCodes.Forbidden => StatusCodes.Status403Forbidden,
    CommandResultCodes.InvalidRequest => StatusCodes.Status400BadRequest,
    CommandResultCodes.ScheduleStale => StatusCodes.Status409Conflict,
    _ => result.Success ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity,
};

static IResult Unauthorized() => Results.Json(
    Error(ApiErrorCodes.Unauthorized, "未登录或登录已失效"), statusCode: StatusCodes.Status401Unauthorized);
static IResult Forbidden() => Results.Json(
    Error(ApiErrorCodes.Forbidden, "权限不足"), statusCode: StatusCodes.Status403Forbidden);
static IResult OperationError(IdentityOperationException ex) => Results.Json(
    Error(ex.Code, ex.Message), statusCode: ex.Code switch
    {
        ApiErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ApiErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ApiErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ApiErrorCodes.PairCodeInvalid => StatusCodes.Status409Conflict,
        "USERNAME_EXISTS" => StatusCodes.Status409Conflict,
        "LAST_ADMIN" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    });
static ApiError Error(string code, string message) => new() { Code = code, Message = message };

public partial class Program;
