using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public sealed record MinecraftIdentity(string Id, string Name, string AccessToken, DateTimeOffset ExpiresAt, string? SkinUrl, string SkinModel);

public sealed class MinecraftAuthentication(HttpClient http)
{
    public async Task<MinecraftIdentity> Login(string microsoftToken, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var xbox = await Post("https://user.auth.xboxlive.com/user/authenticate", new {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
                RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
            }, "Xbox", token);
            var xsts = await Post("https://xsts.auth.xboxlive.com/xsts/authorize", new {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { RequiredText(xbox, "Token") } },
                RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
            }, "XSTS", token);
            var uhs = RequiredText(xsts.GetProperty("DisplayClaims").GetProperty("xui")[0], "uhs");
            var minecraft = await Post("https://api.minecraftservices.com/authentication/login_with_xbox", new {
                identityToken = "XBL3.0 x=" + uhs + ";" + RequiredText(xsts, "Token")
            }, "Minecraft", token);
            var access = RequiredText(minecraft, "access_token");
            var lifetime = minecraft.GetProperty("expires_in").GetInt32();
            if (lifetime <= 0 || lifetime > 172800) throw new InvalidDataException("Minecraft 返回了无效的会话有效期。");
            var expires = DateTimeOffset.UtcNow.AddSeconds(lifetime);

            var entitlements = await Get("https://api.minecraftservices.com/entitlements/mcstore", access, "所有权", token);
            if (!entitlements.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                throw new InvalidDataException("此微软账号未拥有 Minecraft Java 版。");
            var profile = await Get("https://api.minecraftservices.com/minecraft/profile", access, "游戏资料", token);
            var id = RequiredText(profile, "id"); var name = RequiredText(profile, "name");
            if (!Regex.IsMatch(id, "^[a-fA-F0-9]{32}$") || !Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
                throw new InvalidDataException("Minecraft 返回了无效的游戏资料。");
            var skin = ActiveSkin(profile);
            token.ThrowIfCancellationRequested();
            return new(id, name, access, expires, skin.Url, skin.Model);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("微软游戏账号响应不完整，请稍后重试。");
        }
    }

    private async Task<JsonElement> Post(string address, object payload, string stage, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = JsonContent.Create(payload, options: JsonSerializerOptions.Default) };
        using var response = await http.SendAsync(request, token);
        await Check(response, stage, token);
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private async Task<JsonElement> Get(string address, string access, string stage, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await http.SendAsync(request, token);
        await Check(response, stage, token);
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private static async Task Check(HttpResponseMessage response, string stage, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        if (stage == "XSTS" && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                if (error.TryGetProperty("XErr", out var value) && value.TryGetInt64(out var code))
                {
                    var message = code switch {
                        2148916227 => "此 Xbox 账号已被封禁，请在 Xbox 官网查看账号状态。",
                        2148916233 => "请先在 Xbox 官网完成游戏档案设置，再重新登录。",
                        2148916235 => "此账号所在地区暂不支持 Xbox Live。",
                        2148916238 => "此账号需要家庭组织者在 Xbox 中完成未成年账号设置。",
                        _ => "Xbox 账号授权未通过，请在 Xbox 官网检查账号状态。"
                    };
                    throw new InvalidDataException(message);
                }
            }
            catch (JsonException) { }
        }
        // Never forward response bodies, URLs or tokens as an error message.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidDataException("微软登录请求过于频繁，请稍后重试。");
        if (stage == "Minecraft" && response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidDataException("Minecraft 拒绝了此登录应用，请联系管理员检查应用配置。");
        if (stage == "游戏资料" && response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("请先在 Minecraft 官网设置 Java 版游戏名。");
        throw new InvalidDataException($"{stage} 验证未完成（HTTP {(int)response.StatusCode}），请稍后重试。");
    }

    private static string RequiredText(JsonElement value, string key)
    {
        var text = value.GetProperty(key).GetString();
        return !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException("微软游戏账号响应不完整，请稍后重试。");
    }

    public static (string? Url, string Model) ActiveSkin(JsonElement profile)
    {
        if (profile.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
            foreach (var skin in skins.EnumerateArray())
                if (skin.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE")
                    return (skin.GetProperty("url").GetString(), skin.TryGetProperty("variant", out var variant) && variant.GetString() == "SLIM" ? "slim" : "classic");
        return (null, "classic");
    }
}
