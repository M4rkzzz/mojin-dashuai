using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;

namespace Boshan.Launcher;

public sealed class Downloader : IDisposable
{
    public async Task PrimeBundle(ContentBundle bundle,IReadOnlyDictionary<string,ContentFile> files,Action<long>? onBytes,CancellationToken token)
    {
        var archive=await Get(bundle.Archive,onBytes,token);
        using var zip=ZipFile.OpenRead(archive);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if(entry.FullName.EndsWith('/')||!entry.FullName.StartsWith(bundle.Prefix,StringComparison.Ordinal))continue;
            var relative=entry.FullName[bundle.Prefix.Length..];ContentSecurity.ValidateRelativePath(relative);
            if(!seen.Add(relative)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("内容包包含重复路径或链接。");
            if(!files.TryGetValue(relative,out var file))continue;
            if(entry.Length!=file.Size)throw new InvalidDataException("内容包中的文件大小不符。");
            var target=ContentSecurity.SafePath(cache,file.Sha256.ToLowerInvariant());
            if(await ContentSecurity.Matches(target,file,token))continue;
            var temp=target+".extract-"+Guid.NewGuid().ToString("N");
            try
            {
                await using(var input=entry.Open())await using(var output=File.Create(temp))await input.CopyToAsync(output,token);
                if(!await ContentSecurity.Matches(temp,file,token))throw new InvalidDataException("内容包中的文件校验失败。");
                File.Move(temp,target,true);
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
    private readonly HttpClient client;
    private readonly string cache;
    private readonly long limit;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long transferred;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);
    public Downloader(string cache, LauncherSettings settings, HttpMessageHandler? handler = null)
    {
        this.cache = cache; Directory.CreateDirectory(cache); limit = (long)settings.LimitMiB * 1024 * 1024;
        handler ??= new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, UseCookies = false, Proxy = string.IsNullOrEmpty(settings.Proxy) ? null : new WebProxy(settings.Proxy), AllowAutoRedirect = true };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BoshanLauncher/0.1");
    }
    public async Task<string> Get(ContentFile file, Action<long>? onBytes = null, CancellationToken token = default)
    {
        ContentSecurity.ValidateFile(file);
        var gate = locks.GetOrAdd(file.Sha256, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            var target = ContentSecurity.SafePath(cache, file.Sha256.ToLowerInvariant());
            if (await ContentSecurity.Matches(target, file, token)) return target;
            var part = target + ".part";
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++) foreach (var source in file.Sources)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (offset == file.Size && await ContentSecurity.Matches(part, file, token)) { File.Move(part, target, true); return target; }
                    if (offset >= file.Size) { File.Delete(part); offset = 0; }
                    using var request = new HttpRequestMessage(HttpMethod.Get, source);
                    // This dedicated client has no authorization header, credentials or cookie container.
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); throw new IOException("下载源不支持当前续传位置。"); }
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != file.Size) throw new IOException("下载源返回了错误的续传范围。");
                    }
                    else offset = 0;
                    if (response.Content.Headers.ContentLength is long length && length != file.Size - offset) throw new IOException("下载文件大小与清单不符。");
                    await using (var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    {
                        var buffer = new byte[131072]; var written = offset; int count;
                        while ((count = await input.ReadAsync(buffer, token)) > 0)
                        {
                            written += count; if (written > file.Size) throw new IOException("下载源超出清单大小。");
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                            var overall = Interlocked.Add(ref transferred, count); onBytes?.Invoke(count);
                            if (limit > 0) { var delay = overall * 1000.0 / limit - clock.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token); }
                        }
                        await output.FlushAsync(token);
                    }
                    if (!await ContentSecurity.Matches(part, file, token)) { File.Delete(part); throw new IOException("下载校验失败，正在尝试其他来源。"); }
                    File.Move(part, target, true); return target;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { last = ex; }
            }
            throw new IOException("文件下载失败。可重试，已下载的内容会保留。", last);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { client.Dispose(); foreach(var gate in locks.Values)gate.Dispose(); }
}
