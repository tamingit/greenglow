using System.Text.Json;
using System.Text.Json.Serialization;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Configuration;

/// <summary>Where the connections to seed come from.</summary>
public sealed class SeedOptions
{
    /// <summary>A JSON file of connections (see config/connections.example.json).</summary>
    public string? ConnectionsFile { get; set; }

    /// <summary>The same JSON inline, for deployments that pass it in a variable.</summary>
    public string? ConnectionsJson { get; set; }
}

/// <summary>The seed file: the IBM i systems users may connect to.</summary>
public sealed class SeedDocument
{
    public List<SeedConnection> Connections { get; set; } = [];
}

public sealed class SeedConnection
{
    /// <summary>Lowercase letters, digits and dashes; what URLs and the API use.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the picker shows; the name if missing.</summary>
    public string? Label { get; set; }

    /// <summary>tls://host[:992] (verified TLS) or tcp://host[:23] (plain Telnet, test systems only).</summary>
    public string Url { get; set; } = "";

    /// <summary>Most host sessions one user may have open on this system at once.</summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>A test system: automated sign-on tests may use it. Never set for production.</summary>
    public bool IsTest { get; set; }

    /// <summary>A demo system: the fake IBM i or a public one. Demo mode allows no other kind.</summary>
    public bool IsDemo { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The system's colour on the page, #rrggbb; none if missing.</summary>
    public string? Color { get; set; }

    /// <summary>Entra ID users and groups allowed to use it. Without any, only administrators can.</summary>
    public List<SeedAccess> Access { get; set; } = [];
}

public sealed class SeedAccess
{
    /// <summary>"user" or "group".</summary>
    public string Type { get; set; } = "";

