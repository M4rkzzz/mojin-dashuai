using System.Security.Claims;
using System.Threading.RateLimiting;
using Boshan.Hub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddDbContext<HubDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Hub") ?? throw new InvalidOperationException("ConnectionStrings__Hub is required")));
builder.Services.AddIdentityCore<HubUser>(o => {
    o.Password.RequiredLength = 10; o.Password.RequireDigit = false; o.Password.RequireUppercase = false;
    o.Password.RequireLowercase = false; o.Password.RequireNonAlphanumeric = false;
    o.Lockout.MaxFailedAccessAttempts = 8; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<HubDb>().AddDefaultTokenProviders();
var keyPath = builder.Configuration["DataProtectionPath"];
if (!string.IsNullOrEmpty(keyPath)) builder.Services.AddDataProtection().SetApplicationName("Boshan.Hub").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddScoped<AccountService>();
builder.Services.AddAuthentication("session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("session", _ => {});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => {
        // CF-Connecting-IP is trusted only in the dedicated tunnel-only deployment; direct API port binds to loopback.
        var ip = builder.Configuration.GetValue<bool>("TrustCloudflareTunnel") && ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) ? cf.ToString() : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 });
    });
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
var app = builder.Build();
if (args.FirstOrDefault() == "admin") {
    await Admin.Run(app.Services, args.Skip(1).ToArray()); return;
}
if (builder.Configuration.GetValue<bool>("InitializeDatabase")) {
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HubDb>().Database.EnsureCreatedAsync();
}
app.Use(async (context, next) => {
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(); }
    catch (HubError ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (DbUpdateException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "账号或游戏名已被使用，请检查后重试。" }); }
    catch (PostgresException ex) when (ex.SqlState == "40001") { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "操作冲突，请重试。" }); }
    catch (Exception) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "账号服务暂时不可用，请稍后重试。" }); }
});
app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", async (HubDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
var auth = app.MapGroup("/v1/auth").RequireRateLimiting("auth");
auth.MapPost("/register", (RegisterRequest r, AccountService service) => service.Register(r));
auth.MapPost("/login", (LoginRequest r, AccountService service) => service.Login(r));
auth.MapPost("/refresh", (RefreshRequest r, AccountService service) => service.Refresh(r.RefreshToken));
auth.MapPost("/recover", async (RecoverRequest r, AccountService service) => new { recoveryCode = await service.Recover(r) });
auth.MapPost("/logout", async (ClaimsPrincipal p, HubDb db) => { var id = Guid.Parse(p.FindFirstValue("session")!); await db.Sessions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow)); return Results.NoContent(); }).RequireAuthorization();
var account = app.MapGroup("/v1/account").RequireAuthorization().RequireRateLimiting("auth");
account.MapGet("/me", async (ClaimsPrincipal p, HubDb db) => { var user = await db.Users.FindAsync(Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!)); return new Profile(user!.Id, user.UserName!, user.GameName); });
account.MapPost("/password", async (ClaimsPrincipal p, PasswordRequest r, AccountService service) => { await service.ChangePassword(Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!), r); return Results.NoContent(); });
account.MapPost("/recovery-code", async (ClaimsPrincipal p, LoginRequest r, AccountService service) => new { recoveryCode = await service.RotateRecovery(Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!), r.Password) });
app.MapGet("/v1/catalog", () => {
    var path = Path.Combine(builder.Configuration["PublicPath"] ?? "public", "catalog.signed.json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.Json(new { error = "正式内容正在验收，目录尚未发布。" }, statusCode: 503);
});
app.MapGet("/v1/announcements", () => new[] { new { id = "welcome", title = "欢迎来到群服大厅", body = "选择你的世界。客户端与 Java 会在首次进入时自动下载。" } });
app.Run();
public partial class Program { }
