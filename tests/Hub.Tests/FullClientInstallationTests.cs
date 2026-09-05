using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class FullClientInstallationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-full-client-"+Guid.NewGuid().ToString("N"));
    private string Cache=>Path.Combine(root,"cache");
    private const string Origin="https://unified.example";
    private const string OfficialSource="https://cdn.modrinth.com/data/fixture/versions/v1/official.jar";
    private static readonly Lazy<byte[]> JavaExecutable=new(CreateJavaFixture);
    private sealed record Fixture(PackManifest Pack,Dictionary<string,byte[]> Contents,byte[] Archive);
    private sealed class Capture(Action<TransferProgress>? report=null):IProgress<TransferProgress>
    {
        public ConcurrentQueue<TransferProgress> Values {get;}=new();
        public void Report(TransferProgress value){Values.Enqueue(value);report?.Invoke(value);}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests {get;}=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Assert.Null(request.Headers.Authorization);Assert.False(request.Headers.Contains("Cookie"));
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);return Task.FromResult(respond(request));
        }
    }
    private static HttpResponseMessage Full(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private static ContentFile Record(string path,byte[] bytes,FilePolicy policy=FilePolicy.Managed)
    {
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(path,bytes.Length,hash,["https://source.example/objects/sha256/"+hash],policy,"fixture redistributable");
    }
    private static string ObjectUrl(ContentFile file)=>Origin+"/objects/sha256/"+file.Sha256.ToLowerInvariant();
    private static byte[] Zip(IEnumerable<KeyValuePair<string,byte[]>> contents)
    {
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var (path,bytes) in contents){using var entry=zip.CreateEntry(path,CompressionLevel.NoCompression).Open();entry.Write(bytes);}
        return output.ToArray();
    }
    private static Fixture CreateFixture(bool official=false)
    {
        var contents=new Dictionary<string,byte[]>
        {
            ["mods/a.jar"]=Encoding.UTF8.GetBytes("managed mod"),
            ["config/player.cfg"]=Encoding.UTF8.GetBytes("default config"),
            ["options.txt"]=Encoding.UTF8.GetBytes("default options")
        };
        var files=contents.Select(pair=>Record(pair.Key,pair.Value,pair.Key=="options.txt"?FilePolicy.Preserve:pair.Key.StartsWith("config/")?FilePolicy.Seed:FilePolicy.Managed)).ToList();
        if(official)
        {
            var bytes=Encoding.UTF8.GetBytes("official distribution");contents.Add("mods/official.jar",bytes);
            files.Add(Record("mods/official.jar",bytes) with {OfficialOnly=true,Sources=[OfficialSource],DistributionBasis="fixture official download only"});
        }
        var java=JavaExecutable.Value;
        var runtime=Zip(new Dictionary<string,byte[]>{{"bin/java.exe",java}});
        contents.Add(ContentBundle.RuntimeArchivePath,runtime);
        var archive=Zip(contents.Where(pair=>!files.Any(file=>file.Path==pair.Key&&file.OfficialOnly)));
        var pack=new PackManifest("dc2","fixture-1",1,"1.20.1","forge","47","fixture",new("java17",17,"17.0.1","windows-x64",Record("java17.zip",runtime),"bin/java.exe",java.Length),8192,"fixture",files.ToArray(),["fixture"],[new(Record("client.zip",archive),"",true)]);
        return new(pack,contents,archive);
    }
    private static Fixture Repack(Fixture fixture,IEnumerable<KeyValuePair<string,byte[]>> entries)
    {
        var archive=Zip(entries);
        return fixture with {Archive=archive,Pack=fixture.Pack with {Bundles=[new(Record("client.zip",archive),"",true)]}};
    }
    private static Handler BundleOnly(Fixture fixture)=>new(request=>
    {
        Assert.Equal(ObjectUrl(fixture.Pack.Bundles!.Single().Archive),request.RequestUri!.AbsoluteUri);
        return Full(fixture.Archive);
    });
    private async Task AssertCache(Fixture fixture)
    {
        foreach(var file in fixture.Pack.Files.Append(fixture.Pack.Runtime.Archive))
            Assert.True(await ContentSecurity.Matches(Path.Combine(Cache,file.Sha256),file));
    }

    [Fact]
    public void MissingOptionalFlagsRemainBackwardCompatibleAndCompleteSchemaIsUnambiguous()
    {
        var fixture=CreateFixture();var file=fixture.Pack.Files[0];
        var oldFile=JsonSerializer.SerializeToNode(file,Json.Options)!.AsObject();oldFile.Remove("officialOnly");
        Assert.False(oldFile.Deserialize<ContentFile>(Json.Options)!.OfficialOnly);
        var oldBundle=new JsonObject{["archive"]=oldFile.DeepClone(),["prefix"]="overrides/"};
        Assert.False(oldBundle.Deserialize<ContentBundle>(Json.Options)!.Complete);
        ContentSecurity.Validate(fixture.Pack);
        var bundle=fixture.Pack.Bundles!.Single();
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(fixture.Pack with {Bundles=[bundle with {Prefix="overrides/"}]}));
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(fixture.Pack with {Bundles=[bundle,bundle]}));
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(fixture.Pack with {Files=[file with {Path="__RUNTIME/unlisted.zip"}]}));
    }

    [Fact]
    public async Task FirstInstallUsesOneArchiveIncludesJavaAndPreservesExistingPlayerData()
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);var instance=installer.InstancePath("dc2");
        Directory.CreateDirectory(Path.Combine(instance,"config"));Directory.CreateDirectory(Path.Combine(instance,"saves","world"));
        File.WriteAllText(Path.Combine(instance,"config","player.cfg"),"player config");File.WriteAllText(Path.Combine(instance,"options.txt"),"player options");
        File.WriteAllText(Path.Combine(instance,"saves","world","level.dat"),"player save");
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);var progress=new Capture();
        var summary=await installer.Install(fixture.Pack,downloader,4,progress);
        Assert.Single(handler.Requests);Assert.True(summary.RuntimePrepared);Assert.Equal(1,summary.RestoredFiles);Assert.NotNull(installer.ReadInstalled("dc2"));
        Assert.Equal(fixture.Contents["mods/a.jar"],File.ReadAllBytes(Path.Combine(instance,"mods","a.jar")));
        Assert.Equal("player config",File.ReadAllText(Path.Combine(instance,"config","player.cfg")));
        Assert.Equal("player options",File.ReadAllText(Path.Combine(instance,"options.txt")));
        Assert.Equal("player save",File.ReadAllText(Path.Combine(instance,"saves","world","level.dat")));
        await AssertCache(fixture);
        Assert.DoesNotContain(progress.Values,value=>value.Phase.Contains("世界配置")||value.Phase=="正在下载世界内容"||value.Phase.StartsWith("下载 Java ",StringComparison.Ordinal));
        var downloads=progress.Values.Where(value=>value.Phase=="下载客户端").ToArray();
        Assert.All(downloads,value=>{Assert.Equal(fixture.Archive.Length,value.Total);Assert.InRange(value.Completed,0,value.Total);});
        Assert.Equal(downloads.Last().Total,downloads.Last().Completed);
        var extraction=progress.Values.Where(value=>value.Phase=="解压客户端").ToArray();
        Assert.Equal(0,extraction.First().Completed);Assert.Equal(fixture.Contents.Values.Sum(bytes=>(long)bytes.Length),extraction.Last().Total);
        Assert.Equal(extraction.Last().Total,extraction.Last().Completed);
        var again=new Capture();await installer.Install(fixture.Pack,downloader,4,again);
        Assert.Single(handler.Requests);Assert.DoesNotContain(again.Values,value=>value.Phase.Contains("下载"));
    }

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/player.cfg")]
    [InlineData("options.txt")]
    [InlineData(ContentBundle.RuntimeArchivePath)]
    public async Task IncompleteArchivesFailBeforeAnyIndividualRequestEvenForExistingPreservedFiles(string missing)
    {
        var fixture=CreateFixture();fixture=Repack(fixture,fixture.Contents.Where(pair=>pair.Key!=missing));
        var installer=new TransactionalInstaller(root);var instance=installer.InstancePath("dc2");Directory.CreateDirectory(Path.Combine(instance,"config"));
        File.WriteAllText(Path.Combine(instance,"config","player.cfg"),"player config");File.WriteAllText(Path.Combine(instance,"options.txt"),"player options");
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        var failure=await Assert.ThrowsAsync<InvalidDataException>(()=>installer.Install(fixture.Pack,downloader,4));
        Assert.Contains(missing,failure.Message);Assert.Single(handler.Requests);Assert.Null(installer.ReadInstalled("dc2"));
        Assert.All(fixture.Pack.Files,file=>Assert.False(File.Exists(Path.Combine(Cache,file.Sha256))));
        Assert.Equal("player options",File.ReadAllText(Path.Combine(instance,"options.txt")));
    }

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData(ContentBundle.RuntimeArchivePath)]
    public async Task ArchiveHashValidationCannotBeBypassedByAnAlreadyVerifiedCacheEntry(string corrupt)
    {
        var fixture=CreateFixture();var entries=new Dictionary<string,byte[]>(fixture.Contents);var tampered=entries[corrupt].ToArray();tampered[0]^=1;entries[corrupt]=tampered;
        fixture=Repack(fixture,entries);var file=corrupt==ContentBundle.RuntimeArchivePath?fixture.Pack.Runtime.Archive:fixture.Pack.Files.Single(file=>file.Path==corrupt);
        Directory.CreateDirectory(Cache);File.WriteAllBytes(Path.Combine(Cache,file.Sha256),fixture.Contents[corrupt]);
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        var installer=new TransactionalInstaller(root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>installer.Install(fixture.Pack,downloader,4));
        Assert.Single(handler.Requests);Assert.Null(installer.ReadInstalled("dc2"));Assert.True(await ContentSecurity.Matches(Path.Combine(Cache,file.Sha256),file));
        Assert.Empty(Directory.GetFiles(Cache,"*.extract-*"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("mods/A.jar")]
    [InlineData("saves/world.dat")]
    public async Task UnsafeDuplicateOrUnlistedEntriesNeverReachAnInstance(string extra)
    {
        var fixture=CreateFixture();fixture=Repack(fixture,fixture.Contents.Append(new(extra,[1,2,3])));
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);var installer=new TransactionalInstaller(root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>installer.Install(fixture.Pack,downloader,4));
        Assert.Single(handler.Requests);Assert.Null(installer.ReadInstalled("dc2"));Assert.False(Directory.Exists(Path.Combine(installer.InstancePath("dc2"),"mods")));
    }

    [Fact]
    public async Task LegacyOverrideBundlesAreIgnoredAndOldManifestsStillInstallAllFilesAndJava()
    {
        var fixture=CreateFixture();var pack=fixture.Pack with {Bundles=[fixture.Pack.Bundles!.Single() with {Complete=false,Prefix="overrides/"}]};
        var expected=pack.Files.ToDictionary(ObjectUrl,file=>fixture.Contents[file.Path]);expected.Add(ObjectUrl(pack.Runtime.Archive),fixture.Contents[ContentBundle.RuntimeArchivePath]);
        using var handler=new Handler(request=>Full(expected[request.RequestUri!.AbsoluteUri]));using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);var progress=new Capture();
        var installer=new TransactionalInstaller(root);await installer.Install(pack,downloader,4,progress);
        Assert.Equal(expected.Keys.Order(),handler.Requests.Order());Assert.NotNull(installer.ReadInstalled("dc2"));
        Assert.DoesNotContain(progress.Values,value=>value.Phase.Contains("世界配置"));
        Assert.Contains(progress.Values,value=>value.Phase=="下载 Java 17"&&value.Completed==value.Total);await AssertCache(fixture);
    }

    [Fact]
    public async Task UpdatesAndRepairsDownloadOnlyChangedFilesAndNeverFetchTheFullArchive()
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);
        using(var initial=BundleOnly(fixture))using(var downloader=new Downloader(Cache,new LauncherSettings(),initial,Origin))await installer.Install(fixture.Pack,downloader,4);
        var body=Encoding.UTF8.GetBytes("updated mod");var changed=Record("mods/a.jar",body);
        var next=fixture.Pack with {Version="fixture-2",Sequence=2,Files=fixture.Pack.Files.Select(file=>file.Path==changed.Path?changed:file).ToArray()};
        using var handler=new Handler(request=>{Assert.Equal(ObjectUrl(changed),request.RequestUri!.AbsoluteUri);return Full(body);});using var updater=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        await installer.Install(next,updater,4);Assert.Single(handler.Requests);
        var target=Path.Combine(installer.InstancePath("dc2"),"mods","a.jar");File.WriteAllText(target,"damaged");
        await installer.Install(next,updater,4);Assert.Single(handler.Requests);Assert.Equal(body,File.ReadAllBytes(target));
        File.Delete(target);File.Delete(Path.Combine(Cache,changed.Sha256));
        await installer.Install(next,updater,4);Assert.Equal(2,handler.Requests.Count);Assert.Equal(body,File.ReadAllBytes(target));
    }

    [Fact]
    public async Task OfficialExceptionsShareClientProgressUseOnlyTheirOfficialSourceAndStayDifferential()
    {
        var fixture=CreateFixture(true);var official=fixture.Pack.Files.Single(file=>file.OfficialOnly);var installer=new TransactionalInstaller(root);
        using var handler=new Handler(request=>request.RequestUri!.AbsoluteUri==OfficialSource?Full(fixture.Contents[official.Path]):Full(request.RequestUri.AbsoluteUri==ObjectUrl(fixture.Pack.Bundles!.Single().Archive)?fixture.Archive:throw new InvalidOperationException("Unexpected source.")));
        using(var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin))
        {
            var progress=new Capture();await installer.Install(fixture.Pack,downloader,4,progress);
            Assert.Equal(new[]{ObjectUrl(fixture.Pack.Bundles!.Single().Archive),OfficialSource},handler.Requests);
            var downloads=progress.Values.Where(value=>value.Phase=="下载客户端").ToArray();var total=fixture.Archive.Length+official.Size;
            Assert.All(downloads,value=>{Assert.Equal(total,value.Total);Assert.InRange(value.Completed,0,total);});
            Assert.Equal(total,downloads.Last().Completed);Assert.Equal(downloads.Select(value=>value.Completed).Order(),downloads.Select(value=>value.Completed));
            Assert.DoesNotContain(progress.Values,value=>value.Phase=="正在下载世界内容");await AssertCache(fixture);
            Assert.DoesNotContain(progress.Values.SkipWhile(value=>value.Phase!="解压客户端"),value=>value.Phase.StartsWith("下载",StringComparison.Ordinal));
        }
        var newer=Encoding.UTF8.GetBytes("new official version");var nextOfficial=Record(official.Path,newer) with {OfficialOnly=true,Sources=[OfficialSource.Replace("/v1/","/v2/")]};
        var next=fixture.Pack with {Sequence=2,Version="fixture-2",Files=fixture.Pack.Files.Select(file=>file.OfficialOnly?nextOfficial:file).ToArray()};
        using var updates=new Handler(request=>{Assert.Equal(nextOfficial.Sources.Single(),request.RequestUri!.AbsoluteUri);return Full(newer);});using var updater=new Downloader(Cache,new LauncherSettings(),updates,Origin);
        await installer.Install(next,updater,4);Assert.Single(updates.Requests);
        File.Delete(Path.Combine(installer.InstancePath("dc2"),"mods","official.jar"));File.Delete(Path.Combine(Cache,nextOfficial.Sha256));
        await installer.Install(next,updater,4);Assert.Equal(2,updates.Requests.Count);
    }

    [Fact]
    public async Task SneakingAnOfficialOnlyFileIntoTheArchiveIsRejectedBeforeContactingItsSource()
    {
        var fixture=CreateFixture(true);fixture=Repack(fixture,fixture.Contents);
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);var installer=new TransactionalInstaller(root);
        var failure=await Assert.ThrowsAsync<InvalidDataException>(()=>installer.Install(fixture.Pack,downloader,4));
        Assert.Contains("仅限官方",failure.Message);Assert.Single(handler.Requests);Assert.Null(installer.ReadInstalled("dc2"));
    }

    [Fact]
    public async Task OfficialOnlyBytesCannotBeIncludedUnderARedistributableAlias()
    {
        var fixture=CreateFixture(true);var official=fixture.Pack.Files.Single(file=>file.OfficialOnly);
        var alias=official with {Path="mods/renamed.jar",OfficialOnly=false};
        var files=fixture.Pack.Files.Append(alias).ToArray();
        Assert.Throws<InvalidDataException>(()=>ContentSecurity.Validate(fixture.Pack with {Files=files}));
        using var handler=new Handler(_=>throw new InvalidOperationException("Conflicting distribution flags must fail before networking."));using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.PrimeBundle(fixture.Pack.Bundles!.Single(),files.ToDictionary(file=>file.Path),null,default,runtimeArchive:fixture.Pack.Runtime.Archive));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task OfficialDownloadFailureNeverFallsBackAndRetryReusesTheCompleteArchive(HttpStatusCode failure)
    {
        var fixture=CreateFixture(true);var official=fixture.Pack.Files.Single(file=>file.OfficialOnly);var installer=new TransactionalInstaller(root);
        using(var failed=new Handler(request=>
        {
            if(request.RequestUri!.AbsoluteUri==ObjectUrl(fixture.Pack.Bundles!.Single().Archive))return Full(fixture.Archive);
            Assert.Equal(OfficialSource,request.RequestUri.AbsoluteUri);
            var response=new HttpResponseMessage(failure);response.Headers.Location=new Uri("https://unapproved.example/redirect.jar");return response;
        }))
        using(var downloader=new Downloader(Cache,new LauncherSettings(),failed,Origin))
        {
            await Assert.ThrowsAsync<IOException>(()=>installer.Install(fixture.Pack,downloader,4));Assert.Null(installer.ReadInstalled("dc2"));
            Assert.Equal(4,failed.Requests.Count);Assert.Equal(3,failed.Requests.Count(url=>url==OfficialSource));
        }
        using var resumed=new Handler(request=>{Assert.Equal(OfficialSource,request.RequestUri!.AbsoluteUri);return Full(fixture.Contents[official.Path]);});using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);
        await installer.Install(fixture.Pack,retry,4);Assert.Single(resumed.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));
    }

    [Fact]
    public async Task PausedFirstInstallResumesTheSameFullArchiveWithItsExistingPosition()
    {
        var fixture=CreateFixture();var bundle=fixture.Pack.Bundles!.Single();var installer=new TransactionalInstaller(root);using var cancellation=new CancellationTokenSource();
        using(var initial=new Handler(request=>new(HttpStatusCode.OK){Content=new StreamContent(new ChunkedStream(fixture.Archive)) {Headers={ContentLength=fixture.Archive.Length}}}))
        using(var downloader=new Downloader(Cache,new LauncherSettings(),initial,Origin))
        {
            var progress=new Capture(value=>{if(value.Phase=="下载客户端"&&value.Completed>=256)cancellation.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>installer.Install(fixture.Pack,downloader,4,progress,cancellation.Token));
            Assert.Single(initial.Requests);Assert.Null(installer.ReadInstalled("dc2"));
        }
        var part=Path.Combine(Cache,bundle.Archive.Sha256+".part");Assert.Equal(256,new FileInfo(part).Length);
        using var resumed=new Handler(request=>
        {
            Assert.Equal(ObjectUrl(bundle.Archive),request.RequestUri!.AbsoluteUri);Assert.Equal(256,request.Headers.Range!.Ranges.Single().From);
            var content=new ByteArrayContent(fixture.Archive[256..]);content.Headers.ContentRange=new ContentRangeHeaderValue(256,fixture.Archive.Length-1,fixture.Archive.Length);
            return new(HttpStatusCode.PartialContent){Content=content};
        });
        using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);var resumedProgress=new Capture();
        await installer.Install(fixture.Pack,retry,4,resumedProgress);
        Assert.Single(resumed.Requests);Assert.False(File.Exists(part));Assert.NotNull(installer.ReadInstalled("dc2"));
        Assert.Equal(256,resumedProgress.Values.First(value=>value.Phase=="下载客户端").Completed);await AssertCache(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADownloadedZipOrCompletedPartialInstallsAfterRestartWithNoNetwork(bool completedPartial)
    {
        var fixture=CreateFixture(true);var bundle=fixture.Pack.Bundles!.Single();var official=fixture.Pack.Files.Single(file=>file.OfficialOnly);
        Directory.CreateDirectory(Cache);File.WriteAllBytes(Path.Combine(Cache,bundle.Archive.Sha256+(completedPartial?".part":"")),fixture.Archive);
        File.WriteAllBytes(Path.Combine(Cache,official.Sha256),fixture.Contents[official.Path]);
        using var handler=new Handler(_=>throw new InvalidOperationException("Completed downloads must be reusable offline."));using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        var progress=new Capture();var installer=new TransactionalInstaller(root);await installer.Install(fixture.Pack,downloader,4,progress);
        Assert.Empty(handler.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));await AssertCache(fixture);
        Assert.False(File.Exists(Path.Combine(Cache,bundle.Archive.Sha256+".part")));
        Assert.DoesNotContain(progress.Values.SkipWhile(value=>value.Phase!="解压客户端"),value=>value.Phase.StartsWith("下载",StringComparison.Ordinal));
    }

    [Fact]
    public async Task PausingExtractionKeepsVerifiedEntriesCleansTheActiveEntryAndRestartsOffline()
    {
        var fixture=CreateFixture(true);var official=fixture.Pack.Files.Single(file=>file.OfficialOnly);var installer=new TransactionalInstaller(root);
        using var cancellation=new CancellationTokenSource();
        using(var handler=new Handler(request=>request.RequestUri!.AbsoluteUri==OfficialSource?Full(fixture.Contents[official.Path]):Full(fixture.Archive)))
        using(var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin))
        {
            var afterTwo=fixture.Pack.Files.Take(2).Sum(file=>file.Size);
            var progress=new Capture(value=>{if(value.Phase=="解压客户端"&&value.Completed>afterTwo)cancellation.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>installer.Install(fixture.Pack,downloader,4,progress,cancellation.Token));
            Assert.Equal(2,handler.Requests.Count);Assert.Null(installer.ReadInstalled("dc2"));
            Assert.True(await ContentSecurity.Matches(Path.Combine(Cache,fixture.Pack.Files[0].Sha256),fixture.Pack.Files[0]));
            Assert.True(await ContentSecurity.Matches(Path.Combine(Cache,official.Sha256),official));
            Assert.Empty(Directory.GetFiles(Cache,"*.extract-*"));
            Assert.False(File.Exists(Path.Combine(installer.InstancePath("dc2"),"mods","a.jar")));
        }
        using var resumed=new Handler(_=>throw new InvalidOperationException("Extraction resume must reuse its downloaded ZIP and official exceptions."));using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);
        await installer.Install(fixture.Pack,retry,4);Assert.Empty(resumed.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));await AssertCache(fixture);
    }

    [Fact]
    public async Task ZipImportsRequireFreshSpaceForPartialsAndRemoveThemOnlyAfterVerification()
    {
        var fixture=CreateFixture();var file=fixture.Pack.Files[0];Directory.CreateDirectory(Cache);
        var partial=Path.Combine(Cache,file.Sha256+".part");File.WriteAllBytes(partial,fixture.Contents[file.Path][..4]);
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        Assert.Equal(4,await downloader.Available(file));Assert.Equal(0,await downloader.Available(file,includePartial:false));
        await new TransactionalInstaller(root).Install(fixture.Pack,downloader,4);
        Assert.Single(handler.Requests);Assert.False(File.Exists(partial));Assert.Equal(file.Size,await downloader.Available(file,includePartial:false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CancellingPreparationNeverCommitsAndCanRetryFromTheVerifiedCache(int cancelAfter)
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);using var cancellation=new CancellationTokenSource();
        using(var handler=BundleOnly(fixture))using(var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin))
        {
            var progress=new Capture(value=>{if(value.Phase=="校验并准备更新"&&value.Completed==cancelAfter)cancellation.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>installer.Install(fixture.Pack,downloader,4,progress,cancellation.Token));
            Assert.Null(installer.ReadInstalled("dc2"));Assert.False(File.Exists(Path.Combine(installer.InstancePath("dc2"),"mods","a.jar")));
            Assert.False(File.Exists(Path.Combine(installer.InstancePath("dc2"),".hub","journal.json")));await AssertCache(fixture);
        }
        using var resumed=new Handler(_=>throw new InvalidOperationException("Preparation retry must not redownload."));using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);
        await installer.Install(fixture.Pack,retry,4);Assert.Empty(resumed.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));
    }

    [Fact]
    public async Task CancellationAfterCommitBeginsFinishesOneConsistentInstallation()
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);using var cancellation=new CancellationTokenSource();
        using var handler=BundleOnly(fixture);using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        var progress=new Capture(value=>{if(value.Phase=="应用更新"&&value.Completed==1)cancellation.Cancel();});
        await installer.Install(fixture.Pack,downloader,4,progress,cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);Assert.NotNull(installer.ReadInstalled("dc2"));
        foreach(var file in fixture.Pack.Files)Assert.True(await ContentSecurity.Matches(ContentSecurity.SafePath(installer.InstancePath("dc2"),file.Path),file));
        Assert.False(File.Exists(Path.Combine(installer.InstancePath("dc2"),".hub","journal.json")));
    }

    [Fact]
    public async Task ACommitFailureRollsBackOnlyNewClientFilesAndCanRetryOffline()
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);var instance=installer.InstancePath("dc2");
        Directory.CreateDirectory(Path.Combine(instance,"saves"));File.WriteAllText(Path.Combine(instance,"saves","keep.dat"),"player save");
        using(var handler=BundleOnly(fixture))using(var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin))
        {
            var progress=new Capture(value=>{if(value.Phase=="应用更新"&&value.Completed==1)throw new IOException("simulated commit interruption");});
            await Assert.ThrowsAsync<IOException>(()=>installer.Install(fixture.Pack,downloader,4,progress));
        }
        Assert.Null(installer.ReadInstalled("dc2"));Assert.All(fixture.Pack.Files,file=>Assert.False(File.Exists(ContentSecurity.SafePath(instance,file.Path))));
        Assert.Equal("player save",File.ReadAllText(Path.Combine(instance,"saves","keep.dat")));
        Assert.False(File.Exists(Path.Combine(instance,".hub","journal.json")));
        using var resumed=new Handler(_=>throw new InvalidOperationException("Rollback retry must reuse verified cache objects."));using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);
        await installer.Install(fixture.Pack,retry,4);Assert.Empty(resumed.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));
    }

    [Fact]
    public async Task SpacePreflightRejectsAnUnpreparedOversizedRuntimeWithoutStartingDownloads()
    {
        var fixture=CreateFixture();var free=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        var pack=fixture.Pack with {Runtime=fixture.Pack.Runtime with {ExpandedSize=free+1024*1024*1024}};
        using var handler=new Handler(_=>throw new InvalidOperationException("Insufficient disk space must be detected before downloading."));using var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin);
        var installer=new TransactionalInstaller(root);var error=await Assert.ThrowsAsync<IOException>(()=>installer.Install(pack,downloader,4));
        Assert.Contains("磁盘空间不足",error.Message);Assert.Empty(handler.Requests);Assert.Null(installer.ReadInstalled("dc2"));
    }

    [Fact]
    public async Task SpacePreflightDoesNotReserveAnAlreadyPreparedRuntimeAgainAfterRestart()
    {
        var fixture=CreateFixture();var installer=new TransactionalInstaller(root);
        using(var handler=BundleOnly(fixture))using(var downloader=new Downloader(Cache,new LauncherSettings(),handler,Origin))await installer.Install(fixture.Pack,downloader,4);
        // Simulate a fresh ledger after cancellation: the immutable runtime and cache already
        // occupy disk, so their declared sizes must not be required as additional free space.
        File.Delete(Path.Combine(installer.InstancePath("dc2"),".hub","installed.json"));
        var free=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        var pack=fixture.Pack with {Runtime=fixture.Pack.Runtime with {ExpandedSize=free+1024*1024*1024}};
        using var resumed=new Handler(_=>throw new InvalidOperationException("Existing runtime and cache must be reusable offline."));using var retry=new Downloader(Cache,new LauncherSettings(),resumed,Origin);
        var summary=await installer.Install(pack,retry,4);Assert.False(summary.RuntimePrepared);Assert.Empty(resumed.Requests);Assert.NotNull(installer.ReadInstalled("dc2"));
    }
    private sealed class ChunkedStream(byte[] bytes):MemoryStream(bytes,false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)=>base.ReadAsync(buffer[..Math.Min(buffer.Length,256)],cancellationToken);
    }

    private static byte[] CreateJavaFixture()
    {
        // The launcher targets Windows. Compile a tiny local fixture that only prints a Java
        // version, so installation tests exercise runtime validation without an installed JDK.
        var directory=Path.Combine(Path.GetTempPath(),"mojin-java-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var compiler=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Microsoft.NET","Framework64","v4.0.30319","csc.exe");
            var source=Path.Combine(directory,"JavaFixture.cs");var executable=Path.Combine(directory,"java.exe");
            File.WriteAllText(source,"class JavaFixture { static int Main(string[] args) { System.Console.Error.WriteLine(\"openjdk version \\\"17.0.1\\\"\"); System.Console.Error.WriteLine(\"OpenJDK 64-Bit Server VM\"); return args.Length == 1 && args[0] == \"-version\" ? 0 : 1; } }");
            using var process=new Process {StartInfo=new(compiler){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
            foreach(var argument in new[]{"/nologo","/target:exe","/platform:x64","/out:"+executable,source})process.StartInfo.ArgumentList.Add(argument);
            process.Start();var stderr=process.StandardError.ReadToEnd();var stdout=process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(15000),"The hidden Java fixture compiler timed out.");Assert.True(process.ExitCode==0,stderr+stdout);
            return File.ReadAllBytes(executable);
        }
        finally{DeleteFixtureDirectory(directory,"mojin-java-fixture-");}
    }
    private static void DeleteFixtureDirectory(string directory,string prefix)
    {
        var absolute=Path.GetFullPath(directory);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!absolute.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith(prefix,StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fixture directory.");
        if(Directory.Exists(absolute))Directory.Delete(absolute,true);
    }
    public void Dispose()=>DeleteFixtureDirectory(root,"mojin-full-client-");
}