    /// <summary>The Entra object ID. Optional: without it, Name is looked up in Entra ID.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// A group's display name or a user's UPN. Shown in the admin UI; without an Id, it is
    /// looked up in the directory when the connection is first added.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Seeds the connections table. Connections whose name isn't in the database yet are added
/// with their access; existing ones are left alone, because after the first start the database
/// (and the admin UI) is the source of truth.
/// </summary>
public static class ConnectionSeed
{
    public const int MaxSessionsLimit = 9;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The seed from configuration, validated; empty when none is configured.</summary>
    public static IReadOnlyList<HostConnection> Load(SeedOptions options)
    {
        var sources = new List<(string Where, string Text)>();
        if (!string.IsNullOrWhiteSpace(options.ConnectionsFile))
        {
            try
            {
                sources.Add((options.ConnectionsFile, File.ReadAllText(options.ConnectionsFile)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ConfigException($"Seed:ConnectionsFile {options.ConnectionsFile} could not be read ({e.GetType().Name})");
            }
        }
        if (!string.IsNullOrWhiteSpace(options.ConnectionsJson)) sources.Add(("Seed:ConnectionsJson", options.ConnectionsJson));

        var connections = new List<HostConnection>();
        foreach (var (where, text) in sources)
            foreach (var connection in Parse(where, text))
            {
                if (connections.Any(c => c.Name == connection.Name))
                    throw new ConfigException($"{where}: the connection \"{connection.Name}\" is seeded twice");
                connections.Add(connection);
            }
        return connections;
    }

    public static IReadOnlyList<HostConnection> Parse(string where, string text)
    {
        SeedDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SeedDocument>(text, Json);
        }
        catch (JsonException e)
        {
            throw new ConfigException($"{where} is not valid seed JSON: {e.Message}");
        }
        if (document is null) throw new ConfigException($"{where} is empty");

        var result = new List<HostConnection>();
        foreach (var (seed, index) in document.Connections.Select((c, i) => (c, i)))
        {
            var name = seed.Name.Trim();
            if (!TargetParser.IsValidName(name))
                throw new ConfigException($"{where}: connection {index + 1} needs a name of lowercase letters, digits and dashes");
            if (result.Any(c => c.Name == name)) throw new ConfigException($"{where}: the connection \"{name}\" is seeded twice");
            var target = TargetParser.ParseUrl(name, seed.Label?.Trim() is { Length: > 0 } label ? label : name, seed.Url.Trim());
            if (seed.MaxSessionsPerUser is < 1 or > MaxSessionsLimit)
                throw new ConfigException($"{where}: \"{name}\" maxSessionsPerUser must be from 1 to {MaxSessionsLimit}");
            var color = SystemColor.Parse(seed.Color);
            if (seed.Color is not null && color is null)
                throw new ConfigException($"{where}: \"{name}\" color must be #rrggbb");
            var connection = new HostConnection
            {
                Name = name,
                Label = target.Label,
                Host = target.Host,
                Port = target.Port,
                Secure = target.Secure,
                MaxSessionsPerUser = seed.MaxSessionsPerUser,
                IsTest = seed.IsTest,
                IsDemo = seed.IsDemo,
                Enabled = seed.Enabled,
                Color = color,
            };
            foreach (var access in seed.Access)
            {
                var type = access.Type.Trim().ToLowerInvariant() switch
                {
                    "user" => PrincipalType.User,
                    "group" => PrincipalType.Group,
                    _ => throw new ConfigException($"{where}: \"{name}\" access type must be user or group, not \"{access.Type}\""),
                };
                var display = access.Name?.Trim() ?? "";
                string principal;
                if (!string.IsNullOrWhiteSpace(access.Id))
                {
                    if (!Guid.TryParse(access.Id, out var id))
                        throw new ConfigException($"{where}: \"{name}\" access id \"{access.Id}\" must be an Entra object ID");
                    principal = id.ToString();
                }
                else if (display.Length > 0) principal = ByName + display;
                else
                    throw new ConfigException(
                        $"{where}: \"{name}\" access needs an id, or a name ({(type == PrincipalType.Group ? "the group's name" : "the user's UPN")})");
                if (connection.Grants.Any(g => g.PrincipalType == type && g.PrincipalId == principal)) continue;
                connection.Grants.Add(new ConnectionGrant { PrincipalType = type, PrincipalId = principal, DisplayName = display });
            }
            result.Add(connection);
        }
        return result;
    }

    /// <summary>Marks a grant given by name, to look up in the directory when it is added.</summary>
    public const string ByName = "name:";

    /// <summary>
    /// Adds the seeded connections the database doesn't have yet, in seed order. Grants given
    /// by name are looked up in the directory first: a group by its exact display name (which
    /// must match one group), a user by UPN. A name that can't be resolved stops the start,
    /// rather than leaving a connection open to administrators only without saying so.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        GreenGlowDb db, IEnumerable<HostConnection> seed, IDirectory directory, TimeProvider clock)
    {
        var known = (await db.Connections.Select(c => c.Name).ToListAsync()).ToHashSet();
        var order = await db.Connections.Select(c => (int?)c.SortOrder).MaxAsync() ?? -1;
        var added = new List<string>();
        foreach (var connection in seed.Where(c => !known.Contains(c.Name)))
        {
            await ResolveAsync(connection, directory);
            // Keys from the names, so nodes seeding the same JSON make the same rows.
            connection.Id = SeedId("connection", connection.Name);
            foreach (var grant in connection.Grants)
                grant.Id = SeedId("grant", connection.Name, grant.PrincipalType.ToString(), grant.PrincipalId);
            connection.SortOrder = ++order;
            connection.CreatedAt = connection.UpdatedAt = clock.GetUtcNow();
            db.Connections.Add(connection);
            added.Add(connection.Name);
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>
    /// Demo mode: the seed is the whole configuration, because there is no admin page. Every
    /// seeded connection must be a demo system, with no access list (every guest may use every
    /// enabled one). Connections in the database are made to match the seed, those not in it are
    /// deleted (one row at a time, so each delete is journalled), and new ones are added.
    /// Returns the names added, changed and removed.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> Removed)> ApplyDemoAsync(
        GreenGlowDb db, IReadOnlyList<HostConnection> seed, TimeProvider clock)
    {
        if (seed.Count == 0)
            throw new ConfigException("Demo mode needs its connections in Seed__ConnectionsJson or Seed__ConnectionsFile.");
        foreach (var connection in seed)
        {
            if (!connection.IsDemo)
                throw new ConfigException($"Demo mode allows only demo systems: mark \"{connection.Name}\" \"isDemo\": true, or remove it.");
            if (connection.Grants.Count > 0)
                throw new ConfigException($"Demo mode: \"{connection.Name}\" takes no access list; every guest may use every enabled demo system.");
        }

        var now = clock.GetUtcNow();
        var stored = await db.Connections.Include(c => c.Grants).ToListAsync();
        var removed = new List<string>();
        foreach (var connection in stored.Where(c => seed.All(s => s.Name != c.Name)))
        {
            db.UserColors.RemoveRange(await db.UserColors.Where(c => c.ConnectionId == connection.Id).ToListAsync());
            db.Connections.Remove(connection);
            removed.Add(connection.Name);
        }

        var updated = new List<string>();
        var added = new List<string>();
        var order = -1;
        foreach (var wanted in seed)
        {
            order++;
            var connection = stored.FirstOrDefault(c => c.Name == wanted.Name);
            if (connection is null)
            {
                wanted.Id = SeedId("connection", wanted.Name);
                wanted.SortOrder = order;
                wanted.CreatedAt = wanted.UpdatedAt = now;
                db.Connections.Add(wanted);
                added.Add(wanted.Name);
                continue;
            }
            // A changed address starts again with no trusted certificate.
            var moved = connection.Host != wanted.Host || connection.Port != wanted.Port || connection.Secure != wanted.Secure;
            var changed = moved || connection.Label != wanted.Label || connection.MaxSessionsPerUser != wanted.MaxSessionsPerUser
                          || connection.IsTest != wanted.IsTest || !connection.IsDemo || connection.Enabled != wanted.Enabled
                          || connection.Color != wanted.Color || connection.SortOrder != order || connection.Grants.Count > 0;
            if (!changed) continue;
            connection.Label = wanted.Label;
            connection.Host = wanted.Host;
            connection.Port = wanted.Port;
            connection.Secure = wanted.Secure;
            connection.MaxSessionsPerUser = wanted.MaxSessionsPerUser;
            connection.IsTest = wanted.IsTest;
            connection.IsDemo = true;
            connection.Enabled = wanted.Enabled;
            connection.Color = wanted.Color;
            connection.SortOrder = order;
            connection.Grants.Clear();
            if (moved) (connection.TrustedCaPem, connection.TrustedName, connection.TrustedBy, connection.TrustedAt) = (null, null, null, null);
            connection.UpdatedAt = now;
            updated.Add(connection.Name);
        }
        await db.SaveChangesAsync();
        return (added, updated, removed);
    }

    /// <summary>A UUID (version 8) made from a seeded row's names: the same on every node.</summary>
    public static Guid SeedId(params string[] parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("greenglow-connect seed|" + string.Join("|", parts)));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static async Task ResolveAsync(HostConnection connection, IDirectory directory)
    {
        foreach (var grant in connection.Grants.Where(g => g.PrincipalId.StartsWith(ByName, StringComparison.Ordinal)).ToList())
        {
            var name = grant.PrincipalId[ByName.Length..];
            try
            {
                if (grant.PrincipalType == PrincipalType.Group)
                {
                    var groups = await directory.FindGroupsByNameAsync(name);
                    grant.PrincipalId = groups.Count switch
                    {
                        1 => groups[0].Id,
                        0 => throw new ConfigException($"Seed: \"{connection.Name}\" gives access to the group \"{name}\", but no Entra ID group has that name"),
                        _ => throw new ConfigException(
                            $"Seed: \"{connection.Name}\" gives access to the group \"{name}\", but {groups.Count} Entra ID groups have that name; give its object ID as \"id\""),
                    };
                    grant.DisplayName = groups[0].Name;
                }
                else
                {
                    var user = await directory.FindUserAsync(name)
                               ?? throw new ConfigException($"Seed: \"{connection.Name}\" gives access to {name}, but no one in Entra ID has that UPN");
                    grant.PrincipalId = user.Id;
                    grant.DisplayName = user.Upn ?? name;
                }
            }
            catch (HttpRequestException e)
            {
                throw new ConfigException($"Seed: \"{connection.Name}\": looking up \"{name}\" in Entra ID failed ({e.Message})");
            }
        }
        // A group named and also given by id is one grant.
        foreach (var duplicate in connection.Grants.GroupBy(g => (g.PrincipalType, g.PrincipalId)).SelectMany(g => g.Skip(1)).ToList())
            connection.Grants.Remove(duplicate);
    }
}
