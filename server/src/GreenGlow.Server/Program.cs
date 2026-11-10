using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using GreenGlow.Server.Macros;
using GreenGlow.Server.Admin;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Cluster;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using GreenGlow.Server.Printing;
using GreenGlow.Server.Relay;
using GreenGlow.Server.Sessions;
using GreenGlow.Server.Web;

// The container's HEALTHCHECK: the runtime image has no curl or wget.
if (args is ["--healthcheck"])
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        return (await http.GetAsync($"http://127.0.0.1:{port}/healthz")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment() && DotEnv.Find(AppContext.BaseDirectory) is { } envFile)
    builder.Configuration.AddInMemoryCollection(DotEnv.Read(envFile));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ");

var server = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var relay = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
var data = builder.Configuration.GetSection("Data").Get<DataOptions>() ?? new DataOptions();
var seed = ConnectionSeed.Load(builder.Configuration.GetSection("Seed").Get<SeedOptions>() ?? new SeedOptions());
var cluster = (builder.Configuration.GetSection("Cluster").Get<ClusterOptions>() ?? new ClusterOptions()).Validate();
var demo = builder.Configuration.GetSection("Demo").Get<DemoOptions>() ?? new DemoOptions();
builder.Services.AddSingleton(demo);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(server);
builder.Services.AddSingleton(relay);
builder.Services.AddSingleton(builder.Configuration.GetSection("Sessions").Get<SessionDefaults>() ?? new SessionDefaults());
// Several servers: this node, and syncing its database with the others.
builder.Services.AddSingleton(cluster);
builder.Services.AddSingleton<ClusterNode>();
builder.Services.AddSingleton(s => new Hlc(cluster.NodeId, s.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<SyncSignals>();
builder.Services.AddSingleton<SyncRecorder>();
builder.Services.AddSingleton<SyncStore>();
builder.Services.AddSingleton<PeerTransport>();
builder.Services.AddSingleton<PeerClient>();
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddHostedService(s => s.GetRequiredService<SyncService>());
builder.Services.AddHostedService<AgentCheck>();
builder.Services.AddGreenGlowDb(data);
// The keys behind sign-in cookies live in the database (the /app/data volume) and sync between
// nodes, so a restart or a redeploy doesn't sign everyone out and every node reads every cookie.
builder.Services.AddDataProtection().SetApplicationName("greenglow-connect");
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(s => new ConfigureOptions<KeyManagementOptions>(o =>
    o.XmlRepository = new DbKeyRepository(s.GetRequiredService<IDbContextFactory<GreenGlowDb>>())));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ConnectionLogger>();
builder.Services.AddSingleton<TargetCatalog>();
builder.Services.AddSingleton(HostConnector.Create(relay.CaFile, relay.CaPem));
builder.Services.AddSingleton(new OriginPolicy(server));
builder.Services.AddGreenGlowRateLimiter(
    builder.Configuration.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions(), demo);
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<Access>();
builder.Services.AddSingleton<Shares>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<UserFeed>();
var printer = builder.Configuration.GetSection("Printer").Get<PrinterOptions>() ?? new PrinterOptions();
if (builder.Environment.IsProduction()) (printer.Port, printer.SecurePort) = (null, null);
builder.Services.AddSingleton(printer);
builder.Services.AddSingleton<PrintSessions>();
var entra = builder.Configuration.GetSection("Entra").Get<EntraOptions>() ?? new EntraOptions();
var signIn = builder.AddGreenGlowAuth(entra, builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions(), demo);
if (demo.Enabled)
{
    builder.Services.AddSingleton<GuestPruner>();
    builder.Services.AddHostedService(s => s.GetRequiredService<GuestPruner>());
}
builder.Services.AddSingleton(entra);
// Who's who: Microsoft Graph with Entra ID; with the dev login, a stand-in over the database.
if (entra.Configured)
{
    builder.Services.AddHttpClient<GraphDirectory>(http => http.Timeout = TimeSpan.FromSeconds(15));
    builder.Services.AddSingleton<IDirectory>(s => s.GetRequiredService<GraphDirectory>());
}
else
{
    builder.Services.AddSingleton(builder.Configuration.GetSection("Dev").Get<DevOptions>() ?? new DevOptions());
    builder.Services.AddSingleton<IDirectory, LocalDirectory>();
}

var app = builder.Build();
var clock = app.Services.GetRequiredService<TimeProvider>();
await DatabaseSetup.PrepareAsync(app.Services, clock, data);
var settingsStore = app.Services.GetRequiredService<SettingsStore>();
app.Services.GetRequiredService<SyncStore>().Applied += tables =>
{
    if (tables.Contains("Settings")) settingsStore.Forget();
};
// Before seeding and before serving: take in what the other nodes have, sign-in keys included.
await app.Services.GetRequiredService<SyncService>().JoinAsync();
await app.Services.GetRequiredService<UserStore>().PromoteInitialAdminAsync();
await using (var db = await app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<GreenGlowDb>>().CreateDbContextAsync())
{
    if (demo.Enabled)
    {
        // Demo mode: the seed is the whole configuration, and every connection a demo system.
        var (added, updated, removed) = await ConnectionSeed.ApplyDemoAsync(db, seed, clock);
        if (added.Count + updated.Count + removed.Count > 0)
            app.Logger.LogInformation("demo connections: added {Added}; changed {Updated}; removed {Removed}",
                string.Join(", ", added), string.Join(", ", updated), string.Join(", ", removed));
    }
    else
    {
        var added = await ConnectionSeed.ApplyAsync(db, seed, app.Services.GetRequiredService<IDirectory>(), clock);
        if (added.Count > 0) app.Logger.LogInformation("seeded connections {Connections}", string.Join(", ", added));
    }
}

var log = app.Services.GetRequiredService<ILogger<Program>>();
// Missing Graph permissions show up now, not at the first search.
if (app.Services.GetService<GraphDirectory>() is { } graph)
{
    try
    {
        var missing = await graph.MissingRolesAsync();
        if (missing.Count > 0)
            log.LogWarning(
                "the Entra app registration lacks the Microsoft Graph application permissions {Missing} (with admin consent): people and group searches and group access will fail",
                string.Join(", ", missing));
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or KeyNotFoundException or FormatException or JsonException)
    {
        log.LogWarning("could not check the Microsoft Graph permissions: {Error}", e.Message);
    }
}
var targets = await app.Services.GetRequiredService<TargetCatalog>().AllAsync();
foreach (var target in targets.Where(t => !t.Secure))
    log.LogWarning("target {Target} is plain Telnet: sign-on passwords cross the network unencrypted", target.Name);
foreach (var target in targets.Where(t => t.Secure && !app.Services.GetRequiredService<HostConnector>().VerifiesFor(t)))
    log.LogWarning(
        "target {Target} is TLS without a CA: its certificate is not verified. Set Relay__CaPem or Relay__CaFile, or trust its CA on the admin page, to verify it",
        target.Name);
if (cluster.Clustered)
{
    log.LogInformation("node {Node}; peers {Peers}", cluster.NodeId,
        string.Join(", ", cluster.PeerUrls.Select(p => $"{p.Key}={p.Value}")));
}
log.LogInformation("targets {Targets}; ca {Ca}; origins {Origins}; database {Database}",
    string.Join(", ", targets.Select(t => $"{t.Name}={(t.Secure ? "tls" : "tcp")}://{t.Host}:{t.Port}")),
    app.Services.GetRequiredService<HostConnector>().Ca?.Subject,
    string.Join(", ", app.Services.GetRequiredService<OriginPolicy>().Allowed),
    Path.GetFullPath(data.DbPath));

if (server.TrustForwardedHeaders)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    // The proxy's address isn't known in advance; only it can reach the server.
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
// The peer API first: other nodes, sealed, never through the browser's address.
app.UseCluster();
app.UsePublicUrl(server);
app.UseSecurityHeaders();
app.UseWebSockets();
app.UseBuiltApp(server.StaticDir);
app.UseAuthentication();
app.UseAuthorization();
// After authentication, so limits partition by the signed-in user.
app.UseRateLimiter();
app.UseOriginCheck();
app.MapAuthEndpoints(signIn);

app.MapGet("/healthz", () => Results.Text("ok\n"));
// Names and labels only: the browser never learns or chooses a host.
var api = app.MapGroup("/api").RequireAuthorization();
api.AddEndpointFilter(async (context, next) =>
{
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
api.MapGet("/me", (HttpContext context, SettingsStore settings) =>
    CurrentUser.From(context.User) is { } user
        ? Results.Json(new
        {
            id = user.Id,
            upn = user.Upn,
            name = user.Name,
            isAdmin = context.User.IsInRole(UserStore.AdminRole),
            // Demo mode: a guest, with no sharing and no administration.
            demo = demo.Enabled,
            // What the page tells the user about leaving a session.
            idleMinutes = settings.IdleMinutes,
            maxFrontends = settings.MaxFrontendsPerSession,
        })
        : Results.Unauthorized());
// Only the connections this user may use; names and labels, never hosts.
api.MapGet("/targets", async (HttpContext context, Access access, HostConnector connector, IDbContextFactory<GreenGlowDb> dbs) =>
{
    var user = CurrentUser.From(context.User)!;
    var allowed = await access.AllowedAsync(user);
    await using var db = await dbs.CreateDbContextAsync();
    var mine = await db.UserColors.AsNoTracking().Where(c => c.UserId == user.Id)
        .Select(c => new { c.Connection!.Name, c.Color }).ToDictionaryAsync(c => c.Name, c => c.Color);
    return Results.Json(new
    {
        // verified: for tls://, whether the IBM i's certificate is checked against a CA.
        // color: the administrator's colour for the system; myColor: the user's own, which wins.
        targets = allowed.Select(t => new
        {
            t.Name,
            t.Label,
            t.Secure,
            verified = connector.VerifiesFor(t),
            t.Color,
            myColor = mine.GetValueOrDefault(t.Name),
        }),
    });
});
// The user's own colour for a system they may use; null goes back to the system's colour.
api.MapPut("/targets/{name}/color", async (string name, ColorRequest request, HttpContext context, Access access,
    IDbContextFactory<GreenGlowDb> dbs) =>
{
    var user = CurrentUser.From(context.User)!;
    if (await access.FindAsync(user, name) is null) return Results.NotFound();
    var color = SystemColor.Parse(request.Color);
    if (request.Color is not null && color is null)
        return Results.BadRequest(new { error = "color", message = "The colour must be #rrggbb." });
    await using var db = await dbs.CreateDbContextAsync();
    var connectionId = await db.Connections.Where(c => c.Name == name).Select(c => c.Id).FirstAsync();
    var row = await db.UserColors.FindAsync(user.Id, connectionId);
    if (color is null)
    {
        if (row is not null) db.UserColors.Remove(row);
    }
    else if (row is null) db.UserColors.Add(new UserConnectionColor { UserId = user.Id, ConnectionId = connectionId, Color = color });
    else row.Color = color;
    await db.SaveChangesAsync();
    return Results.NoContent();
});
app.MapSessionEndpoints(api);
app.Map("/ws/user", UserFeed.AttachAsync).RequireAuthorization();
api.MapMacroEndpoints(sharing: !demo.Enabled);
api.MapPrinterEndpoints();
// Sharing, the directory and administration need real people: none of them in demo mode.
if (!demo.Enabled)
{
    api.MapShareEndpoints();
    api.MapAdminEndpoints();
    api.MapGuideEndpoints();
    api.MapDirectoryEndpoints();
}
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<SessionManager>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)));
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<PrintSessions>().StopAsync().Wait(TimeSpan.FromSeconds(10)));

await app.RunAsync();
return 0;

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;

/// <summary>PUT /api/targets/{name}/color: #rrggbb, or null for the system's colour.</summary>
public sealed record ColorRequest(string? Color);
