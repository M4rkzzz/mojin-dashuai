using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class MicrosoftLoginTests
{
    [Theory]
    [InlineData("https://evil.example/link")]
    [InlineData("http://microsoft.com/link")]
    [InlineData("https://microsoft.com.evil.example/link")]
    [InlineData("https://microsoft.com@evil.example/link")]
    [InlineData("https://www.microsoft.com:444/link")]
    [InlineData("https://www.microsoft.com/link?next=https://evil.example")]
    [InlineData("https://www.microsoft.com/other")]
    public void DevicePromptCannotOpenAnUntrustedWebsite(string url) =>
        Assert.Throws<InvalidDataException>(()=>MicrosoftDevicePrompt.Create("ABCD-EFGH",url,DateTimeOffset.UtcNow.AddMinutes(5)));

    [Fact]
    public void DevicePromptExposesOnlyTheDisplayFields()
    {
        var prompt=MicrosoftDevicePrompt.Create("ABCD-EFGH","https://www.microsoft.com/link",DateTimeOffset.UtcNow.AddMinutes(5));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(prompt,Json.Options));
        Assert.Equal(new[]{"expiresAt","userCode","verificationUrl"},json.RootElement.EnumerateObject().Select(p=>p.Name).Order().ToArray());
        Assert.Throws<InvalidDataException>(()=>MicrosoftDevicePrompt.Create("ABCD-EFGH",prompt.VerificationUrl,DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public async Task XboxAndMinecraftExchangeUsesTheCorrectCredentialAtEachStage()
    {
        using var handler=new FakeIdentityServer();using var http=new HttpClient(handler);
        var result=await new MinecraftAuthentication(http).Login("test-ms-token");
        Assert.Equal("Exact_Name",result.Name);
        Assert.Equal(new string('a',32),result.Id);
        Assert.Equal("test-game-token",result.AccessToken);
        Assert.Equal("slim",result.SkinModel);
        Assert.InRange(result.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(59),DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.Equal(5,handler.Calls.Count);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task NoOwnershipDoesNotProduceAGameSession()
    {
        using var handler=new FakeIdentityServer {Owned=false};using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>new MinecraftAuthentication(http).Login("test-ms-token"));
        Assert.Contains("未拥有",error.Message);
        Assert.Equal(4,handler.Calls.Count);
    }

    [Theory]
    [InlineData(2148916227,"封禁")]
    [InlineData(2148916233,"游戏档案")]
    [InlineData(2148916235,"地区")]
    [InlineData(2148916238,"家庭组织者")]
    public async Task XboxErrorsAreActionableAndDoNotExposeResponses(long code,string message)
    {
        using var handler=new FakeIdentityServer {XboxError=code};using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>new MinecraftAuthentication(http).Login("test-ms-token"));
        Assert.Contains(message,error.Message);
        Assert.DoesNotContain("SENSITIVE",error.Message);
        Assert.Equal(2,handler.Calls.Count);
    }

    [Fact]
    public async Task RejectedApplicationIsNotMisreportedAsMissingOwnership()
    {
        using var handler=new FakeIdentityServer {AppRejected=true};using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>new MinecraftAuthentication(http).Login("test-ms-token"));
        Assert.Contains("登录应用",error.Message);
        Assert.DoesNotContain("未拥有",error.Message);
        Assert.DoesNotContain("SENSITIVE",error.Message);
        Assert.Equal(3,handler.Calls.Count);
    }

    [Fact]
    public async Task CancellationStopsTheRemainingVerificationSteps()
    {
        using var cancel=new CancellationTokenSource();
        using var handler=new FakeIdentityServer {AfterFirst=()=>cancel.Cancel()};using var http=new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new MinecraftAuthentication(http).Login("test-ms-token",cancel.Token));
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("0.1.1",true)]
    [InlineData("0.1.0",true)]
    [InlineData("0.1.2",false)]
    [InlineData("invalid",false)]
    public void CatalogComparesAgainstTheInstalledLauncherVersion(string required,bool accepted)
    {
        if(accepted)CatalogClient.ValidateMinimumLauncher(required,new Version(0,1,1,0));
        else Assert.Throws<InvalidDataException>(()=>CatalogClient.ValidateMinimumLauncher(required,new Version(0,1,1,0)));
    }

    [Fact]
    public void CompleteClientCatalogRequiresTheCapableNumericBuild()
    {
        Assert.Throws<InvalidDataException>(()=>CatalogClient.ValidateMinimumLauncher("0.1.2.12",new Version(0,1,2,0)));
        CatalogClient.ValidateMinimumLauncher("0.1.2.12",typeof(CatalogClient).Assembly.GetName().Version!);
    }

    private sealed class FakeIdentityServer:HttpMessageHandler
    {
        public List<string> Calls {get;}=[];
        public bool Owned {get;init;}=true;
        public bool AppRejected {get;init;}
        public long? XboxError {get;init;}
        public Action? AfterFirst {get;init;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.False(request.Headers.Contains("Cookie"));
            var url=request.RequestUri!.AbsoluteUri;Calls.Add(url);
            if(Calls.Count<=3)Assert.Null(request.Headers.Authorization);
            else Assert.Equal("Bearer test-game-token",request.Headers.Authorization!.ToString());
            var body=request.Content is null?default:await request.Content.ReadFromJsonAsync<JsonElement>(token);
            object response;
            switch(url)
            {
                case "https://user.auth.xboxlive.com/user/authenticate":
                    Assert.Equal("d=test-ms-token",body.GetProperty("Properties").GetProperty("RpsTicket").GetString());
                    response=new {Token="test-xbox-token"};AfterFirst?.Invoke();break;
                case "https://xsts.auth.xboxlive.com/xsts/authorize":
                    Assert.Equal("test-xbox-token",body.GetProperty("Properties").GetProperty("UserTokens")[0].GetString());
                    if(XboxError is not null)return Reply(new {XErr=XboxError,Message="SENSITIVE RESPONSE"},HttpStatusCode.Unauthorized);
                    response=new {Token="test-xsts-token",DisplayClaims=new {xui=new[]{new {uhs="test-uhs"}}}};break;
                case "https://api.minecraftservices.com/authentication/login_with_xbox":
                    Assert.Equal("XBL3.0 x=test-uhs;test-xsts-token",body.GetProperty("identityToken").GetString());
                    if(AppRejected)return Reply(new {error="SENSITIVE RESPONSE"},HttpStatusCode.Forbidden);
                    response=new {access_token="test-game-token",expires_in=3600};break;
                case "https://api.minecraftservices.com/entitlements/mcstore":
                    response=new {items=Owned?new[]{new {name="game_minecraft"}}:[]};break;
                case "https://api.minecraftservices.com/minecraft/profile":
                    response=new {id=new string('a',32),name="Exact_Name",skins=new[]{new {state="ACTIVE",variant="SLIM",url="https://textures.minecraft.net/texture/"+new string('a',64)}}};break;
                default:throw new InvalidOperationException("Unexpected authentication destination.");
            }
            return Reply(response);
        }
        private static HttpResponseMessage Reply(object body,HttpStatusCode code=HttpStatusCode.OK)=>new(code){Content=JsonContent.Create(body,options:JsonSerializerOptions.Default)};
    }
}
