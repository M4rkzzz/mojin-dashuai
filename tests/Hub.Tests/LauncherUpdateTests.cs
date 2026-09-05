using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
    public void Dispose(){key.Dispose();Directory.Delete(root,true);}
}
