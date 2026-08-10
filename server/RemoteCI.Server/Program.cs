using RemoteCI.Server;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IStateStore, StateStore>();
builder.Services.AddSingleton<PeerRegistry>();
builder.Services.AddCors(options =>
{
    // 预留：未来如提供 Web 管理页可放开；手表/插件为原生客户端，不受浏览器 CORS 限制。
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// ── WebSocket 中转 ──────────────────────────────────────────────
app.Map("/ws", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WebSocketHub");
    await WebSocketHub.HandleAsync(
        context,
        context.RequestServices.GetRequiredService<ITokenService>(),
        context.RequestServices.GetRequiredService<PeerRegistry>(),
        context.RequestServices.GetRequiredService<IStateStore>(),
        logger);
});

// ── REST API ────────────────────────────────────────────────────
app.MapPost("/api/pair", (PairRequest request, ITokenService tokens) =>
{
    if (!TryParseRole(request.Role, out var role))
    {
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "role 必须是 plugin 或 watch"));
    }

    try
    {
        return Results.Ok(tokens.Pair(request.PairCode, role));
    }
    catch (InvalidPairCodeException)
    {
        return Results.Json(
            Error(ApiErrorCodes.PairCodeInvalid, "配对码错误"),
            statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapGet("/api/state", (HttpContext ctx, ITokenService tokens, IStateStore store) =>
{
    if (!TryAuthorize(ctx, tokens, out _))
    {
        return Results.Json(
            Error(ApiErrorCodes.Unauthorized, "未认证或 token 无效"),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var snapshot = store.GetLatestSnapshot();
    return snapshot is null
        ? Results.Json(Error(ApiErrorCodes.NotFound, "尚无课表状态"), statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(snapshot);
});

app.MapPost("/api/commands", async (
    HttpContext ctx,
    CommandMessage command,
    ITokenService tokens,
    PeerRegistry registry) =>
{
    if (!TryAuthorize(ctx, tokens, out _))
    {
        return Results.Json(
            Error(ApiErrorCodes.Unauthorized, "未认证或 token 无效"),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var envelope = Envelope.Command(command);
    return await registry.SendToPluginAsync(envelope)
        ? Results.Accepted()
        : Results.Json(
            Error("NO_PLUGIN_ONLINE", "插件未在线，指令未执行"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocolVersion = Protocol.Version }));

app.Run();

// ── 辅助 ────────────────────────────────────────────────────────

static bool TryParseRole(string? value, out PeerRole role)
{
    switch (value?.ToLowerInvariant())
    {
        case "plugin":
            role = PeerRole.Plugin;
            return true;
        case "watch":
            role = PeerRole.Watch;
            return true;
        default:
            role = default;
            return false;
    }
}

static bool TryAuthorize(HttpContext ctx, ITokenService tokens, out PeerRole role)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase))
    {
        return tokens.TryValidate(header[(Protocol.BearerScheme.Length + 1)..].Trim(), out role);
    }

    role = default;
    return false;
}

static ApiError Error(string code, string message) => new() { Code = code, Message = message };

/// <summary>便于集成测试引用入口类型。</summary>
public partial class Program;
