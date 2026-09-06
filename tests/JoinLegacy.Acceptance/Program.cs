using System.Net;
using System.Text.Json;
using Boshan.Hub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

// Test-only executable: never ships in the API image, never accepts a production DB.
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Hub") ?? throw new Exception("Missing test database");
var parsed = new NpgsqlConnectionStringBuilder(connection);
if (parsed.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(parsed.Database, "^hub_legacy_acceptance_[a-f0-9]{8}$"))
    throw new Exception("An isolated acceptance database is required");
var options = new DbContextOptionsBuilder<HubDb>().UseNpgsql(connection).Options;
await using var db = new HubDb(options);
await db.Database.EnsureCreatedAsync();
await JoinData.Initialize(db);
if (await db.Users.AnyAsync() || await db.Set<JoinIdentity>().AnyAsync() || await db.ProtectedNames.AnyAsync())
    throw new Exception("Fixture database must be empty");
var keys = new[] { "m3e", "dc2", "mb", "vw" }.ToDictionary(x => "JoinAuth:ServerKeys:" + x, _ => (string?)Secret.New());
var config = new ConfigurationBuilder().AddInMemoryCollection(keys).Build();
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
JoinService Service(string name, string id, bool owned = true) => new(db,
    new MinecraftJoinVerifier(new HttpClient(new OfficialFixture(name, id, owned))), config, new JoinRequestLimits());
async Task Deny(JoinService service, int status, string label)
{
    try { await service.Exchange("synthetic-test-proof", default); throw new Exception("Unexpected allowed: " + label); }
    catch (HubError error) { Check(error.Status == status, label); }
    db.ChangeTracker.Clear();
}
async Task Accept(string name, string id, string expectedUuid, string label)
{
    var service = Service(name, id);
    var grant = await service.Exchange("synthetic-test-proof", default);
    Check(grant.GameName == name && grant.GameUuid == expectedUuid, label + ": exact name and UUID");
    var identity = await db.Set<JoinIdentity>().AsNoTracking().SingleAsync(i => i.MinecraftProfileId == id);
    Check(identity.GameName == name && identity.GameUuid == expectedUuid, label + ": persisted binding");
    var repeated = await service.Exchange("synthetic-test-proof", default);
    Check(repeated.GameUuid == expectedUuid && await db.Set<JoinIdentity>().CountAsync(i => i.GameNameKey == Secret.NameKey(name)) == 1, label + ": repeated login idempotent");
    foreach (var instance in new[] { "m3e", "dc2", "mb", "vw" })
    {
        var ticket = await service.Issue(grant.AccessToken, instance, default);
        var redeemed = await service.Redeem(keys["JoinAuth:ServerKeys:" + instance]!, new(ticket.Ticket, instance, name), default);
        Check(redeemed.Allowed && redeemed.GameUuid == expectedUuid, label + ": " + instance + " ticket redeemed");
    }
    db.ChangeTracker.Clear();
}

