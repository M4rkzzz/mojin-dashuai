using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Boshan.Hub;

public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, HubDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length != 71) return AuthenticateResult.NoResult();
        var hash = Secret.Hash(header[7..]);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccessHash == hash && x.RevokedAt == null && x.AccessExpiresAt > DateTimeOffset.UtcNow);
        if (session is null) return AuthenticateResult.Fail("Session expired");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.UserId && !x.Disabled);
        if (user is null) return AuthenticateResult.Fail("Account unavailable");
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString()), new("session", session.Id.ToString()), new("gameName", user.GameName)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
