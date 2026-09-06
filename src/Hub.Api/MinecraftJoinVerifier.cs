using System.Net.Http.Headers;
using System.Text.Json;

namespace Boshan.Hub;

public sealed record VerifiedMinecraftIdentity(string ProfileId, string GameName);

public sealed class MinecraftJoinVerifier(HttpClient http)
{
    public async Task<VerifiedMinecraftIdentity> Verify(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 12000 || token.Any(char.IsWhiteSpace))
            throw new HubError("微软登录凭据无效，请重新登录。", 401);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var profile = await Fetch("https://api.minecraftservices.com/minecraft/profile", token, budget.Token);
            using var ownership = await Fetch("https://api.minecraftservices.com/entitlements/mcstore", token, budget.Token);
            return ParseVerified(profile.RootElement, ownership.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HubError("微软账号验证暂时超时，请稍后重试。", 503);
        }
        catch (HttpRequestException) { throw new HubError("微软账号验证暂时不可用，请稍后重试。", 503); }
        catch (JsonException) { throw new HubError("微软账号验证返回异常，请稍后重试。", 503); }
    }
    private async Task<JsonDocument> Fetch(string address, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Redirects are disabled on the registered handler; credentials only go to these fixed endpoints.
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            throw new HubError("微软登录已失效，或此账号没有 Minecraft Java 版。", 401);
        if (!response.IsSuccessStatusCode) throw new HubError("微软账号验证暂时不可用，请稍后重试。", 503);
        await response.Content.LoadIntoBufferAsync(128 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }
    public static VerifiedMinecraftIdentity ParseVerified(JsonElement profile, JsonElement ownership)
    {
        if (!profile.TryGetProperty("id", out var idValue) || !profile.TryGetProperty("name", out var nameValue) ||
            idValue.ValueKind != JsonValueKind.String || nameValue.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(idValue.GetString(), "N", out var id) || id == Guid.Empty ||
            nameValue.GetString() is not { } name || !Secret.GameNamePattern().IsMatch(name) ||
            !ownership.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            !items.EnumerateArray().Any(i => i.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is "game_minecraft" or "product_minecraft"))
            throw new HubError("微软账号资料或 Minecraft Java 版所有权验证失败。", 401);
        return new(id.ToString("N"), name);
    }
}
