using System.Security;
using System.Xml;
using GreenGlow.Server.Auth;
using GreenGlow.Server.Data;
using GreenGlow.Server.People;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Macros;

/// <summary>
/// /api/macros: a user's macros and the ones shared with them. Only the owner changes or deletes
/// a macro. A macro is Host-on-Demand macro XML (the format ACS saves); playing, recording,
/// sharing and key combinations build on this.
/// </summary>
public static class MacroEndpoints
{
    public const int MaxName = 60;
    public const int MaxDescription = 200;
    public const int MaxContent = 256 * 1024;

    public sealed record MacroRequest(string? Name, string? Description, string? Content);

    public sealed record KeysRequest(string? Keys);

    /// <summary>Key combinations a macro can have: Alt+Shift and a letter or digit, but not the page's own.</summary>
    private static readonly System.Text.RegularExpressions.Regex KeyCombination = new("^Alt\\+Shift\\+[A-Z0-9]$");

    private static readonly HashSet<string> PageKeys = ["Alt+Shift+C"];

    /// <summary>A person by UPN, or a group by ID (one the owner is in) with its name.</summary>
    public sealed record ShareRequest(string? Type, string? Upn, string? GroupId, string? Name);

    public static void MapMacroEndpoints(this RouteGroupBuilder api, bool sharing = true)
    {
        api.MapGet("/macros", async (HttpContext context, IDbContextFactory<GreenGlowDb> dbs, Access access) =>
        {
            var user = CurrentUser.From(context.User)!;
            var groups = await access.GroupsAsync(user.Id);
            await using var db = await dbs.CreateDbContextAsync();
            var keys = await db.MacroKeys.AsNoTracking().Where(k => k.UserId == user.Id)
                .ToDictionaryAsync(k => k.MacroId, k => k.Keys);
            var macros = await Visible(db, user, groups).AsNoTracking().Include(m => m.Shares).ToListAsync();
            return Results.Json(new
            {
                macros = macros
                    .OrderBy(m => m.OwnerUserId != user.Id).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => Describe(m, user, keys.GetValueOrDefault(m.Id))),
            });
        });

        api.MapGet("/macros/{id:guid}", async (Guid id, HttpContext context, IDbContextFactory<GreenGlowDb> dbs, Access access) =>
        {
            var user = CurrentUser.From(context.User)!;
            var groups = await access.GroupsAsync(user.Id);
            await using var db = await dbs.CreateDbContextAsync();
            var macro = await Visible(db, user, groups).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
            return macro is null ? Results.NotFound() : Results.Json(new { macro = Describe(macro, user, null), content = macro.Content });
        });

