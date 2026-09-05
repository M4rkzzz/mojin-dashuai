using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO.Compression;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class DownloadContinuityTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-download-continuity-"+Guid.NewGuid().ToString("N"));
    private readonly byte[] bytes=Enumerable.Range(0,32768).Select(index=>(byte)(index%251)).ToArray();
    private ContentFile FileRecord=>new("mods/sample.jar",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),["https://download.example/sample.jar"],FilePolicy.Managed,"download fixture");
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>respond(request,token);
    }
    private static HttpResponseMessage Full(byte[] content)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(content)};
    private sealed class UnknownLengthContent(byte[] content):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(content).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new MemoryStream(content,false));
    }
    private sealed class PrefixThenStallStream(byte[] prefix):Stream
    {
        private int position;
        public TaskCompletionSource Blocked {get;}=Signal();
        public bool Disposed {get;private set;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(position<prefix.Length)
            {
                var count=Math.Min(buffer.Length,prefix.Length-position);
                prefix.AsMemory(position,count).CopyTo(buffer);position+=count;return count;
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan,cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position {get=>position;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush()=>throw new NotSupportedException();
    }
    [Fact]
    public async Task ConcurrentDownloaderInstancesShareOneTransferForTheSameCacheObject()
    {
        var entered=Signal();var release=Signal();var requests=0;
        async Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref requests);entered.TrySetResult();
            await release.Task.WaitAsync(token);return Full(bytes);
        }
        using var first=new Downloader(root,new LauncherSettings(),new Handler(Respond));
        // A differently formatted path still identifies the same shared cache.
        using var second=new Downloader(Path.Combine(root,"."),new LauncherSettings(),new Handler(Respond));
        var firstDownload=first.Get(FileRecord);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondDownload=second.Get(FileRecord);
        try{Assert.Equal(1,Volatile.Read(ref requests));}
        finally{release.TrySetResult();}
        var completed=await Task.WhenAll(firstDownload,secondDownload).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,requests);Assert.Equal(Path.GetFullPath(completed[0]),Path.GetFullPath(completed[1]));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(completed[0]));
    }
    [Theory]
    [InlineData(true,false)]
    [InlineData(false,false)]
    [InlineData(true,true)]
    [InlineData(false,true)]
    public async Task NewDownloaderResumesCancelledPartialOrSafelyRestartsWhenRangeIsIgnored(bool supportsRange,bool legacyOfficialOnly)
    {
        const int prefixLength=4096;
        var file=FileRecord;var firstRequests=0;
        if(legacyOfficialOnly)file=file with {OfficialOnly=true,Sources=["https://cdn.modrinth.com/data/project/versions/version/mod.jar"]};
        var origin=legacyOfficialOnly?"https://unified.example":null;
        var expectedUrl=origin is null?file.Sources.Single():origin+"/objects/sha256/"+file.Sha256;
        using var cancellation=new CancellationTokenSource();
        using(var first=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>
        {
            Interlocked.Increment(ref firstRequests);Assert.Null(request.Headers.Range);Assert.Equal(expectedUrl,request.RequestUri!.AbsoluteUri);
            var content=new StreamContent(new PrefixThenStallStream(bytes[..prefixLength]));
            content.Headers.ContentLength=bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
        }),origin))
        {
            var transfer=first.Get(file,_=>cancellation.Cancel(),cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await transfer.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        var partial=Path.Combine(root,file.Sha256+".part");
        Assert.Equal(1,firstRequests);Assert.Equal(bytes[..prefixLength],await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(Path.Combine(root,file.Sha256)));
        var resumedRequests=0;long received=0;var positions=new List<long>();
        using var resumed=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>
        {
            Interlocked.Increment(ref resumedRequests);
            Assert.Equal(expectedUrl,request.RequestUri!.AbsoluteUri);
            Assert.Equal(prefixLength,request.Headers.Range!.Ranges.Single().From);
            if(!supportsRange)return Task.FromResult(Full(bytes));
            var content=new ByteArrayContent(bytes[prefixLength..]);
            content.Headers.ContentRange=new ContentRangeHeaderValue(prefixLength,bytes.Length-1,bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent){Content=content});
        }),origin);
        Assert.Equal(prefixLength,await resumed.Available(file));
        var completed=await resumed.Get(file,n=>received+=n,onPosition:positions.Add).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,resumedRequests);Assert.Equal(supportsRange?bytes.Length-prefixLength:bytes.Length,received);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(completed));Assert.False(File.Exists(partial));
        Assert.Equal(prefixLength,positions.First());Assert.Equal(file.Size,positions.Last());
        if(supportsRange)Assert.DoesNotContain(0,positions);else Assert.Contains(0,positions);
        Assert.Equal(file.Size,await resumed.Available(file));
    }
    [Fact]
    public async Task CancellingAStalledResponseDisposesItAndReleasesTheCacheLock()
    {
        var stalled=new PrefixThenStallStream([]);var firstRequests=0;
        using var cancellation=new CancellationTokenSource();
        using(var first=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>
        {
            Interlocked.Increment(ref firstRequests);
            var content=new StreamContent(stalled);content.Headers.ContentLength=bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
        })))
        {
            var transfer=first.Get(FileRecord,token:cancellation.Token);
            await stalled.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await transfer.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.True(stalled.Disposed);Assert.Equal(1,firstRequests);
        using var resumed=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>Task.FromResult(Full(bytes))));
        var completed=await resumed.Get(FileRecord).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(completed));
    }
    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelAnotherInstancesActiveDownload()
    {
        var entered=Signal();var release=Signal();var requests=0;
        async Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref requests);entered.TrySetResult();
            await release.Task.WaitAsync(token);return Full(bytes);
        }
        using var first=new Downloader(root,new LauncherSettings(),new Handler(Respond));
        using var second=new Downloader(root,new LauncherSettings(),new Handler(Respond));
        using var cancellation=new CancellationTokenSource();
        var active=first.Get(FileRecord);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting=second.Get(FileRecord,token:cancellation.Token);cancellation.Cancel();
        try{await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await waiting.WaitAsync(TimeSpan.FromSeconds(3)));}
        finally{release.TrySetResult();}
        var completed=await active.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,requests);Assert.Equal(bytes,await File.ReadAllBytesAsync(completed));
    }
    [Fact]
    public async Task ShortUnknownLengthResponsePreservesItsPrefixForTheNextRangeRequest()
    {
        const int prefixLength=4096;var requests=0;
        using var downloader=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>
        {
            if(Interlocked.Increment(ref requests)==1)
            {
                Assert.Null(request.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new UnknownLengthContent(bytes[..prefixLength])});
            }
            Assert.Equal(prefixLength,request.Headers.Range!.Ranges.Single().From);
            var content=new ByteArrayContent(bytes[prefixLength..]);
            content.Headers.ContentRange=new ContentRangeHeaderValue(prefixLength,bytes.Length-1,bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent){Content=content});
        }));
        long received=0;
        var completed=await downloader.Get(FileRecord,n=>received+=n).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2,requests);Assert.Equal(bytes.Length,received);Assert.Equal(bytes,await File.ReadAllBytesAsync(completed));
    }
    [Fact]
    public async Task BundleExtractionWaitsForTheSameObjectAlreadyBeingDownloaded()
    {
        Directory.CreateDirectory(root);
        using var zipBytes=new MemoryStream();
        using(var zip=new ZipArchive(zipBytes,ZipArchiveMode.Create,true))
        {using var entry=zip.CreateEntry(FileRecord.Path).Open();entry.Write(bytes);}
        var archiveBytes=zipBytes.ToArray();var hash=Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var archive=new ContentFile("bundle.zip",archiveBytes.Length,hash,["https://download.example/bundle.zip"],FilePolicy.Managed,"bundle fixture");
        await File.WriteAllBytesAsync(Path.Combine(root,hash),archiveBytes);
        var entered=Signal();var release=Signal();var requests=0;
        using var downloader=new Downloader(root,new LauncherSettings(),new Handler(async(request,token)=>
        {
            Interlocked.Increment(ref requests);entered.TrySetResult();
            await release.Task.WaitAsync(token);return Full(bytes);
        }));
        using var extractor=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>throw new InvalidOperationException("The verified archive should already be cached.")));
        var transfer=downloader.Get(FileRecord);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var extraction=extractor.PrimeBundle(new(archive,""),new Dictionary<string,ContentFile>{{FileRecord.Path,FileRecord}},null,CancellationToken.None);
        try
        {
            var finished=await Task.WhenAny(extraction,Task.Delay(150));
            Assert.NotSame(extraction,finished);
            Assert.False(File.Exists(Path.Combine(root,FileRecord.Sha256)));
        }
        finally{release.TrySetResult();}
        await Task.WhenAll(transfer,extraction).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,requests);Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(root,FileRecord.Sha256)));
    }
    [Fact]
    public async Task CachedFilesReportTheirFullPositionWithoutAnotherRequestAfterReopening()
    {
        Directory.CreateDirectory(root);await File.WriteAllBytesAsync(Path.Combine(root,FileRecord.Sha256),bytes);
        var positions=new List<long>();long transferred=0;
        using var downloader=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>throw new InvalidOperationException("A verified cached file must not download again.")));
        Assert.Equal(bytes.Length,await downloader.Available(FileRecord));
        var completed=await downloader.Get(FileRecord,n=>transferred+=n,onPosition:positions.Add);
        Assert.Equal(0,transferred);Assert.Equal([(long)bytes.Length],positions);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(completed));
    }
    [Fact]
    public async Task AvailabilityDoesNotWaitForAnotherDownloadAndRecognizesItsCompletedMove()
    {
        var entered=Signal();var release=Signal();
        using var active=new Downloader(root,new LauncherSettings(),new Handler(async(request,token)=>
        {
            entered.TrySetResult();await release.Task.WaitAsync(token);return Full(bytes);
        }));
        using var observer=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>throw new InvalidOperationException("Availability must not download.")));
        var transfer=active.Get(FileRecord);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try{Assert.Equal(0,await observer.Available(FileRecord).WaitAsync(TimeSpan.FromSeconds(1)));}
        finally{release.TrySetResult();}
        await transfer.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(Path.Combine(root,FileRecord.Sha256+".part")));
        Assert.Equal(FileRecord.Size,await observer.Available(FileRecord));
    }
    [Fact]
    public async Task AvailabilityToleratesBriefSharingViolationsWithoutTrustingUnverifiedBytes()
    {
        if(!OperatingSystem.IsWindows())return;
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,FileRecord.Sha256);
        await File.WriteAllBytesAsync(path,bytes);
        using var observer=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>throw new InvalidOperationException("Availability must not download.")));
        using(var publishing=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.Equal(0,await observer.Available(FileRecord).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(FileRecord.Size,await observer.Available(FileRecord));
    }
    [Fact]
    public async Task AvailabilityCanBeReadWhilePartialObjectsAreAtomicallyPublished()
    {
        Directory.CreateDirectory(root);
        var files=Enumerable.Range(0,256).Select(index=>
        {
            var body=BitConverter.GetBytes(index);var hash=Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var record=new ContentFile("mods/"+index+".jar",body.Length,hash,["https://download.example/"+index],FilePolicy.Managed,"concurrent publication fixture");
            File.WriteAllBytes(Path.Combine(root,hash+".part"),body);return record;
        }).ToArray();
        using var observer=new Downloader(root,new LauncherSettings(),new Handler((request,token)=>throw new InvalidOperationException("Availability must not download.")));
        var start=Signal();
        var publisher=Task.Run(async()=>
        {
            await start.Task;
            foreach(var file in files){File.Move(Path.Combine(root,file.Sha256+".part"),Path.Combine(root,file.Sha256));await Task.Yield();}
        });
        var readers=Enumerable.Range(0,4).Select(_=>Task.Run(async()=>
        {
            await start.Task;
            foreach(var file in files)Assert.InRange(await observer.Available(file),0,file.Size);
        })).ToArray();
        start.TrySetResult();await Task.WhenAll(readers.Append(publisher)).WaitAsync(TimeSpan.FromSeconds(5));
        foreach(var file in files)Assert.Equal(file.Size,await observer.Available(file));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
