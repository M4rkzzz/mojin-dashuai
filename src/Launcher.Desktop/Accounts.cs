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
using XboxAuthNet.OAuth.CodeFlow;

namespace Boshan.Desktop;

public sealed record PlayerProfile(string Id,string LoginName,string GameName,string Kind="hub");
public sealed record AccountSession(PlayerProfile Profile,string AccessToken,DateTimeOffset AccessExpiresAt,string RefreshToken,DateTimeOffset RefreshExpiresAt,string? RecoveryCode=null,string? MicrosoftAccountId=null,string? SkinUrl=null,string SkinModel="classic",string? MicrosoftClientId=null,string? MicrosoftXuid=null);
public sealed record JoinTicket(string Ticket,DateTimeOffset ExpiresAt,string GameName,string GameUuid);
internal sealed record JoinGrant(string AccessToken,DateTimeOffset ExpiresAt,string GameName,string GameUuid);
public sealed class Accounts
{
    private readonly Vault vault;
    private HttpClient api;
    private readonly string microsoftId;
    private readonly Func<IWebUI>? microsoftWebUi;
    public string MicrosoftLoginMode=>string.IsNullOrWhiteSpace(microsoftId)?"window":"device-code";
    private readonly SemaphoreSlim gate=new(1);
    public AccountSession? Current {get;private set;}
    private JoinGrant? joinGrant;
    private string? joinGrantIdentity;
    private readonly SemaphoreSlim joinGate=new(1);
    public void ReconfigureNetwork()
    {
        var old=api;
        api=new HttpClient(NetworkPolicy.Handler(allowRedirect:false)){BaseAddress=old.BaseAddress,Timeout=TimeSpan.FromSeconds(20)};
        api.DefaultRequestHeaders.UserAgent.ParseAdd("MojinDashuai/"+typeof(Accounts).Assembly.GetName().Version);
        // Allow in-flight account operations to finish with their original transport.
        _=Retire(old);
    }
    private static async Task Retire(HttpClient old){await Task.Delay(TimeSpan.FromMinutes(1));old.Dispose();}
    public Accounts(Vault vault,string apiUrl,string microsoftId,Func<IWebUI>? microsoftWebUi=null)
    {
        this.vault=vault;this.microsoftId=microsoftId;this.microsoftWebUi=microsoftWebUi;
        var uri=new Uri(apiUrl);if(uri.Scheme!="https")throw new InvalidDataException("账号服务必须使用 HTTPS。");
        api=new HttpClient(NetworkPolicy.Handler(allowRedirect:false)){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(20)};
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
            catch(Exception ex) when(NetworkPolicy.IsNetwork(ex)&&current.AccessExpiresAt>DateTimeOffset.UtcNow) { }
        }
        return current.Profile.Kind=="microsoft"?new MSession(current.Profile.GameName,current.AccessToken,current.Profile.Id){UserType="msa",Xuid=current.MicrosoftXuid}:MSession.CreateOfflineSession(current.Profile.GameName);
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
            catch(Exception ex) when(NetworkPolicy.IsNetwork(ex)&&current.AccessExpiresAt>DateTimeOffset.UtcNow) { /* Only an already valid session may survive an outage. */ }
        }
        finally{gate.Release();}
    }
    public async Task<JoinTicket> CreateJoinTicket(string instance,PlayerProfile identity,CancellationToken cancellation=default)
    {
        if(instance is not ("m3e" or "dc2" or "mb" or "vw"))throw new InvalidDataException("服务器无效。");
        await joinGate.WaitAsync(cancellation);
        try
        {
            await Ensure();
            var current=Current??throw new InvalidDataException("请重新登录统一客户端。");
            void CheckIdentity()
            {
                if(Current?.Profile is not {} profile||profile.Id!=identity.Id||profile.Kind!=identity.Kind||profile.GameName!=identity.GameName)
                    throw new InvalidDataException("启动账号已切换，请从统一客户端重新启动游戏。");
            }
            CheckIdentity();
            var bearer=current.AccessToken;
            if(identity.Kind=="microsoft")
            {
                var key=identity.Kind+":"+identity.Id+":"+identity.GameName;
                if(joinGrant is null||joinGrantIdentity!=key||joinGrant.ExpiresAt<=DateTimeOffset.UtcNow.AddSeconds(30))
                {
                    using var exchange=await api.PostAsJsonAsync("/v1/auth/minecraft/exchange",new{accessToken=current.AccessToken},cancellation);
                    await Check(exchange);
                    var next=await exchange.Content.ReadFromJsonAsync<JoinGrant>(Json.Options,cancellation)??throw new InvalidDataException("入服授权响应无效。");
                    CheckIdentity();
                    if(next.GameName!=identity.GameName||next.ExpiresAt<=DateTimeOffset.UtcNow)throw new InvalidDataException("入服角色与启动账号不一致。");
                    joinGrant=next;joinGrantIdentity=key;
                }
                bearer=joinGrant.AccessToken;
            }
            using var request=new HttpRequestMessage(HttpMethod.Post,"/v1/join/tickets"){Content=JsonContent.Create(new{instance})};
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
            using var response=await api.SendAsync(request,cancellation);await Check(response);
            var ticket=await response.Content.ReadFromJsonAsync<JoinTicket>(Json.Options,cancellation)??throw new InvalidDataException("入服凭据响应无效。");
            CheckIdentity();
            if(ticket.GameName!=identity.GameName||ticket.ExpiresAt<=DateTimeOffset.UtcNow||!System.Text.RegularExpressions.Regex.IsMatch(ticket.Ticket,"^[A-Za-z0-9_-]{43}$"))
                throw new InvalidDataException("入服凭据校验失败。");
            return ticket;
        }
        finally{joinGate.Release();}
    }
    public async Task Logout()
    {
        try{if(Current?.Profile.Kind=="hub")await Authorized("/v1/auth/logout",new{});}
        catch(Exception ex) when(NetworkPolicy.IsNetwork(ex)){}
        finally {Current=null;joinGrant=null;joinGrantIdentity=null;vault.Delete("account");vault.Delete("msal");vault.Delete(MicrosoftAccountStorage.Key);if(Guid.TryParse(microsoftId,out var appId))vault.Delete("msal-"+appId.ToString("N"));}
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
        throw NetworkPolicy.Failure(new HttpRequestException("账号服务暂时不可用，请稍后重试。",null,response.StatusCode),"账号服务",response.RequestMessage?.RequestUri);
    }
    private void Store(AccountSession value){vault.Write("account",value with {RecoveryCode=null});Current=value;}
    private readonly SkinTextureCache skinCache=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher","skin-cache"));
    public async Task<SkinTexture?> Skin(string source="account",bool refresh=false)
        => (await SkinPreview(source,refresh))?.Texture;
    public async Task<SkinLoadResult?> SkinPreview(string source="account",bool refresh=false)
    {
        var current=Current;if(current is null)return null;
        if(source is not ("account" or "littleskin"))throw new InvalidDataException("皮肤来源无效。");
        if(source=="littleskin")
        {
            using var http=new HttpClient(NetworkPolicy.Handler(allowRedirect:false)){Timeout=TimeSpan.FromSeconds(5)};
            return await new ThirdPartySkins(http,skinCache).LoadLittleSkin(current.Profile.GameName,refresh);
        }
        return await skinCache.Load("account:"+current.Profile.Kind+":"+current.Profile.Id,token=>AccountSkin(current,token),refresh);
    }
    private async Task<SkinTexture?> AccountSkin(AccountSession current,CancellationToken token)
    {
        using var http=new HttpClient(NetworkPolicy.Handler(allowRedirect:false)){Timeout=TimeSpan.FromSeconds(5)};
        Uri address;
        var model=current.SkinModel;
        if(current.Profile.Kind=="microsoft")
        {
            if(current.AccessExpiresAt>DateTimeOffset.UtcNow)
            {
                using var profileRequest=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/minecraft/profile");
                profileRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",current.AccessToken);
                using var profileResponse=await http.SendAsync(profileRequest,token);
                if(profileResponse.IsSuccessStatusCode)
                {
                    var profile=await profileResponse.Content.ReadFromJsonAsync<JsonElement>(token);
                    var active=MinecraftAuthentication.ActiveSkin(profile);
                    current=current with {SkinUrl=active.Url,SkinModel=active.Model};model=current.SkinModel;
                    if(Current?.Profile.Id==current.Profile.Id&&Current.AccessToken==current.AccessToken)Store(current);
                }
            }
            if(string.IsNullOrEmpty(current.SkinUrl))return null;
            if(!Uri.TryCreate(current.SkinUrl,UriKind.Absolute,out var uri)||uri.Host!="textures.minecraft.net"||!uri.IsDefaultPort||!string.IsNullOrEmpty(uri.UserInfo)||uri.Scheme is not ("http" or "https")||!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,"^/texture/[a-fA-F0-9]{32,64}$"))throw new InvalidDataException("皮肤资源地址无效。");
            address=new UriBuilder(uri){Scheme="https",Port=-1,Query="",Fragment=""}.Uri;
        }
        else address=new Uri(api.BaseAddress!,"/v1/skins/"+Uri.EscapeDataString(current.Profile.GameName));
        // Texture requests use a separate client without account credentials or cookies.
        using var response=await http.GetAsync(address,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        await Check(response);
        if(response.Content.Headers.ContentLength>SkinImage.MaxBytes)throw new InvalidDataException("皮肤文件过大。");
        if(current.Profile.Kind=="hub")model=response.Headers.TryGetValues("X-Skin-Model",out var values)&&values.FirstOrDefault()=="slim"?"slim":"classic";
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        using var bytes=new MemoryStream();
        var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,token))>0){if(bytes.Length+count>SkinImage.MaxBytes)throw new InvalidDataException("皮肤文件过大。");bytes.Write(buffer,0,count);}
        return new(Convert.ToBase64String(SkinImage.Normalize(bytes.ToArray())),model);
    }
    public async Task<SkinTexture> SaveSkin(SkinTexture texture)
    {
        await Ensure();
        if(Current?.Profile.Kind!="hub")throw new InvalidDataException("请在微软账号页面更换正版皮肤。");
        texture=SkinImage.Normalize(texture);
        var result=await Authorized("/v1/account/skin",texture);
        var saved=result!.Value.Deserialize<SkinTexture>(Json.Options)!;
        skinCache.Store("account:"+Current.Profile.Kind+":"+Current.Profile.Id,saved);
        return saved;
    }
    public async Task<object> MicrosoftLogin(bool interactive=true,Func<MicrosoftDevicePrompt,Task>? showCode=null,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(microsoftId))
        {
            var result=await WindowsMicrosoftLogin.Login(vault,Current,interactive,microsoftWebUi,token);
            token.ThrowIfCancellationRequested();Store(result);
            return new {result.Profile};
        }
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
        using var http=new HttpClient(NetworkPolicy.Handler(allowRedirect:false)){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=2*1024*1024};
        var identity=await new MinecraftAuthentication(http).Login(oauth.AccessToken,token);
        var player=new PlayerProfile(identity.Id,oauth.Account.Username,identity.Name,"microsoft");
        token.ThrowIfCancellationRequested();
        Store(new(player,identity.AccessToken,identity.ExpiresAt,"",identity.ExpiresAt,MicrosoftAccountId:oauth.Account.HomeAccountId.Identifier,SkinUrl:identity.SkinUrl,SkinModel:identity.SkinModel,MicrosoftClientId:microsoftId));
        return new {Profile=player};
    }
}
