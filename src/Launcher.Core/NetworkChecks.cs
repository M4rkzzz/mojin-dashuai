using System.Diagnostics;
using System.Net;

namespace Boshan.Launcher;

public sealed record NetworkCheckResult(string Name,string Host,bool Ok,long ElapsedMs,int? HttpStatus,NetworkDiagnostic? Diagnostic);
public static class NetworkChecks
{
    public static async Task<NetworkCheckResult[]> Run(LauncherSettings settings,CancellationToken token=default)
    {
        var targets=new[]{("账号服务",NetworkPolicy.DirectApi+"/health"),("服务器目录",NetworkPolicy.DirectApi+"/v1/catalog"),("文件下载",NetworkPolicy.DirectApi+"/diagnostics/download-health.txt")};
        return await Task.WhenAll(targets.Select(async target=>
        {
            var uri=new Uri(target.Item2);var clock=Stopwatch.StartNew();
            using var http=new HttpClient(NetworkPolicy.Handler(settings,allowRedirect:false)){Timeout=TimeSpan.FromSeconds(8)};
            try{using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);NetworkPolicy.EnsureSuccess(response,"网络检测 / "+target.Item1);return new NetworkCheckResult(target.Item1,uri.Authority,true,clock.ElapsedMilliseconds,(int)response.StatusCode,null);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
            catch(Exception ex)when(NetworkPolicy.IsNetwork(ex)){var d=NetworkPolicy.Failure(ex,"网络检测 / "+target.Item1,uri).Diagnostic;return new(target.Item1,uri.Authority,false,clock.ElapsedMilliseconds,d.HttpStatus,d);}
        }));
    }
}
