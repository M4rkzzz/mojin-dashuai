using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public static class Admin
{
    public static async Task Run(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        if (args.Length == 0) throw new ArgumentException("admin init | invite-create single|super [exactGameName|-] [days] | invite-revoke id | invite-list | invite-uses id | protect gameName | protect-conflict variant1 variant2 | disable loginName | reset loginName");
        switch (args[0]) {
            case "init": await db.Database.EnsureCreatedAsync(); await JoinData.Initialize(db); Console.WriteLine("Database ready."); break;
            case "join-init": await JoinData.Initialize(db); Console.WriteLine("Join schema ready; existing Hub identities retained."); break;
            case "join-list":
                foreach (var i in await db.Set<JoinIdentity>().AsNoTracking().OrderBy(x => x.GameNameKey).ToListAsync())
                    Console.WriteLine($"{i.GameName} hub={i.HubUserId is not null} minecraft={i.MinecraftProfileId is not null} disabled={i.Disabled} uuid={i.GameUuid}");
                break;
            case "join-protected":
                foreach (var n in await db.ProtectedNames.AsNoTracking().Where(n => !db.Set<JoinIdentity>().Any(i => i.GameNameKey == n.Key && i.MinecraftProfileId != null)).OrderBy(n => n.Key).ToListAsync())
                    Console.WriteLine($"{(n.ExactName.Length == 0 ? n.Key + " [case conflict]" : n.ExactName)} minecraft-unlinked");
                break;
            case "join-bind-minecraft": {
                if (args.Length is < 3 or > 4 || !Guid.TryParse(args[1], out var profileId) || profileId == Guid.Empty || !Secret.GameNamePattern().IsMatch(args[2]))
                    throw new ArgumentException("join-bind-minecraft official-profile-uuid exactGameName [--link-existing-hub]; verify official identity and legacy ownership first");
                var exactName = args[2]; var key = Secret.NameKey(exactName); var minecraftId = profileId.ToString("N");
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                var identities = db.Set<JoinIdentity>();
                var byProvider = await identities.SingleOrDefaultAsync(i => i.MinecraftProfileId == minecraftId);
                if (byProvider is not null && byProvider.GameName != exactName) throw new ArgumentException("Official identity is already bound to another exact name. No automatic rename.");
                var identity = await identities.SingleOrDefaultAsync(i => i.GameNameKey == key);
                if (identity is not null && (identity.GameName != exactName || (identity.MinecraftProfileId is not null && identity.MinecraftProfileId != minecraftId)))
                    throw new ArgumentException("Existing exact-name/provider conflict; no binding changed.");
                if (identity?.HubUserId is not null && identity.MinecraftProfileId != minecraftId && (args.Length != 4 || args[3] != "--link-existing-hub"))
                    throw new ArgumentException("This name belongs to a Hub account. Independently confirm same owner before --link-existing-hub.");
                if (identity is null)
                {
                    if (await db.Users.AnyAsync(u => u.GameNameKey == key)) throw new ArgumentException("Run join-init first; existing Hub account must be explicitly linked.");
                    identity = new() { GameName = exactName, GameNameKey = key, GameUuid = JoinSecurity.OfflineUuid(exactName) };
                    identities.Add(identity);
                }
                identity.MinecraftProfileId = minecraftId;
                await db.SaveChangesAsync(); await tx.CommitAsync();
                Console.WriteLine($"Minecraft identity bound: {identity.GameName}; existing offline UUID retained: {identity.GameUuid}"); break;
            }
            case "invite-create": {
                if (args.Length < 2 || args[1] is not ("single" or "super")) throw new ArgumentException("single or super required");
                var bound = args.Length > 2 && args[2] != "-" ? args[2] : null;
                if (bound is not null && (!Secret.GameNamePattern().IsMatch(bound) || args[1] == "super")) throw new ArgumentException("Only single invites may bind a valid exact game name");
                if (bound is not null && (await db.ProtectedNames.FindAsync(Secret.NameKey(bound)))?.ExactName == "") throw new ArgumentException("This name has an unresolved case conflict and cannot be claimed.");
                var code = Secret.New();
                var invite = new Invitation { CodeHash = Secret.Hash(code), Reusable = args[1] == "super", BoundGameName = bound, ExpiresAt = args.Length > 3 ? DateTimeOffset.UtcNow.AddDays(int.Parse(args[3])) : null };
                db.Invitations.Add(invite); await db.SaveChangesAsync();
                Console.WriteLine($"Invitation ID: {invite.Id}\nCode (shown once): {code}"); break;
            }
            case "invite-revoke": {
                var id = Guid.Parse(args[1]); var count = await db.Invitations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true));
                if (count == 0) throw new ArgumentException("Invitation not found"); Console.WriteLine("Revoked. Existing accounts unchanged."); break;
            }
            case "invite-list":
                foreach(var i in await db.Invitations.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync()) Console.WriteLine($"{i.Id} {(i.Reusable ? "super" : "single")} uses={i.UseCount} revoked={i.Revoked} expires={i.ExpiresAt:O} bound={i.BoundGameName}"); break;
            case "invite-uses": {
                var id = Guid.Parse(args[1]);
                foreach(var u in await db.InviteUses.Where(x => x.InvitationId == id).Join(db.Users, x => x.UserId, x => x.Id, (use, user) => new { use.CreatedAt, user.UserName, user.GameName }).ToListAsync()) Console.WriteLine($"{u.CreatedAt:O} {u.UserName} {u.GameName}"); break;
            }
            case "protect": {
                if (!Secret.GameNamePattern().IsMatch(args[1])) throw new ArgumentException("Invalid game name");
                var key = Secret.NameKey(args[1]);
                if (await db.ProtectedNames.FindAsync(key) is null) { db.ProtectedNames.Add(new() { Key = key, ExactName = args[1] }); await db.SaveChangesAsync(); }
                Console.WriteLine("Protected."); break;
            }
            case "protect-conflict": {
                var variants = args.Skip(1).Distinct(StringComparer.Ordinal).ToArray();
                if (variants.Length < 2 || variants.Any(x => !Secret.GameNamePattern().IsMatch(x)) || variants.Select(Secret.NameKey).Distinct().Count() != 1)
                    throw new ArgumentException("Provide at least two valid case variants of the same game name.");
                var key = Secret.NameKey(variants[0]);
                if (await db.Users.AnyAsync(x => x.GameNameKey == key)) throw new ArgumentException("Name already claimed; existing account requires manual review.");
                var reserved = await db.ProtectedNames.FindAsync(key);
                if (reserved is null) db.ProtectedNames.Add(new() {Key=key,ExactName=""});
                else reserved.ExactName = "";
                await db.SaveChangesAsync();
                Console.WriteLine("Case-conflicting name reserved. No game profile was selected or changed."); break;
            }
            case "disable": case "reset": {
                var key = args[1].ToUpperInvariant(); var user = await db.Users.SingleAsync(x => x.NormalizedUserName == key);
                await using var tx = await db.Database.BeginTransactionAsync();
                if (args[0] == "disable") { user.Disabled = true; Console.WriteLine("Account disabled."); }
                else { var code = Secret.New(); db.ResetGrants.Add(new() { UserId = user.Id, Hash = Secret.Hash(code), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) }); Console.WriteLine($"One-time reset credential (30 min): {code}"); }
                await scope.ServiceProvider.GetRequiredService<AccountService>().RevokeAll(user.Id);
                await db.SaveChangesAsync(); await tx.CommitAsync(); break;
            }
            default: throw new ArgumentException("Unknown admin command");
        }
    }
}
