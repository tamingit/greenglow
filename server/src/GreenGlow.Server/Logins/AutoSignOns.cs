using System.Collections.Concurrent;
using System.Security.Cryptography;
using GreenGlow.Engine;
using GreenGlow.Server.Configuration;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Relay;

namespace GreenGlow.Server.Logins;

/// <summary>
/// Makes a saved login into a Telnet automatic sign-on (RFC 4777 section 5): the session
/// sends the user and a one-time password substitute during negotiation, as ACS's bypass
/// sign-on does, so the password never crosses the network and no key is typed for the user.
/// Which substitute the IBM i wants depends on its password level, which Telnet doesn't say:
/// it is asked of the sign-on server (no sign-on involved) and remembered for a few minutes.
/// If that can't be learned, there is no automatic sign-on: the sign-on screen appears.
/// </summary>
public sealed class AutoSignOns(HostEndpoints endpoints, HostConnector connector, TimeProvider clock, ILogger<AutoSignOns> log)
{
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (int Level, DateTimeOffset At)> _levels = new();

    public async Task<AutoSignOn?> ForAsync(Target target, string ibmUser, string password, CancellationToken cancel)
    {
        int level;
        try
        {
            level = await LevelAsync(target, cancel);
        }
        catch (SignOnException e)
        {
            endpoints.Forget(target, HostService.SignOn);
            log.LogInformation("no automatic sign-on to {Connection}: its password level couldn't be learned ({Failure})",
                target.Name, e.Failure);
            return null;
        }
        return new AutoSignOn(ibmUser.ToUpperInvariant(), serverSeed =>
        {
            var clientSeed = RandomNumberGenerator.GetBytes(8);
            try
            {
                var (_, substitute) = PasswordSubstitute.For(level, ibmUser, password, clientSeed, serverSeed);
                return (clientSeed, substitute);
            }
            catch (SignOnException)
            {
                // A password this level can't take, or a level nobody knows: sign on at the screen.
                return null;
            }
        });
    }

    /// <summary>Forgets a system's password level, so the next sign-on asks again.</summary>
    public void Forget(string connection) => _levels.TryRemove(connection, out _);

    private async Task<int> LevelAsync(Target target, CancellationToken cancel)
    {
        var now = clock.GetUtcNow();
        if (_levels.TryGetValue(target.Name, out var known) && now - known.At < Remembered) return known.Level;
        var endpoint = await endpoints.ForAsync(target, HostService.SignOn, cancel);
        var level = await SignOnServer.PasswordLevelAsync(connector, endpoint, cancel);
        _levels[target.Name] = (level, now);
        return level;
    }
}
