using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub.Activities;

public static class ActivityData
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<ActivityAccount>().ToTable("ActivityAccounts").HasKey(x => x.IdentityId);
        b.Entity<ActivityAccount>().HasOne<JoinIdentity>().WithMany().HasForeignKey(x => x.IdentityId);
        b.Entity<ActivityReceipt>().ToTable("ActivityReceipts").HasKey(x => new { x.Instance, x.EventId });
        b.Entity<ActivityDelivery>().ToTable("ActivityDeliveries").HasIndex(x => new { x.IdentityId, x.Instance, x.AwardId }).IsUnique();
        b.Entity<ActivityDelivery>().HasIndex(x => new { x.Instance, x.IdentityId, x.AppliedAt });
        b.Entity<ActivityShowcase>().ToTable("ActivityShowcases").HasIndex(x => new { x.IdentityId, x.Instance, x.Month }).IsUnique();
    }
    public static Task Initialize(HubDb db, CancellationToken ct = default) => db.Database.ExecuteSqlRawAsync(SchemaSql, ct);
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS "ActivityAccounts" ("IdentityId" uuid PRIMARY KEY REFERENCES "JoinIdentities"("Id"), "StateJson" text NOT NULL DEFAULT '{{}}');
        CREATE TABLE IF NOT EXISTS "ActivityReceipts" ("Instance" text NOT NULL, "EventId" uuid NOT NULL, "IdentityId" uuid NOT NULL, "ReceivedAt" timestamptz NOT NULL, PRIMARY KEY ("Instance", "EventId"));
        CREATE TABLE IF NOT EXISTS "ActivityDeliveries" ("Id" uuid PRIMARY KEY, "IdentityId" uuid NOT NULL, "Instance" text NOT NULL, "AwardId" text NOT NULL, "ItemsJson" text NOT NULL, "CreatedAt" timestamptz NOT NULL, "AppliedAt" timestamptz NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ActivityDeliveries_IdentityId_Instance_AwardId" ON "ActivityDeliveries" ("IdentityId", "Instance", "AwardId");
        CREATE INDEX IF NOT EXISTS "IX_ActivityDeliveries_Instance_IdentityId_AppliedAt" ON "ActivityDeliveries" ("Instance", "IdentityId", "AppliedAt");
        CREATE TABLE IF NOT EXISTS "ActivityShowcases" ("Id" uuid PRIMARY KEY, "IdentityId" uuid NOT NULL, "Instance" text NOT NULL, "Month" text NOT NULL, "Stage" text NOT NULL, "Text" text NOT NULL, "Status" text NOT NULL, "CreatedAt" timestamptz NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ActivityShowcases_IdentityId_Instance_Month" ON "ActivityShowcases" ("IdentityId", "Instance", "Month");
        """;
}
