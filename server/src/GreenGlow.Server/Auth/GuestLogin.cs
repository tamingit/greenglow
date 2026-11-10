using System.Security.Claims;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.Sessions;
using GreenGlow.Server.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GreenGlow.Server.Auth;

/// <summary>
/// Demo mode's sign-in (Demo:Enabled): the start page (demo.html in the web app) posts to
/// /auth/guest, which makes a new guest and signs them in. Opening the page creates no one, so
/// crawlers make no guests; the post needs an allowed Origin, like every post, and is limited per
/// client address (<see cref="RateLimiting.Guest"/>).
/// </summary>
public static class GuestLogin
{
    /// <summary>The start page, in the built web app.</summary>
    public const string Path = "/demo.html";

    public static void MapGuestLogin(this WebApplication app)
    {
        app.Logger.LogWarning("Demo mode is on: anyone can sign in as a guest and use the demo systems.");
        app.MapPost("/auth/guest", async (HttpContext context, UserStore users) =>
        {
            var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync() : null;
            var identity = await users.SignInGuestAsync();
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(AuthSetup.SafeReturn(form?["returnUrl"]));
        }).RequireRateLimiting(RateLimiting.Guest);
    }
}

/// <summary>
/// Every hour, removes guests not seen for twice the guest sign-in time who hold no session, so
/// the database doesn't keep every visitor.
/// </summary>
public sealed class GuestPruner(UserStore users, SessionManager sessions, DemoOptions demo, TimeProvider clock,
    ILogger<GuestPruner> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await PruneAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("pruning demo guests failed: {Error}", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stopping));
    }

    public async Task<int> PruneAsync()
    {
        var inUse = sessions.All.Select(s => s.Owner.Id).ToHashSet();
        var removed = await users.PruneGuestsAsync(clock.GetUtcNow() - TimeSpan.FromHours(2 * demo.GuestHours), inUse);
        if (removed > 0) log.LogInformation("removed {Count} demo guests", removed);
        return removed;
    }
}
