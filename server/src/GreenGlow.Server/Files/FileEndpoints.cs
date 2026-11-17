using System.Net.Sockets;
using GreenGlow.Server.Auth;
using GreenGlow.Server.HostServers;
using GreenGlow.Server.Logins;
using GreenGlow.Server.People;
using GreenGlow.Server.Sessions;
using GreenGlow.Server.Web;
using Microsoft.Net.Http.Headers;

namespace GreenGlow.Server.Files;

/// <summary>
/// /api/files/{connection}: files from a system's integrated file system, by path, downloaded
/// under the user's own IBM i profile, so the IBM i decides what they may read. The same access
/// as sessions: a system the user may not use answers like one that doesn't exist. Changes need
/// an allowed Origin (OriginCheck), like the rest of /api.
/// </summary>
public static class FileEndpoints
{
    /// <param name="Login">One of the user's saved logins for the system, in place of a typed user and password.</param>
    /// <param name="Save">Keep the typed user and password as a saved login, once the IBM i accepted them.</param>
    /// <param name="Remember">With Save: also keep the password (sealed) in the database.</param>
    public sealed record SignOnRequest(string? User, string? Password, Guid? Login = null, bool? Save = null, bool? Remember = null);

    public static RouteGroupBuilder MapFileEndpoints(this RouteGroupBuilder api)
    {
        var files = api.MapGroup("/files/{connection}");

        // Whether the user is signed on there, and as whom; otherwise the IBM i user to offer and
        // the user's saved logins (one that is ready signs on without asking).
        files.MapGet("", async (string connection, HttpContext context, Access access, FileSessions sessions,
            SessionManager hostSessions, IbmLogins logins) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            var session = sessions.Find(user.Id, connection);
            var suggested = hostSessions.All
                .Where(s => s.Owner.Id == user.Id && s.Target.Name == connection && !string.IsNullOrEmpty(s.IbmUser))
                .Select(s => s.IbmUser).FirstOrDefault();
            return Results.Json(new
            {
                signedOn = session is not null,
                ibmUser = session?.IbmUser,
                suggestedUser = suggested,
                canRemember = logins.CanRemember,
                logins = (await logins.ListAsync(user.Id, target)).Select(LoginEndpoints.Describe),
            });
        });

        files.MapPost("/signon", async (string connection, SignOnRequest request, HttpContext context, Access access,
            FileSessions sessions, IbmLogins logins) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is not { } target) return NoSystem();
            var (ibmUser, password) = (request.User ?? "", request.Password ?? "");
            if (request.Login is { } loginId)
            {
                var login = await logins.CredentialsAsync(user.Id, target, loginId);
                if (login is null) return Results.NotFound(new { error = "unknown-login", message = "That login isn't saved any more." });
                if (login.Value.Password is null)
                    return Results.Conflict(new { error = "password-needed", message = $"Type the password for {login.Value.IbmUser}." });
                (ibmUser, password) = (login.Value.IbmUser, login.Value.Password);
            }
            try
            {
                var session = await sessions.SignOnAsync(user, target, ClientAddress.Of(context), ibmUser, password, context.RequestAborted);
                if (request.Login is null && request.Save == true)
                    await logins.SaveAsync(user.Id, target, session.IbmUser, password, request.Remember);
                return Results.Json(new { signedOn = true, ibmUser = session.IbmUser });
            }
            catch (SignOnException e)
            {
                // A saved login the IBM i no longer takes is dropped, so it is never tried twice.
                if (request.Login is not null
                    && e.Failure is SignOnFailure.WrongPassword or SignOnFailure.WrongPasswordDisabled or SignOnFailure.PasswordExpired)
                    await logins.RefusedAsync(user.Id, target, ibmUser);
                return LoginEndpoints.Failure(e);
            }
            catch (FileServerException e)
            {
                return Results.Json(new { error = "file-server", message = e.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting(RateLimiting.SignOn);

        files.MapDelete("/signon", async (string connection, HttpContext context, FileSessions sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            await sessions.SignOffAsync(user.Id, connection, "signed off");
            return Results.NoContent();
        });

        // A folder's entries, folders first, then by name; at most MaxEntries (truncated says so).
        files.MapGet("/list", async (string connection, string? path, HttpContext context, Access access,
            FileSessions sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            if (sessions.Find(user.Id, connection) is not { } session) return SignOnNeeded();
            var folder = FolderPath(path);
            try
            {
                FileServerClient.PathBytes(folder);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = "path", message = e.Message });
            }
            using var use = await session.UseAsync(context.RequestAborted);
            try
            {
                // One more than shown, to know whether there were more.
                var entries = await session.Client.ListAsync(folder, MaxEntries + 1, CancellationToken.None);
                return Results.Json(new
                {
                    path = folder,
                    truncated = entries.Count > MaxEntries,
                    entries = entries
                        .OrderByDescending(e => e.Folder)
                        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxEntries)
                        .Select(e => new { name = e.Name, folder = e.Folder, size = e.Folder ? (long?)null : e.Size, modified = e.Modified }),
                });
            }
            catch (FileServerException e) when (e.Code >= 0)
            {
                return Refused(e);
            }
            catch (Exception e) when (IsLost(e))
            {
                use.Dispose();
                return await LostAsync(sessions, session, e);
            }
        });

