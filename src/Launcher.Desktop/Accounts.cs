using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boshan.Launcher;
using CmlLib.Core.Auth;
using Microsoft.Identity.Client;
using Boshan.Shared;

namespace Boshan.Desktop;

public sealed record PlayerProfile(string Id,string LoginName,string GameName,string Kind="hub");
public sealed record AccountSession(PlayerProfile Profile,string AccessToken,DateTimeOffset AccessExpiresAt,string RefreshToken,DateTimeOffset RefreshExpiresAt,string? RecoveryCode=null,string? MicrosoftAccountId=null,string? SkinUrl=null,string SkinModel="classic",string? MicrosoftClientId=null);
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
        api.DefaultRequestHeaders.UserAgent.ParseAdd("MojinDashuai/"+typeof(Accounts).Assembly.GetName().Version);
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
        finally {Current=null;vault.Delete("account");vault.Delete("msal");if(Guid.TryParse(microsoftId,out var appId))vault.Delete("msal-"+appId.ToString("N"));}
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
    private void Store(AccountSession value){vault.Write("account",value with {RecoveryCode=null});Current=value;}
    public async Task<SkinTexture?> Skin()
    {
        var current=Current;
        if(current is null)return null;
        using var http=new HttpClient(new HttpClientHandler{UseCookies=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
        Uri address;
        var model=current.SkinModel;
        if(current.Profile.Kind=="microsoft")
        {
            await Ensure();current=Current!;
            using var profileRequest=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/minecraft/profile");
            profileRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",current.AccessToken);
            using var profileResponse=await http.SendAsync(profileRequest);
            if(profileResponse.IsSuccessStatusCode)
            {
                var profile=await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
                var active=MinecraftAuthentication.ActiveSkin(profile);
                Store(current with {SkinUrl=active.Url,SkinModel=active.Model});current=Current!;model=current.SkinModel;
            }
            if(string.IsNullOrEmpty(current.SkinUrl))return null;
            if(!Uri.TryCreate(current.SkinUrl,UriKind.Absolute,out var uri)||uri.Host!="textures.minecraft.net"||!uri.IsDefaultPort||!string.IsNullOrEmpty(uri.UserInfo)||uri.Scheme is not ("http" or "https")||!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,"^/texture/[a-fA-F0-9]{32,64}$"))throw new InvalidDataException("皮肤资源地址无效。");
            address=new UriBuilder(uri){Scheme="https",Port=-1,Query="",Fragment=""}.Uri;
        }
        else address=new Uri(api.BaseAddress!,"/v1/skins/"+Uri.EscapeDataString(current.Profile.GameName));
        // Texture requests use a separate client without account credentials or cookies.
        using var response=await http.GetAsync(address,HttpCompletionOption.ResponseHeadersRead);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        await Check(response);
        if(response.Content.Headers.ContentLength>SkinImage.MaxBytes)throw new InvalidDataException("皮肤文件过大。");
        if(current.Profile.Kind=="hub")model=response.Headers.TryGetValues("X-Skin-Model",out var values)&&values.FirstOrDefault()=="slim"?"slim":"classic";
        await using var stream=await response.Content.ReadAsStreamAsync();
        using var bytes=new MemoryStream();
        var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer))>0){if(bytes.Length+count>SkinImage.MaxBytes)throw new InvalidDataException("皮肤文件过大。");bytes.Write(buffer,0,count);}
        return new(Convert.ToBase64String(SkinImage.Normalize(bytes.ToArray())),model);
    }
    public async Task<SkinTexture> SaveSkin(SkinTexture texture)
    {
        await Ensure();
        if(Current?.Profile.Kind!="hub")throw new InvalidDataException("请在微软账号页面更换正版皮肤。");
        texture=SkinImage.Normalize(texture);
        var result=await Authorized("/v1/account/skin",texture);
        return result!.Value.Deserialize<SkinTexture>(Json.Options)!;
    }
    public async Task<object> MicrosoftLogin(bool interactive=true,Func<MicrosoftDevicePrompt,Task>? showCode=null,CancellationToken token=default)
    {
        if(!Guid.TryParse(microsoftId,out var appId)||appId==Guid.Empty)throw new InvalidDataException("正版登录暂未开放，请等待管理员完成登录应用配置。");
        if(!interactive&&Current?.MicrosoftClientId!=microsoftId)throw new InvalidDataException("正版登录配置已更新，请重新登录。");
        var application=PublicClientApplicationBuilder.Create(microsoftId).WithAuthority(AzureCloudInstance.AzurePublic,AadAuthorityAudience.PersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build();
        var cacheKey="msal-"+appId.ToString("N");
        application.UserTokenCache.SetBeforeAccess(a=>{var bytes=vault.ReadBytes(cacheKey);if(bytes is not null)a.TokenCache.DeserializeMsalV3(bytes);});
        application.UserTokenCache.SetAfterAccess(a=>{if(a.HasStateChanged&&!token.IsCancellationRequested)vault.WriteBytes(cacheKey,a.TokenCache.SerializeMsalV3());});
        AuthenticationResult oauth;
        try
        {
            if(interactive)
            {
                if(showCode is null)throw new InvalidOperationException("Device-code presentation is required.");
                oauth=await application.AcquireTokenWithDeviceCode(["XboxLive.signin"],result=>
                {
                    token.ThrowIfCancellationRequested();
                    return showCode(MicrosoftDevicePrompt.Create(result.UserCode,result.VerificationUrl,result.ExpiresOn));
                }).WithExtraQueryParameters(new Dictionary<string,(string value,bool includeInCacheKey)>{{"mkt",("zh-CN",false)}}).ExecuteAsync(token);
            }
            else
            {
                var known=(await application.GetAccountsAsync()).SingleOrDefault(x=>x.HomeAccountId.Identifier==Current?.MicrosoftAccountId);
                if(known is null)throw new InvalidDataException("微软账号需要重新登录。");
                oauth=await application.AcquireTokenSilent(["XboxLive.signin"],known).ExecuteAsync(token);
            }
        }
        catch(MsalUiRequiredException){throw new InvalidDataException("微软账号需要重新登录。");}
        catch(MsalException ex)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException(ex.ErrorCode switch
            {
                "authorization_declined" or "access_denied"=>"已取消微软授权，可重新登录。",
                "expired_token" or "code_expired" or "verification_code_expired"=>"登录码已过期，请重新登录。",
                "invalid_client" or "unauthorized_client" or "invalid_scope"=>"正版登录应用配置未通过验证，请联系管理员。",
                "authentication_canceled"=>"已取消微软登录。",
                _=>"微软登录暂时不可用，请稍后重试。"
            });
        }
        token.ThrowIfCancellationRequested();
        using var http=new HttpClient(new HttpClientHandler{UseCookies=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=2*1024*1024};
        var identity=await new MinecraftAuthentication(http).Login(oauth.AccessToken,token);
        var player=new PlayerProfile(identity.Id,oauth.Account.Username,identity.Name,"microsoft");
        token.ThrowIfCancellationRequested();
        Store(new(player,identity.AccessToken,identity.ExpiresAt,"",identity.ExpiresAt,MicrosoftAccountId:oauth.Account.HomeAccountId.Identifier,SkinUrl:identity.SkinUrl,SkinModel:identity.SkinModel,MicrosoftClientId:microsoftId));
        return new {Profile=player};
    }
}
