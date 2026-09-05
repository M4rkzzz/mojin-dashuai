using System.Text.Json;
using System.Web;
using Boshan.Desktop;
using XboxAuthNet.OAuth.CodeFlow;

internal static class MicrosoftNetwork
{
    public static async Task Run()
    {
        // One unauthenticated request with an intentionally invalid test code; no user or browser required.
        var vault=new Vault(Path.Combine(Path.GetTempPath(),"mojin-network-unused-"+Guid.NewGuid().ToString("N")));
        try{await WindowsMicrosoftLogin.Login(vault,null,true,()=>new ProbeUi(),default);Console.WriteLine("Unexpected authentication success.");Environment.ExitCode=1;}
        catch(InvalidDataException){Console.WriteLine("Microsoft token endpoint responded; synthetic authorization correctly rejected.");}
        catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{error=ex.GetType().Name,stage=ex.Data["stage"],requestError=ex.Data["requestError"],transportErrors=ex.Data["transportErrors"]}));Environment.ExitCode=1;}
    }
    private sealed class ProbeUi : IWebUI
    {
        public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri,ICodeFlowUrlChecker checker,CancellationToken token)
        {
            var query=HttpUtility.ParseQueryString(uri.Query);
            return Task.FromResult(checker.GetAuthCodeResult(new Uri("https://login.live.com/oauth20_desktop.srf?code=mojin-invalid-network-test&state="+query["state"])));
        }
        public Task DisplayDialogAndNavigateUri(Uri uri,CancellationToken token)=>throw new InvalidOperationException();
    }
}
