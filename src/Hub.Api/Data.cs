using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public sealed class HubUser : IdentityUser<Guid>
{
    public string GameName { get; set; } = "";
    public string GameNameKey { get; set; } = "";
    public bool Disabled { get; set; }
    public string RecoveryHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CodeHash { get; set; } = "";
    public bool Reusable { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? BoundGameName { get; set; }
    public long UseCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class InviteUse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvitationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ProtectedName
{
    public string Key { get; set; } = "";
    // Empty reserves every case variant until the legacy identity conflict is reviewed.
    public string ExactName { get; set; } = "";
}
public sealed class HubSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string AccessHash { get; set; } = "";
    public string RefreshHash { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class ResetGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
}
public sealed class HubDb(DbContextOptions<HubDb> options) : IdentityDbContext<HubUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<InviteUse> InviteUses => Set<InviteUse>();
    public DbSet<ProtectedName> ProtectedNames => Set<ProtectedName>();
    public DbSet<HubSession> Sessions => Set<HubSession>();
    public DbSet<ResetGrant> ResetGrants => Set<ResetGrant>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        JoinData.Configure(builder);
        builder.Entity<HubUser>().HasIndex(x => x.GameNameKey).IsUnique();
        builder.Entity<HubUser>().Property(x => x.GameName).HasMaxLength(16);
        builder.Entity<HubUser>().Property(x => x.GameNameKey).HasMaxLength(16);
        builder.Entity<ProtectedName>().HasKey(x => x.Key);
        builder.Entity<Invitation>().HasIndex(x => x.CodeHash).IsUnique();
        builder.Entity<HubSession>().HasIndex(x => x.AccessHash).IsUnique();
        builder.Entity<HubSession>().HasIndex(x => x.RefreshHash).IsUnique();
        builder.Entity<HubSession>().HasIndex(x => x.FamilyId);
        builder.Entity<HubSession>().HasOne<HubUser>().WithMany().HasForeignKey(x => x.UserId);
        builder.Entity<InviteUse>().HasOne<HubUser>().WithMany().HasForeignKey(x => x.UserId);
        builder.Entity<InviteUse>().HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId);
        builder.Entity<ResetGrant>().HasIndex(x => x.Hash).IsUnique();
    }
}