        // Whether a path is a file the user may read, and its size: the panel asks before it
        // downloads, so a refusal shows in the panel instead of as a failed download.
        files.MapGet("/info", async (string connection, string? path, HttpContext context, Access access,
            FileSessions sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            if (sessions.Find(user.Id, connection) is not { } session) return SignOnNeeded();
            path = path?.Trim() ?? "";
            try
            {
                FileServerClient.PathBytes(path);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = "path", message = e.Message });
            }
            using var use = await session.UseAsync(context.RequestAborted);
            try
            {
                var file = await session.Client.OpenForReadAsync(path, CancellationToken.None);
                await session.Client.CloseAsync(file.Handle, CancellationToken.None);
                return Results.Json(new { path, name = FileName(path), size = file.Size });
            }
            catch (FileServerException e) when (e.Code >= 0)
            {
                return Refused(e);
            }
            catch (Exception e) when (IsLost(e))
            {
                use.Dispose();
                return await LostAsync(sessions, session, e);
            }
        });

        // A file by its IFS path, as an attachment. It is opened first, so a refusal is an answer
        // of its own; then it streams, with the length the IBM i reported.
        files.MapGet("/download", async (string connection, string? path, HttpContext context, Access access,
            FileSessions sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            if (sessions.Find(user.Id, connection) is not { } session) return SignOnNeeded();
            path = path?.Trim() ?? "";
            try
            {
                FileServerClient.PathBytes(path);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = "path", message = e.Message });
            }

            // Not the request's token for the file server: a read cut off half way would leave the
            // connection out of step with its replies. A browser that goes away stops the writes.
            using var use = await session.UseAsync(context.RequestAborted);
            OpenedFile file;
            try
            {
                file = await session.Client.OpenForReadAsync(path, CancellationToken.None);
            }
            catch (FileServerException e) when (e.Code >= 0)
            {
                return Refused(e);
            }
            catch (Exception e) when (IsLost(e))
            {
                use.Dispose();
                return await LostAsync(sessions, session, e);
            }

            session.Downloading();
            var response = context.Response;
            response.Headers.CacheControl = "no-store";
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = FileName(path) }.ToString();
            response.ContentType = "application/octet-stream";
            if (file.Size >= 0) response.ContentLength = file.Size;
            try
            {
                await session.Client.CopyToAsync(file, response.Body, file.Size >= 0 ? file.Size : null, CancellationToken.None);
            }
            catch when (context.RequestAborted.IsCancellationRequested)
            {
                // The browser went away part way: the file was closed on the IBM i, and the session stays.
                context.Abort();
            }
            catch (Exception e) when (IsLost(e))
            {
                use.Dispose();
                await sessions.EndAsync(session, "connection lost");
                context.Abort();
            }
            catch (FileServerException)
            {
                // Refused part way (a lock, say): the file was closed; the browser sees the download fail.
                context.Abort();
            }
            return Results.Empty;
        });

        return api;
    }

    /// <summary>The most entries a listing shows.</summary>
    public const int MaxEntries = 2000;

    /// <summary>A folder's path as listed: absolute, no trailing slash but the root's, . and .. done.</summary>
    public static string FolderPath(string? path)
    {
        var parts = new List<string>();
        foreach (var part in (path ?? "").Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return (path ?? "").TrimStart().StartsWith('/') || parts.Count == 0 ? "/" + string.Join('/', parts) : string.Join('/', parts);
    }

    /// <summary>The name a download is saved as: the path's last part.</summary>
    public static string FileName(string path)
    {
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? "download" : name;
    }

    private static IResult Refused(FileServerException e) => e.Code switch
    {
        2 or 3 => Results.NotFound(new { error = "not-found", code = e.Code, message = e.Message }),
        4 => Results.BadRequest(new { error = "folder", code = e.Code, message = e.Message }),
        5 or 13 => Results.Json(new { error = "not-authorised", code = e.Code, message = e.Message }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new { error = "file-server", code = e.Code, message = e.Message }, statusCode: StatusCodes.Status502BadGateway),
    };

    /// <summary>The connection to the file server is gone or out of step: the session can't be used again.</summary>
    private static bool IsLost(Exception e) =>
        e is IOException or EndOfStreamException or SocketException or ObjectDisposedException
        || e is FileServerException { Code: < 0 };

    private static async Task<IResult> LostAsync(FileSessions sessions, FileSession session, Exception e)
    {
        await sessions.EndAsync(session, "connection lost");
        var why = e is FileServerException ? e.Message : "The IBM i's file server closed the connection.";
        return Results.Conflict(new { error = "signon-needed", message = $"{why} Sign on again." });
    }

    private static IResult NoSystem() => Results.NotFound(new { error = "unknown-target", message = "No such system." });

    private static IResult SignOnNeeded() => Results.Conflict(new { error = "signon-needed", message = "Sign on to download files." });
}