// Cover every name in the production metadata audit using synthetic proof and an
// isolated DB. No public profile lookup is treated as player authentication.
using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var roleCount = 0;
foreach (var row in fixture.RootElement.GetProperty("roles").EnumerateArray())
{
    roleCount++;
    var names = row.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();
    if (names.Length == 0) continue;
    var name = row.GetProperty("boundExactName").GetString() ?? names[0];
    var key = row.GetProperty("key").GetString()!;
    var status = row.GetProperty("status").GetString();
    var id = Guid.NewGuid().ToString("N");
    if (row.GetProperty("protected").GetBoolean())
        db.ProtectedNames.Add(new() { Key = key, ExactName = row.GetProperty("protectedExactName").GetString()! });
    if (status is "linked_hub" or "linked_both")
    {
        var user = new HubUser { Id = Guid.NewGuid(), GameName = name, GameNameKey = key, UserName = name, NormalizedUserName = key };
        db.Users.Add(user);
        var identity = JoinSecurity.ForHubUser(user);
        if (status == "linked_both") identity.MinecraftProfileId = id;
        db.Set<JoinIdentity>().Add(identity);
        await db.SaveChangesAsync();
        var bearer = Secret.New();
        db.Sessions.Add(new() { Id = Guid.NewGuid(), UserId = user.Id, AccessHash = Secret.Hash(bearer), RefreshHash = Secret.New(), AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var ticket = await Service(name, id).Issue(bearer, "dc2", default);
        Check(ticket.GameUuid == JoinSecurity.OfflineUuid(name), name + ": Hub account retains role");
        if (status == "linked_hub")
        {
            foreach (var variant in names) await Deny(Service(variant, id), 409, variant + ": no implicit Hub ownership takeover");
            continue;
        }
    }
    else if (status == "linked_microsoft")
        db.Set<JoinIdentity>().Add(new() { GameName = name, GameNameKey = key, GameUuid = row.GetProperty("gameUuid").GetString()!, MinecraftProfileId = id });
    await db.SaveChangesAsync();
    if (status == "needs_review")
    {
        foreach (var variant in names) await Deny(Service(variant, id), 409, variant + ": ambiguous history remains protected");
        continue;
    }
    await Deny(Service(name, id, false), 401, name + ": ownership proof required");
    await Accept(name, id, JoinSecurity.OfflineUuid(name), name);
}

db.ProtectedNames.AddRange(new ProtectedName { Key = "CASELEGACY", ExactName = "CaseLegacy" }, new ProtectedName { Key = "AMBIGLEGACY", ExactName = "" });
await db.SaveChangesAsync();
await Deny(Service("caselegacy", Guid.NewGuid().ToString("N")), 409, "legacy case change rejected");
await Deny(Service("AmbigLegacy", Guid.NewGuid().ToString("N")), 409, "ambiguous protected name rejected");
await Accept("CaseLegacy", Guid.NewGuid().ToString("N"), JoinSecurity.OfflineUuid("CaseLegacy"), "exact legacy case succeeds");
var owner = Guid.NewGuid().ToString("N");
db.Set<JoinIdentity>().Add(new() { GameName = "OwnerFixture", GameNameKey = "OWNERFIXTURE", GameUuid = JoinSecurity.OfflineUuid("OwnerFixture"), MinecraftProfileId = owner });
await db.SaveChangesAsync();
await Deny(Service("OwnerFixture", Guid.NewGuid().ToString("N")), 409, "other official owner rejected");
await Deny(Service("RenamedFixture", owner), 409, "official rename does not migrate role");
await db.Set<JoinIdentity>().Where(i => i.MinecraftProfileId == owner).ExecuteUpdateAsync(s => s.SetProperty(i => i.Disabled, true));
await Deny(Service("OwnerFixture", owner), 409, "disabled identity rejected");
db.Users.Add(new() { Id = Guid.NewGuid(), GameName = "MissingHub", GameNameKey = "MISSINGHUB", UserName = "MissingHub" });
await db.SaveChangesAsync();
await Deny(Service("MissingHub", Guid.NewGuid().ToString("N")), 409, "Hub account without identity not overwritten");
Check(!await db.Set<JoinIdentity>().AnyAsync(i => i.GameNameKey == "AMBIGLEGACY"), "rejections persist no new binding");
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, scope = "isolated PostgreSQL and synthetic official proof", auditedRolesCovered = roleCount, checkCount = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));

sealed class OfficialFixture(string name, string id, bool owned) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "api.minecraftservices.com") throw new Exception("Unexpected verification host");
        var json = request.RequestUri.AbsolutePath switch {
            "/minecraft/profile" => JsonSerializer.Serialize(new { id, name }),
            "/entitlements/mcstore" => owned ? "{\"items\":[{\"name\":\"game_minecraft\"}]}" : "{\"items\":[]}",
            _ => throw new Exception("Unexpected verification path")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
