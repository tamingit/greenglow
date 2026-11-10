using System.Globalization;
using System.Text.RegularExpressions;
using GreenGlow.Server.Auth;
using GreenGlow.Server.People;
using GreenGlow.Server.Sessions;
using GreenGlow.Server.Web;
using Microsoft.Net.Http.Headers;

namespace GreenGlow.Server.Printing;

/// <summary>
/// /api/printer/{connection}: the user's own spooled files on a system, as PDFs.
/// The same access as sessions: a system the user may not use answers like one that doesn't
/// exist. Changes need an allowed Origin (OriginCheck), like the rest of /api.
/// </summary>
public static partial class PrinterEndpoints
{
    public sealed record SignOnRequest(string? User, string? Password);

    [GeneratedRegex("^[0-9a-f]{24}$")]
    private static partial Regex FileId();

    public static RouteGroupBuilder MapPrinterEndpoints(this RouteGroupBuilder api)
    {
        var printer = api.MapGroup("/printer/{connection}");

        // Whether the user is signed on there, and as whom; otherwise the IBM i user to offer,
        // from the user's own session on that system.
        printer.MapGet("", async (string connection, HttpContext context, Access access, PrintSessions prints,
            SessionManager sessions) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            var session = prints.Find(user.Id, connection);
            var suggested = sessions.All
                .Where(s => s.Owner.Id == user.Id && s.Target.Name == connection && !string.IsNullOrEmpty(s.IbmUser))
                .Select(s => s.IbmUser).FirstOrDefault();
            return Results.Json(new { signedOn = session is not null, ibmUser = session?.IbmUser, suggestedUser = suggested });
        });

        printer.MapPost("/signon", async (string connection, SignOnRequest request, HttpContext context, Access access,
            PrintSessions prints) =>
        {
            var user = CurrentUser.From(context.User)!;
            var target = await access.FindAsync(user, connection);
            if (target is null) return NoSystem();
            try
            {
                var session = await prints.SignOnAsync(user, target, ClientAddress.Of(context), request.User ?? "",
                    request.Password ?? "", context.RequestAborted);
                return Results.Json(new { signedOn = true, ibmUser = session.IbmUser });
            }
            catch (SignOnException e)
            {
                var status = e.Failure switch
                {
                    SignOnFailure.InvalidUser or SignOnFailure.InvalidPassword when e.Code == 0 => StatusCodes.Status400BadRequest,
                    SignOnFailure.Unreachable or SignOnFailure.Protocol => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status422UnprocessableEntity,
                };
                return Results.Json(new { error = Kebab(e.Failure), message = e.Message }, statusCode: status);
            }
            catch (PrintServerException e)
            {
                return Results.Json(new { error = "print-server", message = e.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting(RateLimiting.SignOn);

        printer.MapDelete("/signon", async (string connection, HttpContext context, PrintSessions prints) =>
        {
            var user = CurrentUser.From(context.User)!;
            await prints.SignOffAsync(user.Id, connection, "signed off");
            return Results.NoContent();
        });

        printer.MapGet("/files", async (string connection, HttpContext context, Access access, PrintSessions prints) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            if (prints.Find(user.Id, connection) is not { } session) return SignOnNeeded();
            try
            {
                // Not the request's token: a list or read cut off half way would leave the
                // connection out of step with its replies.
                var files = await session.Client.ListAsync(CancellationToken.None);
                return Results.Json(new
                {
                    ibmUser = session.IbmUser,
                    files = files.OrderByDescending(f => f.Created ?? DateTime.MinValue).ThenByDescending(f => f.Number)
                        .Select(f => Describe(session.IdFor(f), f)),
                });
            }
            catch (PrintServerException e)
            {
                return await Failed(prints, session, e);
            }
        });

        printer.MapGet("/files/{id}", async (string connection, string id, HttpContext context, Access access,
            PrintSessions prints) =>
        {
            var user = CurrentUser.From(context.User)!;
            if (await access.FindAsync(user, connection) is null) return NoSystem();
            if (prints.Find(user.Id, connection) is not { } session) return SignOnNeeded();
            if (!FileId().IsMatch(id) || session.File(id) is not { } file)
                return Results.NotFound(new { error = "unknown-file", message = "That spooled file isn't in your list. Refresh the list." });
            if (!SpooledPdf.CanOpen(file.DeviceType)) return CannotOpen(file);
            try
            {
                var data = await session.Client.ReadAllAsync(file, CancellationToken.None);
                var pdf = SpooledPdf.From(file.DeviceType, data, $"{file.Name} {file.JobNumber}/{file.JobUser}/{file.JobName} {file.Number}");
                if (pdf is null) return CannotOpen(file);
                session.Opened();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
                {
                    FileNameStar = $"{file.Name}-{file.JobNumber}-{file.Number}.pdf",
                }.ToString();
                return Results.Bytes(pdf, "application/pdf");
            }
            catch (PrintServerException e)
            {
                return await Failed(prints, session, e);
            }
        });

        return api;
    }

    private static object Describe(string id, SpooledFile f) => new
    {
        id,
        name = f.Name,
        number = f.Number,
        job = $"{f.JobNumber}/{f.JobUser}/{f.JobName}",
        jobSystem = f.JobSystem,
        created = f.Created?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        status = f.Status,
        pages = f.Pages,
        userData = f.UserData,
        type = f.DeviceType,
        outputQueue = f.OutputQueue,
        formType = f.FormType,
        canOpen = SpooledPdf.CanOpen(f.DeviceType),
    };

    private static IResult NoSystem() => Results.NotFound(new { error = "unknown-target", message = "No such system." });

    private static IResult SignOnNeeded() => Results.Conflict(new
    {
        error = "signon-needed",
        message = "Sign on to see your printer output.",
    });

    private static IResult CannotOpen(SpooledFile file) => Results.Json(new
    {
        error = "cannot-open",
        message = $"GreenGlow can't show {file.DeviceType} spooled files. Open it in ACS.",
    }, statusCode: StatusCodes.Status415UnsupportedMediaType);

    /// <summary>A lost connection ends the print session, so the user signs on again.</summary>
    private static async Task<IResult> Failed(PrintSessions prints, PrintSession session, PrintServerException e)
    {
        if (!e.Lost)
            return Results.Json(new { error = "print-server", message = e.Message }, statusCode: StatusCodes.Status502BadGateway);
        await prints.EndAsync(session, "connection lost");
        return Results.Conflict(new { error = "signon-needed", message = $"{e.Message} Sign on again." });
    }

    private static string Kebab(SignOnFailure failure) =>
        Regex.Replace(failure.ToString(), "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();
}
