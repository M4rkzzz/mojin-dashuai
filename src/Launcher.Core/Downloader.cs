using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Boshan.Launcher;

public sealed class Downloader : IDisposable
{
    public async Task<IReadOnlyDictionary<string,string>> PrimeBundle(ContentBundle bundle,IReadOnlyDictionary<string,ContentFile> files,Action<long>? onBytes,CancellationToken token,Action<long>? onPosition=null,Action? onExtract=null,ContentFile? runtimeArchive=null,Action<long,long>? onExtractionProgress=null,Func<CancellationToken,Task>? beforeExtract=null)
    {
        foreach(var (path,file) in files){ContentSecurity.ValidateRelativePath(path);ContentSecurity.ValidateFile(file);}
        var expected=files.Where(pair=>!bundle.Complete||!pair.Value.OfficialOnly).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.OrdinalIgnoreCase);
        if(bundle.Complete)
        {
            if(bundle.Prefix.Length!=0||runtimeArchive is null||runtimeArchive.OfficialOnly)throw new InvalidDataException("完整客户端包缺少可随包分发的运行环境或使用了目录前缀。");
            if(expected.Keys.Any(path=>path.Split('/')[0].Equals("__runtime",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("客户端文件占用了运行环境目录。");
            expected.Add(ContentBundle.RuntimeArchivePath,runtimeArchive);
            var officialHashes=files.Values.Where(file=>file.OfficialOnly).Select(file=>file.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(expected.Values.Any(file=>officialHashes.Contains(file.Sha256)))throw new InvalidDataException("完整客户端包不能通过文件别名包含仅限官方分发的内容。");
        }
        foreach(var (path,file) in expected){ContentSecurity.ValidateRelativePath(path);ContentSecurity.ValidateFile(file);}
        var archive=await Get(bundle.Archive,onBytes,token,onPosition).ConfigureAwait(false);
        // Cached downloads may complete synchronously. ZIP scanning, decompression and hashing
        // still belong on a worker, including when a caller has a UI synchronization context.
        return await Task.Run(async()=>
        {
            using var zip=ZipFile.OpenRead(archive);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if(!entry.FullName.StartsWith(bundle.Prefix,StringComparison.Ordinal))continue;
                var relative=entry.FullName[bundle.Prefix.Length..];
                if(relative.Length==0&&entry.FullName.EndsWith('/'))continue;
                var directory=relative.EndsWith('/');
                ContentSecurity.ValidateRelativePath(directory?relative[..^1]:relative);
                if(!seen.Add(directory?relative[..^1]:relative)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("内容包包含重复路径或链接。");
                if(directory)continue;
                if(!expected.TryGetValue(relative,out var file))
                {
                    if(bundle.Complete&&files.TryGetValue(relative,out var excluded)&&excluded.OfficialOnly)throw new InvalidDataException($"完整客户端包不能包含仅限官方分发的文件：{relative}");
                    if(bundle.Complete)throw new InvalidDataException($"完整客户端包包含清单之外的文件：{relative}");
                    continue;
                }
                if(entry.Length!=file.Size)throw new InvalidDataException($"内容包中的文件大小不符：{relative}");
                entries.Add(relative,entry);
            }
            // Check every required path before publishing any object, even if it is already
            // cached or a player's seed/preserve file does not need replacing.
            if(bundle.Complete)
                foreach(var path in expected.Keys)
                    if(!entries.ContainsKey(path))throw new InvalidDataException($"完整客户端包缺少必需文件：{path}");
            // Finish explicit official-source exceptions after structural preflight and before
            // extraction, keeping client download and extraction progress in a single order.
            if(beforeExtract is not null)await beforeExtract(token);
            token.ThrowIfCancellationRequested();
            onExtract?.Invoke();
            var expanded=entries.Keys.Sum(path=>expected[path].Size);long extracted=0;
            onExtractionProgress?.Invoke(0,expanded);
            var imported=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var (relative,entry) in entries)
            {
                token.ThrowIfCancellationRequested();
                var file=expected[relative];var target=ContentSecurity.SafePath(cache,file.Sha256.ToLowerInvariant());
                var gate=locks.GetOrAdd(Path.GetFullPath(target),_=>new SemaphoreSlim(1));
                await gate.WaitAsync(token);
                try
                {
                    var cached=await ContentSecurity.Matches(target,file,token);
                    var temp=target+".extract-"+Guid.NewGuid().ToString("N");
                    try
                    {
                        // Validate the ZIP entry even on a cache hit: a complete archive must
                        // stand on its own and cannot conceal an invalid entry behind old cache data.
                        await using(var input=entry.Open())
                        await using(var output=cached?null:new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true))
                        using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            var buffer=new byte[131072];long written=0;int count;
                            while((count=await input.ReadAsync(buffer,token))!=0)
                            {
                                written+=count;
                                if(written>file.Size)throw new InvalidDataException($"内容包中的文件超出清单大小：{relative}");
                                hash.AppendData(buffer,0,count);
                                if(output is not null)await output.WriteAsync(buffer.AsMemory(0,count),token);
                                extracted+=count;onExtractionProgress?.Invoke(extracted,expanded);
                            }
                            if(written!=file.Size||!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException($"内容包中的文件校验失败：{relative}");
                            if(output is not null)await output.FlushAsync(token);
                        }
                        token.ThrowIfCancellationRequested();
                        if(!cached)File.Move(temp,target,true);
                        if(File.Exists(target+".part"))File.Delete(target+".part");
                        imported.Add(relative,target);
                    }
                    finally{if(File.Exists(temp))File.Delete(temp);}
                }
                finally{gate.Release();}
            }
            onExtractionProgress?.Invoke(expanded,expanded);
            token.ThrowIfCancellationRequested();
            return (IReadOnlyDictionary<string,string>)imported;
        },token).ConfigureAwait(false);
    }
    private readonly HttpClient client;
    private readonly HttpClient officialClient;
    private readonly string cache;
    private readonly string? origin;
    private readonly long limit;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long transferred;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);
    public Downloader(string cache, LauncherSettings settings, HttpMessageHandler? handler = null,string? origin=null)
    {
        if(origin is not null&&(!Uri.TryCreate(origin,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||endpoint.UserInfo.Length!=0||endpoint.Query.Length!=0||endpoint.AbsolutePath!="/"))throw new InvalidDataException("统一下载地址无效。");
        this.origin=origin?.TrimEnd('/');
        this.cache = cache; Directory.CreateDirectory(cache); limit = (long)settings.LimitMiB * 1024 * 1024;
        officialClient = new HttpClient(handler??NetworkPolicy.Handler(settings,allowRedirect:false),disposeHandler:handler is null) { Timeout = TimeSpan.FromSeconds(20) };
        client = new HttpClient(handler??NetworkPolicy.Handler(settings)) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BoshanLauncher/0.1");
        officialClient.DefaultRequestHeaders.UserAgent.ParseAdd("BoshanLauncher/0.1");
    }
    public async Task<string> Get(ContentFile file, Action<long>? onBytes = null, CancellationToken token = default,Action<long>? onPosition=null)
    {
        ContentSecurity.ValidateFile(file);
        var gate = locks.GetOrAdd(Path.GetFullPath(Path.Combine(cache,file.Sha256.ToLowerInvariant())), _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            var target = ContentSecurity.SafePath(cache, file.Sha256.ToLowerInvariant());
            if (await ContentSecurity.Matches(target, file, token)) {onPosition?.Invoke(file.Size);return target;}
            var part = target + ".part";
            Exception? last = null;
            var sources=origin is null||file.OfficialOnly?file.Sources:[origin+"/objects/sha256/"+file.Sha256.ToLowerInvariant()];
            for (var attempt = 0; attempt < 3; attempt++) foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (offset == file.Size && await ContentSecurity.Matches(part, file, token)) { File.Move(part, target, true);onPosition?.Invoke(file.Size); return target; }
                    if (offset >= file.Size) { File.Delete(part); offset = 0; }
                    onPosition?.Invoke(offset);
                    using var request = new HttpRequestMessage(HttpMethod.Get, source);
                    // This dedicated client has no authorization header, credentials or cookie container.
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await (file.OfficialOnly?officialClient:client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); throw new IOException("下载源不支持当前续传位置。"); }
                    NetworkPolicy.EnsureSuccess(response,"下载文件");
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != file.Size) throw new IOException("下载源返回了错误的续传范围。");
                    }
                    else {offset = 0;onPosition?.Invoke(0);}
                    if (response.Content.Headers.ContentLength is long length && length != file.Size - offset) throw new IOException("下载文件大小与清单不符。");
                    await using (var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    {
                        var buffer = new byte[131072]; var written = offset; int count;
                        using var idle=CancellationTokenSource.CreateLinkedTokenSource(token);
                        while (true)
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(30));
                            count=await input.ReadAsync(buffer,idle.Token);
                            idle.CancelAfter(Timeout.InfiniteTimeSpan);
                            if(count==0)break;
                            written += count; if (written > file.Size) throw new IOException("下载源超出清单大小。");
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                            var overall = Interlocked.Add(ref transferred, count); onBytes?.Invoke(count);
                            onPosition?.Invoke(written);
                            if (limit > 0) { var delay = overall * 1000.0 / limit - clock.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token); }
                        }
                        await output.FlushAsync(token);
                    }
                    if(new FileInfo(part).Length<file.Size)throw new IOException("下载连接提前结束，继续下载剩余内容。");
                    if (!await ContentSecurity.Matches(part, file, token)) { File.Delete(part);onPosition?.Invoke(0); throw new IOException("下载校验失败，正在重新下载此文件。"); }
                    File.Move(part, target, true); return target;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException || NetworkPolicy.IsNetwork(ex)) { last = NetworkPolicy.IsNetwork(ex)||ex is OperationCanceledException?NetworkPolicy.Failure(ex,"下载文件",new Uri(source),file.Path,attempt+1):ex; }
            }
            throw new IOException("文件下载失败。可重试，已下载的内容会保留。", last);
        }
        finally { gate.Release(); }
    }
    public async Task<long> Available(ContentFile file,CancellationToken token=default,bool includePartial=true)
    {
        var path=ContentSecurity.SafePath(cache,file.Sha256.ToLowerInvariant());
        async Task<bool> Published()
        {
            try{return await ContentSecurity.Matches(path,file,token);}
            catch(Exception ex)when(ex is FileNotFoundException or DirectoryNotFoundException){return false;}
        }
        if(await Published())return file.Size;
        if(!includePartial)return 0;
        // FileInfo caches one metadata snapshot. A separate File.Exists followed by
        // a new FileInfo can lose the .part file to another instance's final move.
        // Do not wait for its download lock merely to report available bytes.
        var partial=new FileInfo(path+".part");
        try{if(partial.Exists)return Math.Min(file.Size,partial.Length);}
        catch(Exception ex)when(ex is FileNotFoundException or DirectoryNotFoundException){ }
        return await Published()?file.Size:0;
    }
    public void Dispose() { officialClient.Dispose();client.Dispose(); }
}
