using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public static class Admin
{
    public static async Task Run(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        if (args.Length == 0) throw new ArgumentException("admin init | invite-create single|super [exactGameName|-] [days] | invite-revoke id | invite-list | invite-uses id | protect gameName | disable loginName | reset loginName");
        switch (args[0]) {
            case "init": await db.Database.EnsureCreatedAsync(); Console.WriteLine("Database ready."); break;
            case "invite-create": {
                if (args.Length < 2 || args[1] is not ("single" or "super")) throw new ArgumentException("single or super required");
                var bound = args.Length > 2 && args[2] != "-" ? args[2] : null;
                if (bound is not null && (!Secret.GameNamePattern().IsMatch(bound) || args[1] == "super")) throw new ArgumentException("Only single invites may bind a valid exact game name");
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