        api.MapPost("/macros", async (MacroRequest request, HttpContext context, IDbContextFactory<GreenGlowDb> dbs,
            TimeProvider clock) =>
        {
            var user = CurrentUser.From(context.User)!;
            var name = request.Name?.Trim() ?? "";
            if (Check(name, request.Description, request.Content) is { } problem) return problem;
            await using var db = await dbs.CreateDbContextAsync();
            if (await db.Macros.AnyAsync(m => m.OwnerUserId == user.Id && m.Name == name))
                return Results.Conflict(new { error = "name", message = $"You already have a macro called {name}." });
            var now = clock.GetUtcNow();
            var macro = new Macro
            {
                OwnerUserId = user.Id,
                OwnerUpn = user.Upn,
                Name = name,
                Description = request.Description?.Trim() ?? "",
                Content = request.Content ?? EmptyMacro(name),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Macros.Add(macro);
            await db.SaveChangesAsync();
            return Results.Json(new { macro = Describe(macro, user, null) }, statusCode: StatusCodes.Status201Created);
        });

        api.MapPut("/macros/{id:guid}", async (Guid id, MacroRequest request, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs, TimeProvider clock) =>
        {
            var user = CurrentUser.From(context.User)!;
            await using var db = await dbs.CreateDbContextAsync();
            var macro = await db.Macros.FirstOrDefaultAsync(m => m.Id == id && m.OwnerUserId == user.Id);
            if (macro is null) return Results.NotFound();
            var name = request.Name?.Trim() ?? macro.Name;
            if (Check(name, request.Description, request.Content) is { } problem) return problem;
            if (!string.Equals(name, macro.Name, StringComparison.OrdinalIgnoreCase)
                && await db.Macros.AnyAsync(m => m.OwnerUserId == user.Id && m.Name == name && m.Id != id))
                return Results.Conflict(new { error = "name", message = $"You already have a macro called {name}." });
            macro.Name = name;
            if (request.Description is not null) macro.Description = request.Description.Trim();
            if (request.Content is not null) macro.Content = request.Content;
            macro.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Json(new { macro = Describe(macro, user, null) });
        });

        // A key combination for a macro the user may play (their own or one shared with them);
        // null takes it away. Each user's own, and one macro per combination.
        api.MapPut("/macros/{id:guid}/keys", async (Guid id, KeysRequest request, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs, Access access) =>
        {
            var user = CurrentUser.From(context.User)!;
            var groups = await access.GroupsAsync(user.Id);
            await using var db = await dbs.CreateDbContextAsync();
            if (!await Visible(db, user, groups).AnyAsync(m => m.Id == id)) return Results.NotFound();
            var keys = request.Keys?.Trim();
            var mine = await db.MacroKeys.FirstOrDefaultAsync(k => k.UserId == user.Id && k.MacroId == id);
            if (string.IsNullOrEmpty(keys))
            {
                if (mine is not null) db.MacroKeys.Remove(mine);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
            if (!KeyCombination.IsMatch(keys) || PageKeys.Contains(keys))
                return Results.BadRequest(new
                {
                    error = "keys",
                    message = "A macro's keys are Alt+Shift and a letter or digit (not Alt+Shift+C, which copies a table).",
                });
            var taken = await db.MacroKeys.Include(k => k.Macro)
                .FirstOrDefaultAsync(k => k.UserId == user.Id && k.Keys == keys && k.MacroId != id);
            if (taken is not null)
                return Results.Conflict(new { error = "keys", message = $"{keys} already plays {taken.Macro?.Name}." });
            if (mine is null) db.MacroKeys.Add(new MacroKey { UserId = user.Id, MacroId = id, Keys = keys });
            else mine.Keys = keys;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Off in demo mode: guests have no one to share with.
        if (sharing) MapMacroShares(api);

        api.MapDelete("/macros/{id:guid}", async (Guid id, HttpContext context, IDbContextFactory<GreenGlowDb> dbs) =>
        {
            var user = CurrentUser.From(context.User)!;
            // Row by row (not in bulk), with its shares and everyone's keys for it, so each delete
            // reaches the other nodes.
            await using var db = await dbs.CreateDbContextAsync();
            var macro = await db.Macros.Include(m => m.Shares).FirstOrDefaultAsync(m => m.Id == id && m.OwnerUserId == user.Id);
            if (macro is null) return Results.NotFound();
            db.MacroKeys.RemoveRange(await db.MacroKeys.Where(k => k.MacroId == id).ToListAsync());
            db.Macros.Remove(macro);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static void MapMacroShares(RouteGroupBuilder api)
    {
        // Sharing: the owner names people (anyone in the directory) or groups (ones they are in;
        // an administrator any). Those it is shared with may play it, never change it.
        api.MapGet("/macros/{id:guid}/shares", async (Guid id, HttpContext context, IDbContextFactory<GreenGlowDb> dbs) =>
        {
            var user = CurrentUser.From(context.User)!;
            await using var db = await dbs.CreateDbContextAsync();
            var macro = await db.Macros.AsNoTracking().Include(m => m.Shares)
                .FirstOrDefaultAsync(m => m.Id == id && m.OwnerUserId == user.Id);
            return macro is null
                ? Results.NotFound()
                : Results.Json(new { shares = macro.Shares.OrderBy(s => s.PrincipalType).ThenBy(s => s.DisplayName).Select(DescribeShare) });
        });

        api.MapPost("/macros/{id:guid}/shares", async (Guid id, ShareRequest request, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs, IDirectory directory, Access access) =>
        {
            var user = CurrentUser.From(context.User)!;
            await using var db = await dbs.CreateDbContextAsync();
            var macro = await db.Macros.Include(m => m.Shares).FirstOrDefaultAsync(m => m.Id == id && m.OwnerUserId == user.Id);
            if (macro is null) return Results.NotFound();
            MacroShare share;
            if (request.Type == "user")
            {
                var person = await directory.FindUserAsync(request.Upn?.Trim() ?? "");
                if (person is null)
                    return Results.BadRequest(new { error = "upn", message = $"{request.Upn} isn't in the directory." });
                if (person.Id == user.Id)
                    return Results.BadRequest(new { error = "self", message = "It is already yours." });
                share = new MacroShare { PrincipalType = PrincipalType.User, PrincipalId = person.Id, DisplayName = person.Upn ?? person.Name };
            }
            else if (request.Type == "group")
            {
                var groupId = request.GroupId ?? "";
                if (!context.User.IsInRole(UserStore.AdminRole) && !(await access.GroupsAsync(user.Id)).Contains(groupId))
                    return Results.BadRequest(new { error = "group", message = "You can share only with a group you are in." });
                var name = request.Name?.Trim() ?? "";
                share = new MacroShare
                {
                    PrincipalType = PrincipalType.Group,
                    PrincipalId = groupId,
                    DisplayName = name.Length is > 0 and <= 120 ? name : groupId,
                };
            }
            else return Results.BadRequest(new { error = "type", message = "Share with a user or a group." });
            if (macro.Shares.Any(s => s.PrincipalType == share.PrincipalType && s.PrincipalId == share.PrincipalId))
                return Results.Conflict(new { error = "shared", message = $"It is already shared with {share.DisplayName}." });
            macro.Shares.Add(share);
            await db.SaveChangesAsync();
            return Results.Json(DescribeShare(share), statusCode: StatusCodes.Status201Created);
        });

        api.MapDelete("/macros/{id:guid}/shares/{shareId:guid}", async (Guid id, Guid shareId, HttpContext context,
            IDbContextFactory<GreenGlowDb> dbs) =>
        {
            var user = CurrentUser.From(context.User)!;
            await using var db = await dbs.CreateDbContextAsync();
            var share = await db.MacroShares
                .FirstOrDefaultAsync(s => s.Id == shareId && s.MacroId == id && s.Macro!.OwnerUserId == user.Id);
            if (share is null) return Results.NotFound();
            db.MacroShares.Remove(share);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>The user's own macros and those shared with them or a group of theirs.</summary>
    private static IQueryable<Macro> Visible(GreenGlowDb db, CurrentUser user, IReadOnlySet<string> groups)
    {
        var groupIds = groups.ToList();
        return db.Macros.Where(m => m.OwnerUserId == user.Id
            || m.Shares.Any(s => (s.PrincipalType == PrincipalType.User && s.PrincipalId == user.Id)
                                 || (s.PrincipalType == PrincipalType.Group && groupIds.Contains(s.PrincipalId))));
    }

    private static object DescribeShare(MacroShare s) => new
    {
        id = s.Id,
        type = s.PrincipalType == PrincipalType.Group ? "group" : "user",
        name = s.DisplayName,
    };

    private static object Describe(Macro m, CurrentUser user, string? keys) => new
    {
        id = m.Id,
        name = m.Name,
        description = m.Description,
        owner = m.OwnerUpn,
        mine = m.OwnerUserId == user.Id,
        // For the owner: how many people and groups it is shared with.
        sharedWith = m.OwnerUserId == user.Id ? m.Shares.Count : 0,
        updatedAt = m.UpdatedAt,
        keys,
    };

    private static IResult? Check(string name, string? description, string? content)
    {
        if (name.Length is 0 or > MaxName)
            return Results.BadRequest(new { error = "name", message = $"A macro needs a name of at most {MaxName} characters." });
        if (description is { Length: > MaxDescription })
            return Results.BadRequest(new { error = "description", message = $"A description is at most {MaxDescription} characters." });
        if (content is null) return null;
        if (content.Length > MaxContent)
            return Results.BadRequest(new { error = "content", message = "This macro is too big." });
        if (!IsMacroXml(content))
            return Results.BadRequest(new { error = "content", message = "A macro is Host-on-Demand macro XML (an HAScript element)." });
        if (HoldsPassword(content))
            return Results.BadRequest(new
            {
                error = "password",
                message = "This macro holds a typed password, and GreenGlow never keeps one. In ACS, make it a prompt, then import it again.",
            });
        return null;
    }

    /// <summary>Well-formed XML with an HAScript root; no DTD, so no entities to expand.</summary>
    public static bool IsMacroXml(string content)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(content),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            if (reader.LocalName != "HAScript") return false;
            while (reader.Read())
            {
            }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>An input HOD stores encrypted: a password typed when the macro was recorded.</summary>
    public static bool HoldsPassword(string content)
    {
        using var reader = XmlReader.Create(new StringReader(content),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "input" }
                && reader.GetAttribute("encrypted") == "true" && !string.IsNullOrEmpty(reader.GetAttribute("value")))
                return true;
        return false;
    }

    /// <summary>A macro with no screens yet, as ACS starts one.</summary>
    public static string EmptyMacro(string name) =>
        $"""
         <HAScript name="{SecurityElement.Escape(name)}" description="" timeout="60000" pausetime="300" promptall="true" blockinput="false" author="" creationdate="" supressclearevents="false" usevars="false" ignorepauseforenhancedtn="true" delayifnotenhancedtn="0" ignorepausetimeforenhancedtn="true" continueontimeout="false">
         </HAScript>

         """;
}
