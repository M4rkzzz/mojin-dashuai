using System.IO;
using XboxAuthNet.OAuth.CodeFlow;

namespace Boshan.Desktop;

public sealed class MicrosoftOAuthWebUi(IWebUI inner,string state) : IWebUI
{
    public async Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri,ICodeFlowUrlChecker checker,CancellationToken token)
    {
        var result=await inner.DisplayDialogAndInterceptUri(uri,new ReturnChecker(checker,state),token);
        token.ThrowIfCancellationRequested();
        Validate(result,state);
        return result;
    }
    public Task DisplayDialogAndNavigateUri(Uri uri,CancellationToken token)=>inner.DisplayDialogAndNavigateUri(uri,token);
    private static void Validate(CodeFlowAuthorizationResult result,string state)
    {
        if(result.IsEmpty||result.State!=state)throw new InvalidDataException("微软授权已失效，请重新登录。");
    }
    private sealed class ReturnChecker(ICodeFlowUrlChecker checker,string state) : ICodeFlowUrlChecker
    {
        public CodeFlowAuthorizationResult GetAuthCodeResult(Uri uri)
        {
            if(uri.Scheme!="https"||uri.Host!="login.live.com"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.AbsolutePath!="/oauth20_desktop.srf")return default;
            var result=checker.GetAuthCodeResult(uri);
            Validate(result,state);
            return result;
        }
    }
}
