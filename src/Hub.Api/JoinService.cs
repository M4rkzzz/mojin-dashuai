using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Boshan.Hub;

public sealed record JoinTicketRequest(string Instance);
public sealed record MinecraftExchangeRequest(string AccessToken);
public sealed record JoinRedeemRequest(string Ticket, string Instance, string GameName);
public sealed record JoinTicketResult(string Ticket, DateTimeOffset ExpiresAt, string GameName, string GameUuid);
public sealed record JoinGrantResult(string AccessToken, DateTimeOffset ExpiresAt, string GameName, string GameUuid);
public sealed record JoinRedeemResult(bool Allowed, string GameName, string GameUuid);

public sealed class JoinService(HubDb db, MinecraftJoinVerifier minecraft, IConfiguration config, JoinRequestLimits limits)
{
    public async Task<JoinGrantResult> Exchange(string token, CancellationToken ct)
    {
        var verified = await minecraft.Verify(token, ct);
        limits.Take("minecraft:" + verified.ProfileId, 12);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var identities = db.Set<JoinIdentity>();
        var identity = await identities.SingleOrDefaultAsync(i => i.MinecraftProfileId == verified.ProfileId, ct);
        if (identity is null)
        {
            var key = Secret.NameKey(verified.GameName);
            var legacy = await db.ProtectedNames.SingleOrDefaultAsync(n => n.Key == key, ct);
            // A protected legacy name is not an account binding. Official token + Java
            // ownership proof above may claim its exact spelling and retain its offline
            // UUID. Ambiguous casing and existing account ownership still require review.
            if (await identities.AnyAsync(i => i.GameNameKey == key, ct)
                || await db.Users.AnyAsync(u => u.GameNameKey == key, ct)
                || (legacy is not null && legacy.ExactName != verified.GameName))
                throw Conflict();
            identity = new() { MinecraftProfileId = verified.ProfileId, GameName = verified.GameName, GameNameKey = key, GameUuid = JoinSecurity.OfflineUuid(verified.GameName) };
            identities.Add(identity);
        }
        // Official rename is deliberately not a player-data migration or automatic rebind.
        if (identity.Disabled || identity.GameName != verified.GameName ||
            (identity.HubUserId is { } user && !await db.Users.AnyAsync(u => u.Id == user && !u.Disabled, ct))) throw Conflict();
        var access = Secret.New();
        var grant = new JoinGrant { IdentityId = identity.Id, AccessHash = Secret.Hash(access), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        db.Set<JoinGrant>().Add(grant);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(access, grant.ExpiresAt, identity.GameName, identity.GameUuid);
    }

    public async Task<JoinTicketResult> Issue(string bearer, string instance, CancellationToken ct)
    {
        if (!JoinSecurity.ValidInstance(instance)) throw new HubError("未知服务器。", 400);
        if (!JoinSecurity.ValidBearer(bearer)) throw LoginExpired();
        var now = DateTimeOffset.UtcNow;
        var hash = Secret.Hash(bearer);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.AccessHash == hash && s.RevokedAt == null && s.AccessExpiresAt > now, ct);
        JoinIdentity? identity;
        Guid? grantId = null;
        if (session is not null)
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && !u.Disabled, ct) ?? throw LoginExpired();
            identity = await db.Set<JoinIdentity>().SingleOrDefaultAsync(i => i.HubUserId == user.Id, ct);
            if (identity is null)
            {
                if (await db.Set<JoinIdentity>().AnyAsync(i => i.GameNameKey == user.GameNameKey, ct)) throw Conflict();
                identity = JoinSecurity.ForHubUser(user);
                db.Set<JoinIdentity>().Add(identity);
            }
            if (identity.GameName != user.GameName) throw Conflict();
        }
        else
        {
            var grant = await db.Set<JoinGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.AccessHash == hash && g.RevokedAt == null && g.ExpiresAt > now, ct) ?? throw LoginExpired();
            grantId = grant.Id;
            identity = await db.Set<JoinIdentity>().SingleOrDefaultAsync(i => i.Id == grant.IdentityId, ct) ?? throw LoginExpired();
        }
        if (identity.Disabled || (identity.HubUserId is { } hubId && !await db.Users.AnyAsync(u => u.Id == hubId && !u.Disabled, ct))) throw LoginExpired();
        limits.Take("issue:" + identity.Id, 30);
        var raw = JoinSecurity.NewTicket();
        var ticket = new JoinTicket { TokenHash = Secret.Hash(raw), IdentityId = identity.Id, SessionId = session?.Id, GrantId = grantId, InstanceId = instance, ExactName = identity.GameName, GameUuid = identity.GameUuid, ExpiresAt = now.AddSeconds(120) };
        db.Set<JoinTicket>().Add(ticket);
        await db.SaveChangesAsync(ct);
        return new(raw, ticket.ExpiresAt, ticket.ExactName, ticket.GameUuid);
    }

    public async Task<JoinRedeemResult> Redeem(string serverKey, JoinRedeemRequest request, CancellationToken ct)
    {
        if (!JoinSecurity.ValidInstance(request.Instance)) throw new HubError("无效的服务器凭据。", 403);
        var expected = config["JoinAuth:ServerKeys:" + request.Instance];
        if (string.IsNullOrEmpty(expected) || expected.Length < 32 || serverKey.Length > 256 || !JoinSecurity.FixedEquals(expected, serverKey))
            throw new HubError("无效的服务器凭据。", 403);
        limits.Take("redeem:" + request.Instance, 240);
        if (!JoinSecurity.ValidTicket(request.Ticket) || !Secret.GameNamePattern().IsMatch(request.GameName ?? "")) throw TicketInvalid();
        var hash = Secret.Hash(request.Ticket);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await db.Set<JoinTicket>().FromSqlInterpolated($"SELECT * FROM \"JoinTickets\" WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (ticket is null) throw TicketInvalid();
        var identity = await db.Set<JoinIdentity>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == ticket.IdentityId, ct);
        var now = DateTimeOffset.UtcNow;
        if (identity is null || !JoinSecurity.Matches(ticket, identity, request.Instance, request.GameName!, now)) throw TicketInvalid();
        if (identity.HubUserId is { } userId && !await db.Users.AnyAsync(u => u.Id == userId && !u.Disabled && u.GameName == identity.GameName, ct)) throw TicketInvalid();
        if (ticket.SessionId is { } sessionId)
        {
            if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.UserId == identity.HubUserId && s.RevokedAt == null && s.AccessExpiresAt > now, ct)) throw TicketInvalid();
        }
        else if (ticket.GrantId is not { } grantId || !await db.Set<JoinGrant>().AnyAsync(g => g.Id == grantId && g.IdentityId == identity.Id && g.RevokedAt == null && g.ExpiresAt > now, ct)) throw TicketInvalid();
        ticket.ConsumedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, identity.GameName, identity.GameUuid);
    }
    private static HubError Conflict() => new("此角色已有身份绑定、受保护或名称已更改，请联系管理员核实关联。", 409);
    private static HubError TicketInvalid() => new("入服凭据无效、已过期或已使用，请重新连接。", 403);
    private static HubError LoginExpired() => new("登录已过期或账号不可用，请回统一客户端重新登录。", 401);
}

// Bounded per-identity/per-service accounting; players behind one FRP address do not share a quota.
public sealed class JoinRequestLimits
{
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> windows = new();
    private readonly object gate = new();
    public void Take(string key, int permitLimit)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (windows.Count > 2048) foreach (var stale in windows.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray()) windows.Remove(stale);
            if (!windows.TryGetValue(key, out var value) || now - value.Start >= TimeSpan.FromMinutes(1)) value = (now, 0);
            if (value.Count >= permitLimit || windows.Count > 20000) throw new HubError("入服请求过于频繁，请稍后重试。", 429);
            windows[key] = (value.Start, value.Count + 1);
        }
    }
}

public sealed class JoinCleanup(IServiceScopeFactory scopes, ILogger<JoinCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDb>();
                var cut = DateTimeOffset.UtcNow.AddMinutes(-10);
                await db.Set<JoinTicket>().Where(t => t.ExpiresAt < cut).ExecuteDeleteAsync(stoppingToken);
                await db.Set<JoinGrant>().Where(g => g.ExpiresAt < cut).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Join authorization cleanup postponed."); }
        }
    }
}
