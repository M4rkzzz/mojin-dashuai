using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class JoinApiLive
{
    private sealed record Grant(string AccessToken, DateTimeOffset ExpiresAt, string GameName, string GameUuid);

    public static async Task Run(string? instance)
    {
        var report = new Dictionary<string, object?> {
            ["scope"] = instance is null ? "official-identity-read-only" : "production-microsoft-exchange-and-ticket",
            ["observedAt"] = DateTimeOffset.UtcNow,
            ["passed"] = false,
            ["joinApiVerified"] = false,
            ["interactiveWindowOpened"] = false,
            ["gameWindowsOpened"] = 0,
        };
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boshan", "Launcher");
        var tempParent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempParent, "mojin-join-api-live-" + Guid.NewGuid().ToString("N"));
        var stage = "restore-encrypted-session";
        try
        {
            if (instance is not null and not ("m3e" or "dc2" or "mb" or "vw")) throw new InvalidDataException("Invalid instance");
            NetworkPolicy.Configure(new LauncherSettings { ProxyMode = "direct" });
            var original = new Vault(source).Read<AccountSession>("account") ?? throw new InvalidDataException("No encrypted account");
            if (original.Profile.Kind != "microsoft" || original.Profile.GameName != "M4rkzzz") throw new InvalidDataException("Expected existing M4rkzzz Microsoft identity");
            var clientId = Guid.TryParse(original.MicrosoftClientId, out var appId) ? appId.ToString() : "";
            Directory.CreateDirectory(root);
            var cacheKeys = new List<string> { "account", "msal", MicrosoftAccountStorage.Key };
            if (clientId.Length > 0) cacheKeys.Add("msal-" + appId.ToString("N"));
            foreach (var key in cacheKeys)
                if (File.Exists(Path.Combine(source, key + ".dpapi"))) File.Copy(Path.Combine(source, key + ".dpapi"), Path.Combine(root, key + ".dpapi"));
            var accounts = new Accounts(new Vault(root), NetworkPolicy.DirectApi, clientId);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            stage = "silent-microsoft-authentication";
            await accounts.MicrosoftLogin(false, token: budget.Token);
            var current = accounts.Current ?? throw new InvalidDataException("Session missing");
            if (current.Profile.Id != original.Profile.Id || current.Profile.GameName != original.Profile.GameName) throw new InvalidDataException("Silent identity changed");
            report["silentAuthenticationPassed"] = true;
            using var http = new HttpClient(NetworkPolicy.Handler(allowRedirect: false)) { Timeout = TimeSpan.FromSeconds(20) };
            stage = "official-profile";
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
            using var profileResponse = await http.SendAsync(profileRequest, budget.Token);
            report["officialProfileHttpStatus"] = (int)profileResponse.StatusCode;
            if (!profileResponse.IsSuccessStatusCode) throw new InvalidDataException("Official profile rejected");
            var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
            var officialId = profile.GetProperty("id").GetString();
            var officialName = profile.GetProperty("name").GetString();
            if (!Guid.TryParseExact(officialId, "N", out var verifiedId) || officialName != original.Profile.GameName || verifiedId != Guid.Parse(original.Profile.Id)) throw new InvalidDataException("Official identity differs");
            report["officialProfileVerified"] = true;
            report["gameName"] = officialName;
            report["officialUuid"] = verifiedId.ToString("D");
            var offlineUuid = OfflineUuid(officialName!);
            report["offlineUuid"] = offlineUuid;
            stage = "official-entitlements";
            using var ownershipRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
            ownershipRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
            using var ownershipResponse = await http.SendAsync(ownershipRequest, budget.Token);
            report["officialEntitlementsHttpStatus"] = (int)ownershipResponse.StatusCode;
            if (!ownershipResponse.IsSuccessStatusCode) throw new InvalidDataException("Ownership rejected");
            var ownership = await ownershipResponse.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
            if (!ownership.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("name").GetString() is "game_minecraft" or "product_minecraft")) throw new InvalidDataException("Java entitlement absent");
            report["ownershipVerified"] = true;
            if (instance is not null)
            {
                stage = "production-minecraft-exchange";
                using var exchange = await http.PostAsJsonAsync(new Uri(new Uri(NetworkPolicy.DirectApi), "/v1/auth/minecraft/exchange"), new { accessToken = current.AccessToken }, budget.Token);
                report["exchangeHttpStatus"] = (int)exchange.StatusCode;
                if (exchange.StatusCode == System.Net.HttpStatusCode.Conflict) report["requiresIdentityBinding"] = true;
                if (!exchange.IsSuccessStatusCode) throw new InvalidDataException("Join exchange rejected");
                var grant = await exchange.Content.ReadFromJsonAsync<Grant>(budget.Token) ?? throw new InvalidDataException("Join grant missing");
                if (grant.GameName != officialName || Guid.Parse(grant.GameUuid).ToString("D") != offlineUuid || grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(11)) throw new InvalidDataException("Join grant identity/lifetime differs");
                stage = "production-ticket-issue";
                using var ticketRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(NetworkPolicy.DirectApi), "/v1/join/tickets")) { Content = JsonContent.Create(new { instance }) };
                ticketRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
                using var response = await http.SendAsync(ticketRequest, budget.Token);
                report["ticketHttpStatus"] = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) throw new InvalidDataException("Ticket rejected");
                var ticket = await response.Content.ReadFromJsonAsync<JoinTicket>(budget.Token) ?? throw new InvalidDataException("Ticket missing");
                if (ticket.GameName != officialName || Guid.Parse(ticket.GameUuid).ToString("D") != offlineUuid || !System.Text.RegularExpressions.Regex.IsMatch(ticket.Ticket, "^[A-Za-z0-9_-]{43}$") || ticket.ExpiresAt <= DateTimeOffset.UtcNow || ticket.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(125)) throw new InvalidDataException("Ticket identity/lifetime differs");
                report["joinApiVerified"] = true;
                report["ticketInstance"] = instance;
                report["ticketExpiresAt"] = ticket.ExpiresAt;
                report["redeemVerified"] = false; // No service key is accepted by this desktop helper.
            }
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["failedStage"] = stage;
            report["errorCategory"] = error.GetType().Name;
            Environment.ExitCode = 1;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar).Equals(tempParent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true && Path.GetFileName(full).StartsWith("mojin-join-api-live-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
    }
    private static string OfflineUuid(string name)
    {
        // CmlLib's offline session can use a random client UUID; the server instead uses Java's name UUID.
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 15) | 48);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        return Guid.ParseExact(Convert.ToHexString(bytes), "N").ToString("D");
    }
}
