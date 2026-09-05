using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Boshan.Launcher;
using Boshan.Shared;
using Xunit;

namespace Boshan.Tests;

public sealed class ThirdPartySkinTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-skin-"+Guid.NewGuid().ToString("N"));
    private const string Hash="46d1a37c186325a4f157cae1d1348ccabfb4df8832f2031966e68c625afa73b6";

    [Theory]
    [InlineData("skins","slim","slim")]
    [InlineData("textures","default","classic")]
    public async Task LoadsDocumentedAndLittleSkinSchemasWithoutSendingCredentials(string field,string model,string expected)
    {
        var requests=new List<Uri>();
        using var http=new HttpClient(new Handler(request=>
        {
            requests.Add(request.RequestUri!);Assert.Null(request.Headers.Authorization);Assert.False(request.Headers.Contains("Cookie"));
            return request.RequestUri!.AbsolutePath.EndsWith(".json")?Response("{\"username\":\"Steve\",\""+field+"\":{\""+model+"\":\""+Hash+"\"}}"):Response(Png());
        }));
        var result=await new ThirdPartySkins(http,new SkinTextureCache(root)).LittleSkin("Steve");
        Assert.NotNull(result);Assert.Equal(expected,result.Model);
        Assert.Equal(new[]{"https://littleskin.cn/csl/Steve.json","https://littleskin.cn/csl/textures/"+Hash},requests.Select(x=>x.AbsoluteUri));
        Assert.Equal(SkinImage.Normalize(Png()),Convert.FromBase64String(result.PngBase64));
    }

    [Fact]
    public void HonorsApiModelPreferenceAndAcceptsLegacyField()
    {
        using var ordered=JsonDocument.Parse("{\"textures\":{\"default\":\""+Hash+"\",\"slim\":\""+Hash+"\"}}");
        Assert.Equal("classic",ThirdPartySkins.TextureReference(ordered.RootElement)!.Value.Model);
        using var legacy=JsonDocument.Parse("{\"skin\":\""+Hash+"\"}");
        Assert.Equal("classic",ThirdPartySkins.TextureReference(legacy.RootElement)!.Value.Model);
    }

    [Theory]
    [InlineData("https://other.example/player.png")]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("../../private")]
    [InlineData("{hash}?token=private")]
    public async Task RejectsUntrustedTextureReferencesWithoutRequestingThem(string reference)
    {
        var count=0;
        using var http=new HttpClient(new Handler(_=>{count++;return Response(JsonSerializer.Serialize(new {skins=new {slim=reference}}));}));
        Assert.Null(await new ThirdPartySkins(http,new SkinTextureCache(root)).LittleSkin("Steve"));Assert.Equal(1,count);
    }

    [Fact]
    public async Task FailureUsesValidatedCacheAndCachesRemainIsolatedByPlayer()
    {
        var cache=new SkinTextureCache(root);var skin=new SkinTexture(Convert.ToBase64String(Png()),"slim");
        cache.Store("littleskin:steve",skin);
        using var http=new HttpClient(new Handler(_=>throw new HttpRequestException("offline")));
        var skins=new ThirdPartySkins(http,cache);
        Assert.Equal(skin,await skins.LittleSkin("Steve",refresh:true));
        Assert.Null(await skins.LittleSkin("OtherPlayer",refresh:true));
    }

    [Fact]
    public async Task FreshCacheAvoidsNetworkAndOversizedOrDamagedFilesAreNotCached()
    {
        var cache=new SkinTextureCache(root);cache.Store("littleskin:steve",new(Convert.ToBase64String(Png()),"classic"));
        using var offline=new HttpClient(new Handler(_=>throw new InvalidOperationException("should not request")));
        Assert.NotNull(await new ThirdPartySkins(offline,cache).LittleSkin("Steve"));
        using var damaged=new HttpClient(new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith(".json")?Response("{\"skin\":\""+Hash+"\"}"):Response(new byte[SkinImage.MaxBytes+1])));
        Assert.Null(await new ThirdPartySkins(damaged,cache).LittleSkin("OtherPlayer"));
        Assert.Null(cache.Cached("littleskin:otherplayer"));
    }

    [Fact]
    public async Task ExplicitSkinRemovalClearsTheOldCachedTexture()
    {
        var cache=new SkinTextureCache(root);cache.Store("littleskin:steve",new(Convert.ToBase64String(Png()),"classic"));
        using var http=new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new ThirdPartySkins(http,cache).LittleSkin("Steve",refresh:true));
        Assert.Null(cache.Cached("littleskin:steve"));
    }

    [Fact]
    public void InstanceConfigurationPreservesCustomEntriesAndSettingsAndBacksUpChanges()
    {
        var configPath=Path.Combine(root,"CustomSkinLoader","CustomSkinLoader.json");Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        const string original="""
            {"version":"14.17","cacheExpiry":75,"customFlag":true,"loadlist":[
            {"name":"Mojin","type":"CustomSkinAPI","root":"https://launcher.boshan.uk/v1/skins/csl/"},
            {"name":"Mojang","type":"MojangAPI","sessionRoot":"https://sessionserver.mojang.com/"},
            {"name":"My skin site","type":"CustomSkinAPI","root":"https://my.example/csl/","userAgent":"mine"}]}
            """;
        File.WriteAllText(configPath,original);
        Assert.True(ThirdPartySkins.ConfigureInstance(root,"littleskin"));
        var config=JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.Equal("LittleSkin",config["loadlist"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(75,config["cacheExpiry"]!.GetValue<int>());Assert.True(config["customFlag"]!.GetValue<bool>());
        Assert.Contains(config["loadlist"]!.AsArray(),x=>x?["name"]?.GetValue<string>()=="My skin site"&&x["userAgent"]?.GetValue<string>()=="mine");
        var backups=Directory.GetFiles(Path.Combine(root,".hub","skin-config-backups"));Assert.Single(backups);Assert.Equal(original,File.ReadAllText(backups[0]));
        Assert.True(ThirdPartySkins.ConfigureInstance(root,"littleskin"));Assert.Single(Directory.GetFiles(Path.Combine(root,".hub","skin-config-backups")));
        Assert.True(ThirdPartySkins.ConfigureInstance(root,"account"));config=JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.Equal("Mojin",config["loadlist"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(1,config["loadlist"]!.AsArray().Count(x=>x?["name"]?.GetValue<string>()=="LittleSkin"));
        Assert.Equal("14.17",config["version"]!.GetValue<string>());
    }

    [Fact]
    public void BrokenConfigurationDoesNotBlockLaunchOrOverwriteUserFile()
    {
        var path=Path.Combine(root,"CustomSkinLoader","CustomSkinLoader.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"broken-json");
        Assert.False(ThirdPartySkins.ConfigureInstance(root,"littleskin"));Assert.Equal("broken-json",File.ReadAllText(path));
    }

    private static HttpResponseMessage Response(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Response(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));
    }
    private static byte[] Png()
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        var header=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(header,64);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),64);header[8]=8;header[9]=6;
        WriteChunk(output,"IHDR",header);
        using var pixels=new MemoryStream();using(var compressor=new ZLibStream(pixels,CompressionLevel.Fastest,true))compressor.Write(new byte[64*257]);
        WriteChunk(output,"IDAT",pixels.ToArray());WriteChunk(output,"IEND",[]);return output.ToArray();
    }
    private static void WriteChunk(Stream stream,string name,byte[] data)
    {
        var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,data.Length);stream.Write(length);
        var content=Encoding.ASCII.GetBytes(name).Concat(data).ToArray();stream.Write(content);
        uint crc=0xffffffff;
        foreach(var value in content){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}
        BinaryPrimitives.WriteUInt32BigEndian(length,~crc);stream.Write(length);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
