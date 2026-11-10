using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace GreenGlow.Server.Web;

/// <summary>
/// Per-user request rate limits (<see cref="RateLimitOptions"/>). The "directory" policy guards
/// the directory search so it can't be used to enumerate Entra ID quickly; the "signon" policy
/// guards printer sign-on so it can't be used to guess an IBM i password quickly. Each is a fixed
/// window of one minute, partitioned by the signed-in user's object ID (or the client address when
/// there is none). A refused request gets 429 with Retry-After. These sit on top of Entra ID (which
/// throttles and locks web sign-in) and the IBM i (which disables a profile after repeated bad
/// passwords); they are a safety net, not the primary control.
/// </summary>
public static class RateLimiting
{
    public const string Directory = "directory";
    public const string SignOn = "signon";

    /// <summary>New demo guests (POST /auth/guest), per client address per hour.</summary>
    public const string Guest = "guest";

    public static IServiceCollection AddGreenGlowRateLimiter(this IServiceCollection services, RateLimitOptions options, DemoOptions demo)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(Directory, context => Window(context, options, options.DirectoryPerMinute));
            limiter.AddPolicy(SignOn, context => Window(context, options, options.SignOnPerMinute));
            limiter.AddPolicy(Guest, context =>
            {
                // Always by address: a visitor starting a demo isn't signed in yet.
                var key = ClientAddress.Of(context) ?? "anonymous";
                return options.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, demo.GuestsPerHour),
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter(key);
            });
        });
        return services;
    }

    private static RateLimitPartition<string> Window(HttpContext context, RateLimitOptions options, int perMinute)
    {
        var key = Key(context);
        return options.Enabled
            ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, perMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(key);
    }

    /// <summary>The partition: the signed-in user, else the client address, else a shared bucket.</summary>
    private static string Key(HttpContext context) =>
        context.User.FindFirstValue(Claims.ObjectId) ?? ClientAddress.Of(context) ?? "anonymous";
}
