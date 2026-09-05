using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.Json;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class LauncherUpdateTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-launcher-update-"+Guid.NewGuid().ToString("N"));
    private readonly ECDsa key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly LauncherUpdates updates;
    private readonly string[] names=[LauncherUpdates.EntryPoint,"MojinDashuai.Launcher.dll","launcher.json","web/index.html"];
    public LauncherUpdateTests(){Directory.CreateDirectory(root);updates=new(Path.Combine(root,"updates"),new Dictionary<string,string>{{"test",key.ExportSubjectPublicKeyInfoPem()}});}
    private SignedEnvelope Sign(LauncherRelease release)=>ContentSecurity.Sign(release,"test",key.ExportPkcs8PrivateKeyPem());
    private static ContentFile Record(string name,byte[] bytes)=>new(name,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)),["https://download.example/launcher.zip"],FilePolicy.Managed,"test fixture");
    private (LauncherRelease Release,string Zip) Fixture(long sequence=1,string? extra=null,bool omit=false,bool mismatch=false)
    {
        var bytes=Encoding.UTF8.GetBytes("version "+sequence);
        var files=names.Select(name=>Record(name,bytes)).ToArray();
        var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        {
            foreach(var name in omit?names.Skip(1):names)
            {using var stream=zip.CreateEntry(name).Open();stream.Write(mismatch?Encoding.UTF8.GetBytes("incorrect"):bytes);}
            if(extra is not null){using var stream=zip.CreateEntry(extra).Open();stream.Write(bytes);}
        }
        return(new(sequence,"0.2.0-beta.1","windows-x64",Record("launcher.zip",File.ReadAllBytes(path)),files),path);
    }
    [Fact]
    public async Task PreparedUpdateLeavesOldInstallationAndGamesUntouchedAndActivatesOnlyExplicitly()
    {
        var old=Path.Combine(root,"old");Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"game-save.dat"),"keep");
        var (release,zip)=Fixture();var prepared=await updates.PrepareArchive(Sign(release),zip);
        Assert.Equal("keep",File.ReadAllText(Path.Combine(old,"game-save.dat")));
        Assert.False(File.Exists(Path.Combine(updates.Root,"active.signed.json")));
        Assert.Equal(prepared.Executable,(await updates.Ready(old,new Version(0,1,1)))!.Executable);
        updates.Activate(prepared);
        File.Delete(Path.Combine(updates.Root,"ready.signed.json"));
        Assert.NotNull(await updates.Ready(old,new Version(0,1,1)));
        updates.Activate(prepared); // An existing active version can also be reopened.
        Assert.Null(await updates.Ready(prepared.Directory,new Version(0,2,0)));
        Assert.Null(await updates.Ready(old,new Version(0,3,0)));
    }
    [Fact]
    public void MetadataRejectsReplayChangedSequenceAndInvalidSignatures()
    {
        var (release,_)=Fixture(2);var signed=Sign(release);updates.AcceptMetadata(signed);
        Assert.Throws<InvalidDataException>(()=>updates.AcceptMetadata(Sign(release with {Sequence=1})));
        Assert.Throws<InvalidDataException>(()=>updates.AcceptMetadata(Sign(release with {Version="0.2.1"})));
        Assert.Throws<InvalidDataException>(()=>updates.AcceptMetadata(signed with {Signature=Convert.ToBase64String(new byte[64])}));
    }
    [Theory]
    [InlineData("../escape.exe",false,false)]
    [InlineData("unlisted.dll",false,false)]
    [InlineData("launcher.JSON",false,false)]
    [InlineData(null,true,false)]
    [InlineData(null,false,true)]
    public async Task InvalidArchiveCannotChangeTheReadyVersion(string? extra,bool omit,bool mismatch)
    {
        var (old,oldZip)=Fixture();var previous=await updates.PrepareArchive(Sign(old),oldZip);updates.Activate(previous);
        var (bad,badZip)=Fixture(2,extra,omit,mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(()=>updates.PrepareArchive(Sign(bad),badZip));
        Assert.Equal(1,(await updates.Ready(Path.Combine(root,"old"),new Version(0,1,0)))!.Release.Sequence);
        Assert.False(File.Exists(Path.Combine(root,"escape.exe")));
        Assert.Empty(Directory.GetDirectories(updates.Root,"stage-*"));
    }
    [Fact]
    public async Task FailedCandidateFallsBackToPreviouslyActivatedVersion()
    {
        var (first,zip)=Fixture();var active=await updates.PrepareArchive(Sign(first),zip);updates.Activate(active);
        var (next,newZip)=Fixture(2);var candidate=await updates.PrepareArchive(Sign(next),newZip);
        updates.Reject(candidate);
        Assert.True(updates.HasFailed(next));
        Assert.Equal(1,(await updates.Ready(Path.Combine(root,"old"),new Version(0,1,0)))!.Release.Sequence);
        updates.Retry(next);Assert.False(updates.HasFailed(next));
    }
    [Fact]
    public async Task CorruptedArchiveAndPreparedExecutableNeverBecomeRunnable()
    {
        var (release,zip)=Fixture();var prepared=await updates.PrepareArchive(Sign(release),zip);
        File.WriteAllText(prepared.Executable,"damaged");
        Assert.Null(await updates.Ready(Path.Combine(root,"old"),new Version(0,1,0)));
        var repaired=await updates.PrepareArchive(Sign(release),zip);
        Assert.True(await ContentSecurity.Matches(repaired.Executable,release.Files[0]));
        File.AppendAllText(zip,"bad archive");
        await Assert.ThrowsAsync<InvalidDataException>(()=>updates.PrepareArchive(Sign(release),zip));
    }
    private sealed class ObjectsHandler(Dictionary<string,byte[]> contents):HttpMessageHandler
    {
        internal readonly List<string> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var hash=request.RequestUri!.Segments.Last();Requests.Add(hash);
            return Task.FromResult(contents.TryGetValue(hash,out var bytes)
                ?new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}
                :new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private static ContentFile ObjectFile(string name,byte[] bytes)
    {
        var file=Record(name,bytes);
        return file with {Sources=["https://download.example/objects/sha256/"+file.Sha256]};
    }
    [Fact]
    public async Task DifferentialUpdateDownloadsOnlyChangedFilesAndDoesNotCarryDeletedFilesForward()
    {
        var (first,zip)=Fixture();var old=await updates.PrepareArchive(Sign(first),zip);updates.Activate(old);
        File.WriteAllText(Path.Combine(old.Directory,"obsolete.dll"),"old version only");
        var changed=Encoding.UTF8.GetBytes("new executable");
        var files=first.Files.Select(file=>file.Path==LauncherUpdates.EntryPoint?ObjectFile(file.Path,changed):file).ToArray();
        var next=first with {Sequence=2,Version="0.2.0-beta.2",Files=files,Differential=true};
        using var handler=new ObjectsHandler(new(){{files[0].Sha256,changed}});
        using var downloader=new Downloader(Path.Combine(updates.Root,"cache"),new LauncherSettings(),handler);
        Assert.Equal(changed.Length,await updates.PendingDownloadBytes(next,old.Directory));
        long downloaded=0;
        var prepared=await updates.Prepare(Sign(next),downloader,n=>downloaded+=n,currentDirectory:old.Directory);
        Assert.Equal(changed.Length,downloaded);Assert.Equal([files[0].Sha256],handler.Requests);
        Assert.False(File.Exists(Path.Combine(prepared.Directory,"obsolete.dll")));
        Assert.Equal("version 1",File.ReadAllText(old.Executable));
        Assert.Equal("new executable",File.ReadAllText(prepared.Executable));
        Assert.Equal(1,ContentSecurity.Verify<LauncherRelease>(Json.Read<SignedEnvelope>(Path.Combine(updates.Root,"active.signed.json")),new Dictionary<string,string>{{"test",key.ExportSubjectPublicKeyInfoPem()}}).Sequence);
        Assert.Equal(0,await updates.PendingDownloadBytes(next,old.Directory));
    }
    [Fact]
    public async Task DifferentialUpdateRepairsCorruptFilesAndReusesDownloadCacheAfterInterruption()
    {
        var (first,zip)=Fixture();var old=await updates.PrepareArchive(Sign(first),zip);updates.Activate(old);
        var unchanged=Encoding.UTF8.GetBytes("version 1");
        var changed=Encoding.UTF8.GetBytes("new web page");
        var files=first.Files.Select(file=>ObjectFile(file.Path,file.Path=="web/index.html"?changed:unchanged)).ToArray();
        var next=first with {Sequence=2,Version="0.2.0-beta.2",Files=files,Differential=true};
        File.WriteAllText(old.Executable,"damaged version");
        using(var handler=new ObjectsHandler(new(){{files[0].Sha256,unchanged}}))
        using(var downloader=new Downloader(Path.Combine(updates.Root,"cache"),new LauncherSettings(),handler))
            await Assert.ThrowsAsync<IOException>(()=>updates.Prepare(Sign(next),downloader,currentDirectory:old.Directory));
        Assert.Empty(Directory.GetDirectories(updates.Root,"stage-*"));
        Assert.Equal("damaged version",File.ReadAllText(old.Executable));
        using var retryHandler=new ObjectsHandler(new(){{files[3].Sha256,changed}});
        using var retryDownloader=new Downloader(Path.Combine(updates.Root,"cache"),new LauncherSettings(),retryHandler);
        var prepared=await updates.Prepare(Sign(next),retryDownloader,currentDirectory:old.Directory);
        Assert.Equal([files[3].Sha256],retryHandler.Requests);
        Assert.Equal("version 1",File.ReadAllText(prepared.Executable));
    }
    [Fact]
    public async Task CancelledDifferentialUpdateKeepsPreviouslyReadyRelease()
    {
        var (first,zip)=Fixture();var old=await updates.PrepareArchive(Sign(first),zip);updates.Activate(old);
        var next=first with {Sequence=2,Version="0.2.0-beta.2",Differential=true};
        using var handler=new ObjectsHandler(new());using var downloader=new Downloader(Path.Combine(updates.Root,"cache"),new LauncherSettings(),handler);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>updates.Prepare(Sign(next),downloader,token:cancellation.Token,currentDirectory:old.Directory));
        Assert.Empty(handler.Requests);
        Assert.Equal(1,(await updates.Ready(Path.Combine(root,"installed"),"0.1.0"))!.Release.Sequence);
    }
    private sealed record LegacyRelease(long Sequence,string Version,string Platform,ContentFile Archive,ContentFile[] Files);
    [Fact]
    public async Task NewDifferentialReleaseRetainsTheLegacyZipUpgradePath()
    {
        var (release,zip)=Fixture();release=release with {Differential=true};
        var signed=Sign(release);
        var legacy=JsonSerializer.Deserialize<LegacyRelease>(Convert.FromBase64String(signed.Payload),Json.Options)!;
        Assert.Equal(release.Archive.Sha256,legacy.Archive.Sha256);
        var prepared=await updates.PrepareArchive(signed,zip);
        Assert.Equal("version 1",File.ReadAllText(prepared.Executable));
    }
    private sealed class MetadataHandler(SignedEnvelope envelope,HttpStatusCode? firstStatus=null):HttpMessageHandler
    {
        internal readonly List<Uri> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            if(Requests.Count==1)
            {
                if(firstStatus is null)throw NetworkPolicy.Failure(new HttpRequestException("test network failure"),"连接服务",request.RequestUri);
                if(firstStatus!=HttpStatusCode.OK)return Task.FromResult(new HttpResponseMessage(firstStatus.Value){RequestMessage=request});
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(envelope,Json.Options)),RequestMessage=request});
        }
    }
    [Theory]
    [InlineData(null)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task UpdateMetadataFailureNeverFallsBackToAnotherOrigin(HttpStatusCode? firstStatus)
    {
        var (release,_)=Fixture();var signed=Sign(release);
        using var handler=new MetadataHandler(signed,firstStatus);
        var error=await Assert.ThrowsAsync<NetworkFailure>(()=>updates.Fetch("https://launcher.boshan.uk",handler:handler));
        Assert.Equal(firstStatus is null?null:(int?)firstStatus,error.Diagnostic.HttpStatus);
        Assert.Equal(["launcher-direct.boshan.uk"],handler.Requests.Select(uri=>uri.Host));
    }
    [Fact]
    public async Task UpdateMetadataUsesTheSingleOriginEvenForAnOlderConfiguredAddress()
    {
        var (release,_)=Fixture();var signed=Sign(release);using var handler=new MetadataHandler(signed,HttpStatusCode.OK);
        Assert.Equal(signed,await updates.Fetch("https://launcher.boshan.uk",handler:handler));
        Assert.Equal(["launcher-direct.boshan.uk"],handler.Requests.Select(uri=>uri.Host));
    }
    [Fact]
    public async Task UpdateMetadataDoesNotMaskAnAuthorizationErrorWithAnotherRoute()
    {
        var (release,_)=Fixture();using var handler=new MetadataHandler(Sign(release),HttpStatusCode.Forbidden);
        var error=await Assert.ThrowsAsync<NetworkFailure>(()=>updates.Fetch("https://launcher.boshan.uk",handler:handler));
        Assert.Single(handler.Requests);Assert.Equal(403,error.Diagnostic.HttpStatus);Assert.Equal("检查启动器更新",error.Diagnostic.Stage);
    }
    [Theory]
    [InlineData("0.1.2-beta.2","0.1.2-beta.10",-1)]
    [InlineData("0.1.2-beta.4","0.1.2-beta.3",1)]
    [InlineData("0.1.2-beta.4+build1","0.1.2-beta.4+build2",0)]
    [InlineData("0.1.2","0.1.2-beta.10",1)]
    [InlineData("0.1.2-beta.10","0.1.2",-1)]
    [InlineData("0.1.2-1","0.1.2-alpha",-1)]
    [InlineData("0.1.2-beta","0.1.2-beta.1",-1)]
    [InlineData("0.1.2-beta.9999999999999999999999","0.1.2-beta.10",1)]
    [InlineData("0.2.0-beta.1","0.1.99",1)]
    public void VersionOrderingIncludesPrereleaseAndIgnoresBuildMetadata(string left,string right,int expected)
        =>Assert.Equal(expected,Math.Sign(LauncherVersion.Compare(left,right)));
    [Theory]
    [InlineData("0.1.2-beta..4")]
    [InlineData("0.1.2-beta.04")]
    [InlineData("0.01.2")]
    [InlineData("0.1.2.0")]
    [InlineData("0.1.2-")]
    [InlineData("0.1.2\n")]
    public void MetadataRejectsMalformedVersion(string version)
    {
        var (release,_)=Fixture();
        Assert.Throws<InvalidDataException>(()=>updates.AcceptMetadata(Sign(release with {Version=version})));
    }
    [Theory]
    [InlineData("0.1.2-beta.2","0.1.2-beta.10",true)]
    [InlineData("0.1.2-beta.4","0.1.2-beta.3",false)]
    [InlineData("0.1.2-beta.4+local","0.1.2-beta.4+published",false)]
    [InlineData("0.1.2-beta.4","0.1.2",true)]
    [InlineData("0.1.2","0.1.2-beta.10",false)]
    public async Task OnlyStrictlyNewerVersionsCanBeSelectedFromReadyOrActive(string current,string candidate,bool expected)
    {
        var (release,zip)=Fixture();var prepared=await updates.PrepareArchive(Sign(release with {Version=candidate}),zip);
        var installed=Path.Combine(root,"installed");
        Assert.Equal(expected,await updates.Ready(installed,current) is not null);
        updates.Activate(prepared);File.Delete(Path.Combine(updates.Root,"ready.signed.json"));
        Assert.Equal(expected,await updates.Ready(installed,current) is not null);
    }
    public void Dispose(){key.Dispose();Directory.Delete(root,true);}
}
