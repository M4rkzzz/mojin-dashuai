using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public sealed record NetworkDiagnostic(string Id,string Stage,string Category,string? Host,string? Path,int? HttpStatus,string Code,string ProxyMode,string? File=null,int? Attempt=null);
public sealed class NetworkFailure(NetworkDiagnostic diagnostic,Exception inner):Exception("网络请求未完成。",inner)
{
    public NetworkDiagnostic Diagnostic {get;}=diagnostic;
}
public static class NetworkPolicy
{
    public const string DirectApi="https://launcher-direct.boshan.uk:21708";
    public const string LegacyApi="https://launcher.boshan.uk";
    private static LauncherSettings current=new();
    public static void Configure(LauncherSettings settings)=>current=settings;
    public static string Mode=>current.ProxyMode;
    public static HttpMessageHandler Handler(LauncherSettings? settings=null,bool allowRedirect=true)
    {
        var s=settings??current;
        return new DiagnosticHandler(s.ProxyMode){InnerHandler=new HttpClientHandler{UseCookies=false,AllowAutoRedirect=allowRedirect,AutomaticDecompression=DecompressionMethods.None,UseProxy=s.ProxyMode!="direct",Proxy=s.ProxyMode=="manual"?new WebProxy(s.Proxy):null}};
    }
    public static IEnumerable<Uri> MetadataSources(Uri original)
    {
        if(original.Host=="launcher.boshan.uk"||original.Host=="launcher-direct.boshan.uk")
        {
            yield return new Uri(DirectApi+original.PathAndQuery);
        }
        else yield return original;
    }
    public static NetworkFailure Failure(Exception error,string stage,Uri? uri=null,string? file=null,int? attempt=null)
    {
        var old=Find(error);
        var http=Chain(error).OfType<HttpRequestException>().FirstOrDefault();var socket=Chain(error).OfType<SocketException>().FirstOrDefault();
        var category=socket?.SocketErrorCode==SocketError.HostNotFound?"DNS 解析失败":error is TaskCanceledException or OperationCanceledException?"请求超时":http?.HttpRequestError switch {HttpRequestError.NameResolutionError=>"DNS 解析失败",HttpRequestError.SecureConnectionError=>"TLS 证书或握手失败",HttpRequestError.ConnectionError=>"连接失败",_=>http?.StatusCode is not null?"服务器返回错误":"网络请求失败"};
        var safePath=uri?.AbsolutePath;
        if(safePath?.StartsWith("/v1/skins/")==true)safePath="/v1/skins/{player}";
        var diagnostic=old??new(Guid.NewGuid().ToString("N")[..10],stage,category,uri?.Authority,safePath,http?.StatusCode is null?null:(int)http.StatusCode,socket?.SocketErrorCode.ToString()??http?.HttpRequestError.ToString()??error.HResult.ToString("X8"),Mode);
        return new(diagnostic with {Stage=stage,File=file??diagnostic.File,Attempt=attempt??diagnostic.Attempt},error);
    }
    private static IEnumerable<Exception> Chain(Exception ex){for(Exception? e=ex;e is not null;e=e.InnerException)yield return e;}
    public static NetworkDiagnostic? Find(Exception ex)=>Chain(ex).Select(e=>e is NetworkFailure n?n.Diagnostic:e.Data["MojinNetworkDiagnostic"] as NetworkDiagnostic).FirstOrDefault(d=>d is not null);
    public static bool IsNetwork(Exception ex)=>Find(ex)is not null||Chain(ex).Any(e=>e is HttpRequestException or SocketException or TaskCanceledException);
    public static string Message(NetworkDiagnostic d)=>$"{d.Stage}：{d.Category}"+(d.HttpStatus is null?"。":$"（HTTP {d.HttpStatus}）。");
    public static void EnsureSuccess(HttpResponseMessage response,string stage)
    {
        try{response.EnsureSuccessStatusCode();}catch(HttpRequestException ex){throw Failure(ex,stage,response.RequestMessage?.RequestUri);}
    }
    public static async Task<byte[]> Metadata(Uri uri,string stage,CancellationToken token=default)
    {
        Exception? last=null;
        using var client=new HttpClient(Handler()){Timeout=TimeSpan.FromSeconds(10),MaxResponseContentBufferSize=16*1024*1024};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MojinDashuai/0.1.2");
        foreach(var source in MetadataSources(uri))
        {
            token.ThrowIfCancellationRequested();
            try{using var response=await client.GetAsync(source,token);EnsureSuccess(response,stage);return await response.Content.ReadAsByteArrayAsync(token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
            catch(Exception ex)when(IsNetwork(ex)){last=Failure(ex,stage,source);}
        }
        throw last??new HttpRequestException("没有可用的请求地址。");
    }
    private sealed class DiagnosticHandler(string mode):DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            try{return await base.SendAsync(request,token);}
            catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException)
            {
                var failure=Failure(ex,"连接服务",request.RequestUri);
                if(token.IsCancellationRequested&&ex is OperationCanceledException){ex.Data["MojinNetworkDiagnostic"]=failure.Diagnostic with{ProxyMode=mode};throw;}
                throw new NetworkFailure(failure.Diagnostic with {ProxyMode=mode},ex);
            }
        }
    }
}
