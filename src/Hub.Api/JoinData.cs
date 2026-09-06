using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public sealed class JoinIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? HubUserId { get; set; }
    public string? MinecraftProfileId { get; set; }
    public string GameName { get; set; } = "";
    public string GameNameKey { get; set; } = "";
    public string GameUuid { get; set; } = "";
    public bool Disabled { get; set; }
}
public sealed class JoinGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IdentityId { get; set; }
    public string AccessHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class JoinTicket
{
    public string TokenHash { get; set; } = "";
    public Guid IdentityId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? GrantId { get; set; }
    public string InstanceId { get; set; } = "";
    public string ExactName { get; set; } = "";
    public string GameUuid { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
public static class JoinData
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<JoinIdentity>().ToTable("JoinIdentities");
        b.Entity<JoinIdentity>().HasIndex(x => x.HubUserId).IsUnique();
        b.Entity<JoinIdentity>().HasIndex(x => x.MinecraftProfileId).IsUnique();
        b.Entity<JoinIdentity>().HasIndex(x => x.GameNameKey).IsUnique();
        b.Entity<JoinIdentity>().HasOne<HubUser>().WithMany().HasForeignKey(x => x.HubUserId);
        b.Entity<JoinGrant>().ToTable("JoinGrants");
        b.Entity<JoinGrant>().HasIndex(x => x.AccessHash).IsUnique();
        b.Entity<JoinGrant>().HasIndex(x => x.ExpiresAt);
        b.Entity<JoinGrant>().HasOne<JoinIdentity>().WithMany().HasForeignKey(x => x.IdentityId);
        b.Entity<JoinTicket>().ToTable("JoinTickets");
        b.Entity<JoinTicket>().HasKey(x => x.TokenHash);
        b.Entity<JoinTicket>().HasIndex(x => x.ExpiresAt);
        b.Entity<JoinTicket>().HasOne<JoinIdentity>().WithMany().HasForeignKey(x => x.IdentityId);
    }

    // Existing installations predate EF migrations. This additive schema is safe to rerun;
    // no existing account/world identifier is rewritten.
    public static async Task Initialize(HubDb db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(SchemaSql, ct);
        var users = await db.Users.AsNoTracking().Where(u => !db.Set<JoinIdentity>().Any(i => i.HubUserId == u.Id)).ToListAsync(ct);
        foreach (var u in users)
        {
            var identity = JoinSecurity.ForHubUser(u);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"JoinIdentities\" (\"Id\",\"HubUserId\",\"MinecraftProfileId\",\"GameName\",\"GameNameKey\",\"GameUuid\",\"Disabled\") VALUES ({identity.Id},{identity.HubUserId},NULL,{identity.GameName},{identity.GameNameKey},{identity.GameUuid},false) ON CONFLICT DO NOTHING", ct);
        }
    }
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS "JoinIdentities" (
          "Id" uuid PRIMARY KEY, "HubUserId" uuid NULL REFERENCES "AspNetUsers"("Id"),
          "MinecraftProfileId" text NULL, "GameName" text NOT NULL, "GameNameKey" text NOT NULL,
          "GameUuid" text NOT NULL, "Disabled" boolean NOT NULL DEFAULT false);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_JoinIdentities_HubUserId" ON "JoinIdentities" ("HubUserId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_JoinIdentities_MinecraftProfileId" ON "JoinIdentities" ("MinecraftProfileId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_JoinIdentities_GameNameKey" ON "JoinIdentities" ("GameNameKey");
        CREATE TABLE IF NOT EXISTS "JoinGrants" (
          "Id" uuid PRIMARY KEY, "IdentityId" uuid NOT NULL REFERENCES "JoinIdentities"("Id"),
          "AccessHash" text NOT NULL, "ExpiresAt" timestamptz NOT NULL, "RevokedAt" timestamptz NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_JoinGrants_AccessHash" ON "JoinGrants" ("AccessHash");
        CREATE INDEX IF NOT EXISTS "IX_JoinGrants_ExpiresAt" ON "JoinGrants" ("ExpiresAt");
        CREATE TABLE IF NOT EXISTS "JoinTickets" (
          "TokenHash" text PRIMARY KEY, "IdentityId" uuid NOT NULL REFERENCES "JoinIdentities"("Id"),
          "SessionId" uuid NULL, "GrantId" uuid NULL, "InstanceId" text NOT NULL,
          "ExactName" text NOT NULL, "GameUuid" text NOT NULL,
          "ExpiresAt" timestamptz NOT NULL, "ConsumedAt" timestamptz NULL);
        CREATE INDEX IF NOT EXISTS "IX_JoinTickets_ExpiresAt" ON "JoinTickets" ("ExpiresAt");
        """;
}
