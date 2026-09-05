using Boshan.Launcher;
using System.Net;
using Xunit;

namespace Hub.Tests;

public class NetworkPolicyTests
{
    [Fact] public void DefaultsToDirectAndRequiresExplicitManualProxy()
    {
        var settings=new LauncherSettings();Assert.Equal("direct",settings.ProxyMode);settings.Validate();
        settings.ProxyMode="manual";Assert.Throws<InvalidDataException>(settings.Validate);
        settings.Proxy="http://127.0.0.1:7890";settings.Validate();
        settings.Proxy="http://name:password@127.0.0.1:7890";Assert.Throws<InvalidDataException>(settings.Validate);
    }
    [Fact] public void DiagnosticsDoNotContainQueryCredentialsOrPlayerName()
    {
        var exception=new HttpRequestException(HttpRequestError.NameResolutionError,"token=must-never-be-logged");
        var diagnostic=NetworkPolicy.Failure(exception,"下载文件",new Uri("https://files.example/v1/skins/Player?token=secret"),"mods/test.jar",2).Diagnostic;
        var text=System.Text.Json.JsonSerializer.Serialize(diagnostic);
        Assert.DoesNotContain("secret",text);Assert.DoesNotContain("Player",text);Assert.DoesNotContain("must-never",text);
        Assert.Equal("files.example",diagnostic.Host);Assert.Equal("DNS 解析失败",diagnostic.Category);Assert.Equal(2,diagnostic.Attempt);
    }
    [Fact] public void ProductionMetadataUsesOnlyTheUnifiedEndpoint()
    {
        var routes=NetworkPolicy.MetadataSources(new Uri(NetworkPolicy.LegacyApi+"/v1/catalog")).ToArray();
        Assert.Single(routes);Assert.Equal(NetworkPolicy.DirectApi+"/v1/catalog",routes[0].AbsoluteUri);
    }
    [Fact] public async Task UnifiedDownloadNeverContactsManifestSources()
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-origin-"+Guid.NewGuid().ToString("N"));var body=new byte[]{1,2,3};
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var file=new ContentFile("mods/example.jar",body.Length,hash,["https://github.com/unavailable","https://libraries.minecraft.net/unavailable"],FilePolicy.Managed,"test fixture");
        var urls=new List<string>();
        try{
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(request=>{urls.Add(request.RequestUri!.AbsoluteUri);return new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};}),NetworkPolicy.DirectApi);
            await downloader.Get(file);Assert.Equal([NetworkPolicy.DirectApi+"/objects/sha256/"+hash],urls);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));}
}
