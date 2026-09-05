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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedDownloadNeverContactsManifestSourcesIncludingLegacyOfficialOnly(bool officialOnly)
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-origin-"+Guid.NewGuid().ToString("N"));var body=new byte[]{1,2,3};
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var file=new ContentFile("mods/example.jar",body.Length,hash,["https://github.com/unavailable","https://libraries.minecraft.net/unavailable"],FilePolicy.Managed,"test fixture");
        if(officialOnly)file=file with {OfficialOnly=true,Sources=["https://cdn.modrinth.com/data/project/versions/version/mod.jar"]};
        var urls=new List<string>();
        try{
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(request=>{urls.Add(request.RequestUri!.AbsoluteUri);return new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};}),NetworkPolicy.DirectApi);
            await downloader.Get(file);Assert.Equal([NetworkPolicy.DirectApi+"/objects/sha256/"+hash],urls);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutUnifiedOriginManifestSourcesRemainAvailable(bool officialOnly)
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-origin-"+Guid.NewGuid().ToString("N"));var body=new byte[]{4,5,6};
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var source=officialOnly?"https://cdn.modrinth.com/data/project/versions/version/mod.jar":"https://source.example/mod.jar";
        var file=new ContentFile("mods/example.jar",body.Length,hash,[source],FilePolicy.Managed,"test fixture",OfficialOnly:officialOnly);
        var urls=new List<string>();
        try
        {
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(request=>{urls.Add(request.RequestUri!.AbsoluteUri);return new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};}));
            var path=await downloader.Get(file);Assert.Equal(new[]{source},urls);Assert.True(await ContentSecurity.Matches(path,file));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public async Task LegacyOfficialOnlyObjectsDownloadedFromUnifiedOriginStillRequireTheirExactHash()
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-origin-"+Guid.NewGuid().ToString("N"));var body=new byte[]{7,8,9};
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var file=new ContentFile("mods/example.jar",body.Length,hash,["https://cdn.modrinth.com/data/project/versions/version/mod.jar"],FilePolicy.Managed,"legacy fixture",OfficialOnly:true);
        var calls=0;
        try
        {
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(request=>
            {
                Assert.Equal(NetworkPolicy.DirectApi+"/objects/sha256/"+hash,request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Headers.Authorization);Assert.False(request.Headers.Contains("Cookie"));
                Assert.False(File.Exists(Path.Combine(root,hash)));
                return new(HttpStatusCode.OK){Content=new ByteArrayContent(++calls==1?new byte[]{9,8,7}:body)};
            }),NetworkPolicy.DirectApi);
            var path=await downloader.Get(file);Assert.Equal(2,calls);Assert.True(await ContentSecurity.Matches(path,file));
            Assert.False(File.Exists(Path.Combine(root,hash+".part")));
            Assert.Equal(path,await downloader.Get(file));Assert.Equal(2,calls);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("https://cdn.modrinth.com/data/project/versions/version/mod.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/1234/567/mod.jar")]
    [InlineData("https://edge.forgecdn.net/files/1234/567/mod.jar")]
    public void OfficialOnlyFilesAcceptOneAnonymousFixedOfficialHttpsSource(string source)
    {
        var file=new ContentFile("mods/official.jar",1,new string('a',64),[source],FilePolicy.Managed,"official distribution only",OfficialOnly:true);
        ContentSecurity.ValidateFile(file);
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.ValidateFile(file with {Sources=[source,source]}));
    }
    [Theory]
    [InlineData("http://cdn.modrinth.com/data/project/versions/version/mod.jar")]
    [InlineData("https://user:secret@cdn.modrinth.com/data/project/versions/version/mod.jar")]
    [InlineData("https://cdn.modrinth.com:444/data/project/versions/version/mod.jar")]
    [InlineData("https://cdn.modrinth.com/data/project/versions/version/mod.jar?token=secret")]
    [InlineData("https://cdn.modrinth.com/data/project/versions/version/mod.jar#fragment")]
    [InlineData("https://cdn.modrinth.com/")]
    [InlineData("https://cdn.modrinth.com.evil.example/mod.jar")]
    [InlineData("https://unified.example/objects/sha256/abcd")]
    [InlineData("https://github.com/project/releases/latest/download/mod.jar")]
    public void OfficialOnlyFilesRejectCredentialsDynamicOrUnapprovedSources(string source)
    {
        var file=new ContentFile("mods/official.jar",1,new string('a',64),[source],FilePolicy.Managed,"official distribution only",OfficialOnly:true);
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.ValidateFile(file));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));}
}
