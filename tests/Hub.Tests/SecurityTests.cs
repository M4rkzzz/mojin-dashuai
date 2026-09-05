using System.Net;
using System.Security.Cryptography;
using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class SecurityTests
{
    [Theory]
    [InlineData("../outside.txt")][InlineData("mods/../a.jar")][InlineData("/absolute")][InlineData("C:/evil")][InlineData("mods\\evil.jar")][InlineData("mods/con.jar")][InlineData("mods/a.jar:evil")][InlineData("mods/a. ")][InlineData("mods//a.jar")]
    public void RejectUnsafePaths(string path)=>Assert.Throws<InvalidDataException>(()=>ContentSecurity.SafePath(Path.GetTempPath(),path));

    [Fact]
    public void SignatureRejectsTamperingAndUnknownKey()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys=new Dictionary<string,string>{{"test",key.ExportSubjectPublicKeyInfoPem()}};
        var signed=ContentSecurity.Sign(new {Sequence=7},"test",key.ExportPkcs8PrivateKeyPem());
        Assert.Equal(7,ContentSecurity.Verify<System.Text.Json.JsonElement>(signed,keys).GetProperty("sequence").GetInt32());
        var payload=Convert.FromBase64String(signed.Payload);payload[4]^=1;
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Verify<object>(signed with{Payload=Convert.ToBase64String(payload)},keys));
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Verify<object>(signed,new Dictionary<string,string>()));
    }
    private static PackManifest Manifest(int java=25,string loader="cleanroom")
    {
        var file=new ContentFile("x",1,new string('a',64),["https://example.org/x"],FilePolicy.Managed,"test source");
        return new("mb","1",1,"1.12.2",loader,"0.5.17-alpha","cleanroom",new("java25",java,"25","windows-x64",file,"jdk/bin/java.exe",100),8736,"mb-1",[],["tested"]);
    }
    [Theory][InlineData(8)][InlineData(17)][InlineData(21)]
    public void MeatballNeverAcceptsOtherJava(int major)=>Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(Manifest(major)));
    [Fact]public void MeatballNeverAcceptsForge()=>Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(Manifest(loader:"forge")));
    [Theory][InlineData("saves/world/level.dat")][InlineData("journeymap/cache.dat")][InlineData(".hub/installed.json")][InlineData("options.txt")]
    public void ManifestCannotOwnPlayerData(string path)
    {
        var manifest=Manifest();var file=manifest.Runtime.Archive with{Path=path};
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(manifest with{Files=[file]}));
    }
    [Fact]public void DuplicatePathsAreCaseInsensitive()
    {
        var manifest=Manifest();Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(manifest with{Files=[manifest.Runtime.Archive with{Path="mods/A.jar"},manifest.Runtime.Archive with{Path="mods/a.jar"}]}));
    }
    [Fact]
    public void RecoveryRestoresOldFilesAndRemovesOnlyNewManagedFiles()
    {
        var root=Path.Combine(Path.GetTempPath(),"boshan-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var installer=new TransactionalInstaller(root);var instance=installer.InstancePath("mb");
            Directory.CreateDirectory(Path.Combine(instance,".hub/transactions/abc/backup/mods"));Directory.CreateDirectory(Path.Combine(instance,"mods"));Directory.CreateDirectory(Path.Combine(instance,"saves"));
            File.WriteAllText(Path.Combine(instance,".hub/transactions/abc/backup/mods/old.jar"),"old");File.WriteAllText(Path.Combine(instance,"mods/old.jar"),"new");File.WriteAllText(Path.Combine(instance,"mods/added.jar"),"added");File.WriteAllText(Path.Combine(instance,"saves/keep.txt"),"player");
            Json.Write(Path.Combine(instance,".hub/journal.json"),new UpdateJournal("abc","committing",Manifest(),null,[new("mods/old.jar",true,false,null),new("mods/added.jar",false,false,null)]));
            installer.Recover("mb");
            Assert.Equal("old",File.ReadAllText(Path.Combine(instance,"mods/old.jar")));Assert.False(File.Exists(Path.Combine(instance,"mods/added.jar")));Assert.Equal("player",File.ReadAllText(Path.Combine(instance,"saves/keep.txt")));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task DownloaderRejectsCorruptionAndRetriesOtherSourceWithoutCredentials()
    {
        var root=Path.Combine(Path.GetTempPath(),"boshan-test-"+Guid.NewGuid().ToString("N"));var content=Encoding.UTF8.GetBytes("correct");var calls=0;
        var handler=new FakeHttp(req=>{
            Assert.Null(req.Headers.Authorization);Assert.False(req.Headers.Contains("Cookie"));calls++;
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(req.RequestUri!.Host=="bad.example"?Encoding.UTF8.GetBytes("corrupt"):content)};
        });
        try
        {
            using var download=new Downloader(root,new LauncherSettings(),handler);
            var file=new ContentFile("mod.jar",content.Length,Convert.ToHexString(SHA256.HashData(content)),["https://bad.example/x","https://good.example/x"],FilePolicy.Managed,"test");
            var path=await download.Get(file);Assert.Equal("correct",File.ReadAllText(path));Assert.Equal(2,calls);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task DownloadResumeWorksWhenServerIgnoresRange()
    {
        var root=Path.Combine(Path.GetTempPath(),"boshan-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var bytes=Encoding.UTF8.GetBytes("full content");var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();File.WriteAllText(Path.Combine(root,hash+".part"),"full");
        try
        {
            using var downloader=new Downloader(root,new LauncherSettings(),new FakeHttp(req=>{Assert.Equal(4,req.Headers.Range!.Ranges.Single().From);return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}));
            var path=await downloader.Get(new("file",bytes.Length,hash,["https://example.org/file"],FilePolicy.Managed,"test"));Assert.Equal("full content",File.ReadAllText(path));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class FakeHttp(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(send(request));}
}
