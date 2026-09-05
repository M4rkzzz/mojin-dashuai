using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public sealed record RegisterRequest(string LoginName, string GameName, string Password, string Invitation);
public sealed record LoginRequest(string LoginName, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
public sealed record RecoverRequest(string LoginName, string Code, string NewPassword);
public sealed record Profile(Guid Id, string LoginName, string GameName);
public sealed record LoginResult(Profile Profile, string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, string? RecoveryCode = null);
public sealed class HubError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

public static partial class Secret
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string NameKey(string name) => name.ToUpperInvariant();
    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)] public static partial Regex GameNamePattern();
    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$", RegexOptions.CultureInvariant)] public static partial Regex LoginPattern();
}

public sealed class AccountService(HubDb db, UserManager<HubUser> users)
{
    public async Task<LoginResult> Register(RegisterRequest request)
    {
        if (!Secret.LoginPattern().IsMatch(request.LoginName) || !Secret.GameNamePattern().IsMatch(request.GameName))
            throw new HubError("登录名需为 3–32 位字母、数字或 _.-；游戏名需为 3–16 位字母、数字或下划线。");
        CheckPassword(request.Password);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var hash = Secret.Hash(request.Invitation);
        // The database lock serializes registration with both consumption and administrative revocation.
        var invite = await db.Invitations.FromSqlInterpolated($"SELECT * FROM \"Invitations\" WHERE \"CodeHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync();
        if (invite is null || invite.Revoked || invite.ExpiresAt <= DateTimeOffset.UtcNow || (!invite.Reusable && invite.UseCount != 0))
            throw new HubError("邀请码无效、已使用或已撤销。");
        if (invite.BoundGameName is not null && invite.BoundGameName != request.GameName)
            throw new HubError("此邀请码只能认领绑定的原游戏名，请保留大小写。");
        var nameKey = Secret.NameKey(request.GameName);
        var reserved = await db.ProtectedNames.FindAsync(nameKey);
        if (reserved is not null && (invite.Reusable || invite.BoundGameName != reserved.ExactName || request.GameName != reserved.ExactName))
            throw new HubError("此游戏名已受保护，请使用绑定原名的单次邀请码。");
        if (await users.FindByNameAsync(request.LoginName) is not null || await db.Users.AnyAsync(x => x.GameNameKey == nameKey))
            throw new HubError("登录名或游戏名已被使用。");
        var recovery = Secret.New();
        var user = new HubUser { Id = Guid.NewGuid(), UserName = request.LoginName, GameName = request.GameName, GameNameKey = nameKey, RecoveryHash = Secret.Hash(recovery) };
        Ensure(await users.CreateAsync(user, request.Password));
        invite.UseCount++;
        db.InviteUses.Add(new InviteUse { InvitationId = invite.Id, UserId = user.Id });
        var result = NewSession(user, recoveryCode: recovery);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<LoginResult> Login(LoginRequest request)
    {
        var user = await users.FindByNameAsync(request.LoginName);
        if (user is null)
        {
            // Match the expensive password verification path for unknown accounts.
            new PasswordHasher<HubUser>().HashPassword(new HubUser(), request.Password);
            throw new HubError("账号或密码错误。", 401);
        }
        if (user.Disabled || await users.IsLockedOutAsync(user)) throw new HubError("账号或密码错误，或账号暂时不可用。", 401);
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            throw new HubError("账号或密码错误。", 401);
        }
        await users.ResetAccessFailedCountAsync(user);
        var result = NewSession(user);
        await db.SaveChangesAsync();
        return result;
    }

    public async Task<LoginResult> Refresh(string token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var hash = Secret.Hash(token);
        var old = await db.Sessions.FromSqlInterpolated($"SELECT * FROM \"Sessions\" WHERE \"RefreshHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync();
        if (old is null) throw new HubError("登录已过期，请重新登录。", 401);
        if (old.RevokedAt is not null)
        {
            await db.Sessions.Where(x => x.FamilyId == old.FamilyId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
            await tx.CommitAsync();
            throw new HubError("会话已被使用，请重新登录。", 401);
        }
        var user = await users.FindByIdAsync(old.UserId.ToString());
        if (old.RefreshExpiresAt <= DateTimeOffset.UtcNow || user is null || user.Disabled) throw new HubError("登录已过期，请重新登录。", 401);
        old.RevokedAt = DateTimeOffset.UtcNow;
        var result = NewSession(user, old.FamilyId);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return result;
    }

    public async Task ChangePassword(Guid userId, PasswordRequest request)
    {
        CheckPassword(request.NewPassword);
        await using var tx = await db.Database.BeginTransactionAsync();
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new HubError("登录已过期。", 401);
        Ensure(await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword));
        await RevokeAll(userId);
        await tx.CommitAsync();
    }

    public async Task<string> Recover(RecoverRequest request)
    {
        CheckPassword(request.NewPassword);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var normalized = users.NormalizeName(request.LoginName);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedUserName\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();
        var hash = Secret.Hash(request.Code);
        if (user is null || user.Disabled) throw new HubError("账号或恢复凭据无效。");
        var grant = await db.ResetGrants.SingleOrDefaultAsync(x => x.UserId == user.Id && x.Hash == hash && !x.Used && x.ExpiresAt > DateTimeOffset.UtcNow);
        if (user.RecoveryHash != hash && grant is null) throw new HubError("账号或恢复凭据无效。");
        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        Ensure(await users.ResetPasswordAsync(user, resetToken, request.NewPassword));
        var recovery = Secret.New();
        user.RecoveryHash = Secret.Hash(recovery);
        // Every outstanding administrator reset grant is invalid after a successful reset.
        await db.ResetGrants.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Used, true));
        await RevokeAll(user.Id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return recovery;
    }

    public async Task<string> RotateRecovery(Guid userId, string password)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new HubError("登录已过期。", 401);
        if (!await users.CheckPasswordAsync(user, password)) throw new HubError("密码错误。", 401);
        var code = Secret.New();
        user.RecoveryHash = Secret.Hash(code);
        Ensure(await users.UpdateAsync(user));
        return code;
    }

    public Task<int> RevokeAll(Guid userId) => db.Sessions.Where(x => x.UserId == userId && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
    private LoginResult NewSession(HubUser user, Guid? family = null, string? recoveryCode = null)
    {
        var access = Secret.New(); var refresh = Secret.New(); var now = DateTimeOffset.UtcNow;
        var session = new HubSession { UserId = user.Id, FamilyId = family ?? Guid.NewGuid(), AccessHash = Secret.Hash(access), RefreshHash = Secret.Hash(refresh), AccessExpiresAt = now.AddMinutes(15), RefreshExpiresAt = now.AddDays(30) };
        db.Sessions.Add(session);
        return new(new Profile(user.Id, user.UserName!, user.GameName), access, session.AccessExpiresAt, refresh, session.RefreshExpiresAt, recoveryCode);
    }
    private static void CheckPassword(string password)
    {
        if (password.Length is < 10 or > 128) throw new HubError("密码长度需为 10–128 个字符。");
    }
    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new HubError("操作未完成，请检查密码及账号信息。", 400);
    }
}
