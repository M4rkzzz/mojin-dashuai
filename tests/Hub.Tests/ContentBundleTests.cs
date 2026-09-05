using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class ContentBundleTests
{
    private static ContentFile File(string path,byte[] bytes)=>new(path,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)),["https://download.example/"+path],FilePolicy.Seed,"test");
    private static byte[] Zip(string path,byte[] bytes)
    {
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))using(var entry=zip.CreateEntry(path).Open())entry.Write(bytes);
        return output.ToArray();
    }
    [Fact]
    public async Task FirstInstallExpandsBundleIntoVerifiedCacheWithoutIndividualRequests()
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-bundle-"+Guid.NewGuid().ToString("N"));
        var bytes=Encoding.UTF8.GetBytes("player default");var file=File("config/example.cfg",bytes);
        var archive=Zip("overrides/"+file.Path,bytes);var calls=0;
        try
        {
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(_=>{calls++;return archive;}));
            await downloader.PrimeBundle(new(File("pack.zip",archive),"overrides/"),new Dictionary<string,ContentFile>{{file.Path,file}},null,default);
            var path=await downloader.Get(file);
            Assert.Equal(bytes,System.IO.File.ReadAllBytes(path));Assert.Equal(1,calls);
        }
        finally{Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("overrides/../outside.cfg")]
    [InlineData("overrides/config/example.cfg")]
    public async Task UnsafePathsAndContentMismatchesNeverPopulateCache(string path)
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-bundle-"+Guid.NewGuid().ToString("N"));
        var file=File("config/example.cfg",Encoding.UTF8.GetBytes("expected"));var archive=Zip(path,Encoding.UTF8.GetBytes("tampered"));
        try
        {
            using var downloader=new Downloader(root,new LauncherSettings(),new Handler(_=>archive));
            await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.PrimeBundle(new(File("pack.zip",archive),"overrides/"),new Dictionary<string,ContentFile>{{file.Path,file}},null,default));
            Assert.False(System.IO.File.Exists(Path.Combine(root,file.Sha256.ToLowerInvariant())));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,byte[]> content):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(content(request))});}
}
