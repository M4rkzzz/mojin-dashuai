using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Boshan.Launcher;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;
using XboxAuthNet.XboxLive;

namespace Boshan.Desktop;

public static class WindowsMicrosoftLogin
{
    public const string Provider="cmllib-windows";
    public static async Task<AccountSession> Login(Vault vault,AccountSession? current,bool interactive,Func<IWebUI>? webUi,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!interactive&&current?.MicrosoftClientId!=Provider)throw new InvalidDataException("微软账号需要重新登录。");
        var storage=new MicrosoftAccountStorage(vault,interactive,token);
        var transport=new AuthTransport {InnerHandler=NetworkPolicy.Handler(allowRedirect:false)};
        using var http=new HttpClient(transport){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=2*1024*1024};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MojinDashuai/"+typeof(Accounts).Assembly.GetName().Version);
        try
        {
            var manager=new JsonXboxGameAccountManager(storage,JEGameAccount.FromSessionStorage,JsonXboxGameAccountManager.DefaultSerializerOption);
            var known=manager.GetAccounts();
            var account=interactive?manager.NewAccount():known.FirstOrDefault(x=>x.Identifier==current?.Profile.Id)??throw new InvalidDataException("微软账号需要重新登录。");
            var handler=new JELoginHandlerBuilder().WithAccountManager(manager).WithHttpClient(http).Build();
            if(interactive)
            {
                if(webUi is null)throw new InvalidDataException("请从启动器打开微软登录。");
                var state=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var authenticator=handler.CreateAuthenticator(account,token);
                authenticator.AddForceMicrosoftOAuthForJE(oauth=>oauth.Interactive(builder=>builder.WithWebUI(new MicrosoftOAuthWebUi(webUi(),state)),new CodeFlowAuthorizationParameter {State=state}));
                authenticator.AddForceXboxAuthForJE(xbox=>xbox.Basic());
                authenticator.AddForceJEAuthenticator();
                await authenticator.ExecuteForLauncherAsync();
            }
            else await handler.AuthenticateSilently(account,token);
            token.ThrowIfCancellationRequested();
            var identity=(JEGameAccount)account;
            var profile=identity.Profile;
            var credential=identity.Token;
            if(profile is null||credential is null||!Regex.IsMatch(profile.UUID??"","^[a-fA-F0-9]{32}$")||!Regex.IsMatch(profile.Username??"","^[A-Za-z0-9_]{3,16}$")||string.IsNullOrWhiteSpace(credential.AccessToken))
                throw new InvalidDataException("微软账号未返回有效的 Minecraft 角色。");
            var expiry=new DateTimeOffset(DateTime.SpecifyKind(credential.ExpiresOn,DateTimeKind.Utc));
            if(expiry<=DateTimeOffset.UtcNow||expiry>DateTimeOffset.UtcNow.AddDays(2))throw new InvalidDataException("微软登录凭据已失效，请重新登录。");
            using var ownershipRequest=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/entitlements/mcstore");
            ownershipRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential.AccessToken);
            using var ownershipResponse=await http.SendAsync(ownershipRequest,token);
            if(!ownershipResponse.IsSuccessStatusCode)throw new InvalidDataException("Minecraft 所有权验证暂时不可用，请稍后重试。");
            using var ownership=JsonDocument.Parse(await ownershipResponse.Content.ReadAsStringAsync(token));
            if(!ownership.RootElement.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array||!items.EnumerateArray().Any())
                throw new InvalidDataException("此微软账号未拥有 Minecraft Java 版。");
            var skin=profile.Skins?.FirstOrDefault(x=>x.State=="ACTIVE");
            var player=new PlayerProfile(profile.UUID!,profile.Username!,profile.Username!,"microsoft");
            var result=new AccountSession(player,credential.AccessToken,expiry,"",expiry,MicrosoftAccountId:profile.UUID,SkinUrl:skin?.Url,SkinModel:skin?.Variant=="SLIM"?"slim":"classic",MicrosoftClientId:Provider,MicrosoftXuid:identity.XboxTokens?.XstsToken?.XuiClaims?.XboxUserId);
            storage.Commit();
            return result;
        }
        catch(OperationCanceledException){throw;}
        catch(InvalidDataException){throw;}
        catch(NetworkFailure ex){throw NetworkPolicy.Failure(ex,"正版登录 / "+transport.Stage);}
        catch(HttpRequestException ex)
        {
            var error=new HttpRequestException("微软登录服务暂时无法连接，请稍后重试。",null,ex.StatusCode);
            error.Data["stage"]=transport.Stage;
            error.Data["requestError"]=ex.HttpRequestError.ToString();
            error.Data["transportErrors"]=SafeTransportErrors(ex);
            throw error;
        }
        catch(XboxAuthException ex){throw new InvalidDataException(ex.Error switch
        {
            "2148916233"=>"请先在 Xbox 官网创建玩家档案，再重新登录。",
            "2148916235"=>"当前账号所在地区无法使用 Xbox 服务。",
            "2148916238"=>"此账号需要在微软家庭中完成监护人授权。",
            "2148916227"=>"此 Xbox 账号已被限制登录。",
            _=>"Xbox 登录未通过，请检查账号后重试。"
        });}
        catch(JEAuthException ex){throw new InvalidDataException(ex.StatusCode switch
        {
            404=>"此微软账号没有可用的 Minecraft Java 版角色。",
            403=>"Minecraft 拒绝了此次登录，请稍后重试。",
            _=>"Minecraft 账号验证未完成，请重新登录。"
        });}
        catch(AuthCodeException ex){throw new InvalidDataException(ex.Error=="access_denied"?"已取消微软授权，可重新登录。":"微软授权未完成，请重新登录。");}
        catch(MicrosoftOAuthException){throw new InvalidDataException("微软账号需要重新登录。");}
        catch(Exception){throw new InvalidDataException("微软登录未完成，请重新登录。");}
    }
    private static string SafeTransportErrors(Exception exception)
    {
        var result=new List<string>();
        for(Exception? current=exception;current is not null;current=current.InnerException)
            result.Add(current.GetType().Name+":"+current.HResult.ToString("X8"));
        return string.Join(",",result);
    }
    private sealed class AuthTransport : DelegatingHandler
    {
        public string Stage {get;private set;}="prepare";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Stage=request.RequestUri?.Host switch
            {
                "login.live.com"=>"microsoft-token",
                "user.auth.xboxlive.com"=>"xbox-user",
                "xsts.auth.xboxlive.com"=>"xbox-security",
                "api.minecraftservices.com" when request.RequestUri.AbsolutePath=="/authentication/login_with_xbox"=>"minecraft-token",
                "api.minecraftservices.com" when request.RequestUri.AbsolutePath=="/minecraft/profile"=>"minecraft-profile",
                "api.minecraftservices.com" when request.RequestUri.AbsolutePath=="/entitlements/mcstore"=>"minecraft-ownership",
                _=>"authentication"
            };
            return base.SendAsync(request,token);
        }
    }
}
