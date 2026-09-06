using System.Net;
using System.Text.Json;
using Boshan.Hub;
using Xunit;

namespace Boshan.Tests;

public sealed class JoinAuthTests
{
    [Fact]
    public void TicketIs32ByteOpaqueBase64UrlAndOfflineUuidPreservesCase()
    {
        var a = JoinSecurity.NewTicket(); var b = JoinSecurity.NewTicket();
        Assert.True(JoinSecurity.ValidTicket(a)); Assert.NotEqual(a, b);
        Assert.Equal(32, Convert.FromBase64String(a.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", JoinSecurity.OfflineUuid("Notch"));
        Assert.NotEqual(JoinSecurity.OfflineUuid("Notch"), JoinSecurity.OfflineUuid("notch"));
        Assert.False(JoinSecurity.ValidTicket(new string('a', 42)));
        Assert.False(JoinSecurity.ValidTicket(new string('a', 42) + "/"));
    }
    [Fact]
    public void RedeemRequiresExactScopeNameIdentityAndUnusedUnexpiredTicket()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new JoinIdentity { GameName = "Notch", GameUuid = JoinSecurity.OfflineUuid("Notch") };
        var ticket = new JoinTicket { ExactName = identity.GameName, GameUuid = identity.GameUuid, InstanceId = "dc2", ExpiresAt = now.AddSeconds(120) };
        Assert.True(JoinSecurity.Matches(ticket, identity, "dc2", "Notch", now));
        Assert.False(JoinSecurity.Matches(ticket, identity, "mb", "Notch", now));
        Assert.False(JoinSecurity.Matches(ticket, identity, "dc2", "notch", now));
        Assert.False(JoinSecurity.Matches(ticket, identity, "dc2", "Notch", now.AddSeconds(120)));
        ticket.ConsumedAt = now;
        Assert.False(JoinSecurity.Matches(ticket, identity, "dc2", "Notch", now));
        ticket.ConsumedAt = null; identity.Disabled = true;
        Assert.False(JoinSecurity.Matches(ticket, identity, "dc2", "Notch", now));
        identity.Disabled = false; identity.GameUuid = "changed";
        Assert.False(JoinSecurity.Matches(ticket, identity, "dc2", "Notch", now));
    }
    [Fact]
    public async Task MinecraftProofUsesOnlyFixedOfficialEndpointsAndVerifiedName()
    {
        var visited = new List<string>();
        using var http = new HttpClient(new FakeHttp(request => {
            visited.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = visited.Count == 1 ? "{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"Notch\"}" : "{\"items\":[{\"name\":\"game_minecraft\"}]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var verified = await new MinecraftJoinVerifier(http).Verify("test-credential-not-real", default);
        Assert.Equal("Notch", verified.GameName);
        Assert.Equal("069a79f444e94726a5befca90e38aaf5", verified.ProfileId);
        Assert.Equal(["https://api.minecraftservices.com/minecraft/profile", "https://api.minecraftservices.com/entitlements/mcstore"], visited);
    }
    [Fact]
    public async Task RedirectIsFailureAndDoesNotBecomeTrustedProof()
    {
        var calls = 0;
        using var http = new HttpClient(new FakeHttp(_ => { calls++; return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://untrusted.example/steal") } }; }));
        var error = await Assert.ThrowsAsync<HubError>(() => new MinecraftJoinVerifier(http).Verify("test-credential-not-real", default));
        Assert.Equal(503, error.Status); Assert.Equal(1, calls);
        Assert.DoesNotContain("test-credential", error.Message);
    }
    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"name\":\"other_product\"}]}")]
    [InlineData("{}")]
    public void ProfileAloneDoesNotGrantOwnership(string body)
    {
        using var profile = JsonDocument.Parse("{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"Notch\"}");
        using var ownership = JsonDocument.Parse(body);
        Assert.Throws<HubError>(() => MinecraftJoinVerifier.ParseVerified(profile.RootElement, ownership.RootElement));
    }
    [Fact]
    public void InternalRedeemTrustsOnlyExplicitTcpPeerNetwork()
    {
        Assert.False(JoinInternalAccess.IsAllowed(IPAddress.Parse("172.24.0.5"), null));
        Assert.True(JoinInternalAccess.IsAllowed(IPAddress.Parse("172.24.0.5"), "172.24.0.5/32"));
        Assert.True(JoinInternalAccess.IsAllowed(IPAddress.Parse("::ffff:172.24.0.5"), "172.24.0.5/32"));
        Assert.False(JoinInternalAccess.IsAllowed(IPAddress.Parse("172.24.0.6"), "172.24.0.5/32"));
        Assert.False(JoinInternalAccess.IsAllowed(IPAddress.Loopback, "invalid"));
    }
    [Fact]
    public void QuotasSeparateAccountsRatherThanSharingOneFrpIp()
    {
        var limits = new JoinRequestLimits();
        limits.Take("issue:one", 1);
        Assert.Equal(429, Assert.Throws<HubError>(() => limits.Take("issue:one", 1)).Status);
        limits.Take("issue:two", 1);
    }
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
