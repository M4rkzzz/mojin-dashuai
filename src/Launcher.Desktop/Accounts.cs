using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boshan.Launcher;
using CmlLib.Core.Auth;
using Microsoft.Identity.Client;

namespace Boshan.Desktop;

public sealed record PlayerProfile(string Id,string LoginName,string GameName,string Kind="hub");
public sealed record AccountSession(PlayerProfile Profile,string AccessToken,DateTimeOffset AccessExpiresAt,string RefreshToken,DateTimeOffset RefreshExpiresAt,string? RecoveryCode=null,string? MicrosoftAccountId=null);
public sealed class Accounts
{
    private readonly Vault vault;
    private readonly HttpClient api;
    private readonly string microsoftId;
    private readonly SemaphoreSlim gate=new(1);
    public AccountSession? Current {get;private set;}
    public Accounts(Vault vault,string apiUrl,string microsoftId)
    {
        this.vault=vault;this.microsoftId=microsoftId;
        var uri=new Uri(apiUrl);if(uri.Scheme!="https")throw new InvalidDataException("账号服务必须使用 HTTPS。");
        api=new HttpClient(new HttpClientHandler {UseCookies=false,AllowAutoRedirect=false}){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(20)};
        Current=vault.Read<AccountSession>("account");
    }
    public async Task<object> Login(string action,JsonElement args)
    {
        var result=await Post<AccountSession>("/v1/auth/"+action,args);
        Store(result with {RecoveryCode=null});return new {result.Profile,result.RecoveryCode};
    }
    public async Task<object> Recover(JsonElement args)=>await Post<JsonElement>("/v1/auth/recover",args);
    public async Task<PlayerProfile?> Restore()
    {
        if(Current is null)return null;
        try{await Ensure();return Current?.Profile;}catch{Current=null;return null;}
    }
    public async Task<MSession> GameSession()
    {
        await Ensure();var current=Current??throw new InvalidDataException("请先登录账号。");
        if(current.Profile.Kind=="hub")
        {
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,"/v1/account/me");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",current.AccessToken);
                using var response=await api.SendAsync(request);
                if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){Current=null;vault.Delete("account");throw new InvalidDataException("登录已失效，请重新登录。");}
                if((int)response.StatusCode<500)await Check(response);
                else if(current.AccessExpiresAt<=DateTimeOffset.UtcNow)throw new InvalidDataException("登录已过期。");
            }
            catch(HttpRequestException) when(current.AccessExpiresAt>DateTimeOffset.UtcNow) { }
            catch(TaskCanceledException) when(current.AccessExpiresAt>DateTimeOffset.UtcNow) { }
        }
        return current.Profile.Kind=="microsoft"?new MSession(current.Profile.GameName,current.AccessToken,current.Profile.Id){UserType="msa"}:MSession.CreateOfflineSession(current.Profile.GameName);
    }
    private async Task Ensure()
    {
        await gate.WaitAsync();
        try
        {
            var current=Current??throw new InvalidDataException("请先登录账号。");
            if(current.AccessExpiresAt>DateTimeOffset.UtcNow.AddMinutes(1))return;
            try
            {
                if(current.Profile.Kind=="microsoft")await MicrosoftLogin(false);
                else Store(await Post<AccountSession>("/v1/auth/refresh",new {current.RefreshToken}));
            }
            catch(HttpRequestException) when(current.AccessExpiresAt>DateTimeOffset.UtcNow) { /* Only an already valid session may survive an outage. */ }
            catch(TaskCanceledException) when(current.AccessExpiresAt>DateTimeOffset.UtcNow) { }
        }
        finally{gate.Release();}
    }
    public async Task Logout()
    {
        try{if(Current?.Profile.Kind=="hub")await Authorized("/v1/auth/logout",new{});}
        catch(HttpRequestException){}catch(TaskCanceledException){}
        finally {Current=null;vault.Delete("account");vault.Delete("msal");}
    }
    public async Task<JsonElement?> Authorized(string path,object args)
    {
        var current=Current??throw new InvalidDataException("请先登录账号。");
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(args)};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",current.AccessToken);
        using var response=await api.SendAsync(request);await Check(response);
        return response.StatusCode==HttpStatusCode.NoContent?null:await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<T> Post<T>(string path,object args)
    {
        using var response=await api.PostAsJsonAsync(path,args);await Check(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }
    private static async Task Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new InvalidDataException("操作过于频繁，请稍后重试。");
        try{var json=await response.Content.ReadFromJsonAsync<JsonElement>();if(json.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String)throw new InvalidDataException(error.GetString());}
        catch(JsonException){}
        throw new HttpRequestException("账号服务暂时不可用，请稍后重试。",null,response.StatusCode);
    }
    private void Store(AccountSession value){Current=value;vault.Write("account",value with {RecoveryCode=null});}
    public async Task<object> MicrosoftLogin(bool interactive=true)
    {
        if(!Guid.TryParse(microsoftId,out _))throw new InvalidDataException("微软正版登录的项目应用尚未配置。请等待管理员完成应用注册。");
        var application=PublicClientApplicationBuilder.Create(microsoftId).WithAuthority(AzureCloudInstance.AzurePublic,AadAuthorityAudience.PersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build();
        application.UserTokenCache.SetBeforeAccess(a=>{var bytes=vault.ReadBytes("msal");if(bytes is not null)a.TokenCache.DeserializeMsalV3(bytes);});
        application.UserTokenCache.SetAfterAccess(a=>{if(a.HasStateChanged)vault.WriteBytes("msal",a.TokenCache.SerializeMsalV3());});
        AuthenticationResult oauth;
        if(interactive)oauth=await application.AcquireTokenInteractive(["XboxLive.signin"]).WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync();
        else
        {
            var known=(await application.GetAccountsAsync()).SingleOrDefault(x=>x.HomeAccountId.Identifier==Current?.MicrosoftAccountId);
            if(known is null)throw new InvalidDataException("微软账号需要重新登录。");
            oauth=await application.AcquireTokenSilent(["XboxLive.signin"],known).ExecuteAsync();
        }
        using var http=new HttpClient(new HttpClientHandler{UseCookies=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
        async Task<JsonElement> Exchange(string url,object payload)
        {
            using var response=await http.PostAsJsonAsync(url,payload);
            if(!response.IsSuccessStatusCode)throw new InvalidDataException("微软游戏账号验证未通过。请确认已拥有 Minecraft Java 版并完成 Xbox 资料设置。");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var xbox=await Exchange("https://user.auth.xboxlive.com/user/authenticate",new {Properties=new {AuthMethod="RPS",SiteName="user.auth.xboxlive.com",RpsTicket="d="+oauth.AccessToken},RelyingParty="http://auth.xboxlive.com",TokenType="JWT"});
        var xsts=await Exchange("https://xsts.auth.xboxlive.com/xsts/authorize",new {Properties=new {SandboxId="RETAIL",UserTokens=new[]{xbox.GetProperty("Token").GetString()}},RelyingParty="rp://api.minecraftservices.com/",TokenType="JWT"});
        var uhs=xsts.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString();
        var minecraft=await Exchange("https://api.minecraftservices.com/authentication/login_with_xbox",new {identityToken="XBL3.0 x="+uhs+";"+xsts.GetProperty("Token").GetString()});
        var access=minecraft.GetProperty("access_token").GetString()!;
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",access);
        using var entitlements=await http.GetAsync("https://api.minecraftservices.com/entitlements/mcstore");
        if(!entitlements.IsSuccessStatusCode)throw new InvalidDataException("无法核对 Minecraft Java 版所有权。");
        var ent=await entitlements.Content.ReadFromJsonAsync<JsonElement>();
        if(!ent.TryGetProperty("items",out var items)||items.GetArrayLength()==0)throw new InvalidDataException("此微软账号未拥有 Minecraft Java 版。");
        var profile=await http.GetFromJsonAsync<JsonElement>("https://api.minecraftservices.com/minecraft/profile");
        var player=new PlayerProfile(profile.GetProperty("id").GetString()!,oauth.Account.Username,profile.GetProperty("name").GetString()!,"microsoft");
        var until=DateTimeOffset.UtcNow.AddSeconds(minecraft.GetProperty("expires_in").GetInt32());
        Store(new(player,access,until,"",until,MicrosoftAccountId:oauth.Account.HomeAccountId.Identifier));
        return new {Profile=player};
    }
}
