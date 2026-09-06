using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boshan.Launcher;

namespace Boshan.Desktop;

public sealed partial class Accounts
{
    public async Task<JsonElement> Activities(JsonElement command, CancellationToken ct = default)
    {
        await joinGate.WaitAsync(ct);
        try
        {
            await Ensure();
            var current = Current ?? throw new InvalidDataException("请先登录。");
            var identity = current.Profile;
            void CheckIdentity()
            {
                if (Current?.Profile != identity) throw new InvalidDataException("账号已切换，请重新打开活动页面。");
            }
            var bearer = current.AccessToken;
            if (identity.Kind == "microsoft")
            {
                var key = identity.Kind + ":" + identity.Id + ":" + identity.GameName;
                if (joinGrant is null || joinGrantIdentity != key || joinGrant.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                {
                    using var exchange = await api.PostAsJsonAsync("/v1/auth/minecraft/exchange", new { accessToken = current.AccessToken }, ct);
                    await Check(exchange);
                    var next = await exchange.Content.ReadFromJsonAsync<JoinGrant>(Json.Options, ct) ?? throw new InvalidDataException("活动身份响应无效。");
                    CheckIdentity();
                    if (next.GameName != identity.GameName || next.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("活动角色与当前账号不一致。");
                    joinGrant = next; joinGrantIdentity = key;
                }
                bearer = joinGrant.AccessToken;
            }
            CheckIdentity();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/activities") { Content = JsonContent.Create(command) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await api.SendAsync(request, ct); await Check(response);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json.Options, ct);
            CheckIdentity(); return result;
        }
        finally { joinGate.Release(); }
    }
}
