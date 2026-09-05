using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XboxAuthNet.OAuth.CodeFlow;

namespace Boshan.Desktop;

// Remote authentication has no launcher bridge and uses a separate, private browser profile.
public sealed class MicrosoftWebUi(Window owner,string profileRoot) : IWebUI
{
    public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri,ICodeFlowUrlChecker checker,CancellationToken token)
        =>owner.Dispatcher.InvokeAsync(()=>Display(uri,checker,token)).Task.Unwrap();
    public async Task DisplayDialogAndNavigateUri(Uri uri,CancellationToken token)
        =>await owner.Dispatcher.InvokeAsync(()=>Display(uri,null,token)).Task.Unwrap();

    private async Task<CodeFlowAuthorizationResult> Display(Uri uri,ICodeFlowUrlChecker? checker,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(uri.Scheme!="https")throw new InvalidDataException("微软登录地址无效。");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var uiToken=lifetime.Token;
        var complete=new TaskCompletionSource<CodeFlowAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var web=new WebView2();
        var dialog=new Window {Title="微软登录 · 魔金大帅",Width=540,Height=730,MinWidth=420,MinHeight=540,Content=web,Background=Brushes.Black,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        if(owner.IsVisible){dialog.Owner=owner;dialog.ShowInTaskbar=false;}
        dialog.Closed+=(_,_)=>{if(checker is null)complete.TrySetResult(default);else complete.TrySetCanceled();lifetime.Cancel();};
        using var cancellation=token.Register(()=>owner.Dispatcher.BeginInvoke(()=>{complete.TrySetCanceled(token);dialog.Close();}));
        try
        {
            dialog.Show();
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:profileRoot).WaitAsync(uiToken);
            uiToken.ThrowIfCancellationRequested();
            var options=environment.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;options.ProfileName="MicrosoftLogin";
            await web.EnsureCoreWebView2Async(environment,options).WaitAsync(uiToken);
            uiToken.ThrowIfCancellationRequested();
            var core=web.CoreWebView2;
            core.Settings.IsWebMessageEnabled=false;core.Settings.AreHostObjectsAllowed=false;
            core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.ProcessFailed+=(_,_)=>{complete.TrySetException(new InvalidDataException("微软登录窗口已断开，请重新登录。"));dialog.Close();};
            core.NavigationStarting+=(_,e)=>
            {
                if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var address)||address.Scheme!="https"){e.Cancel=true;return;}
                if(checker is null)return;
                try
                {
                    var result=checker.GetAuthCodeResult(address);
                    if(!result.IsEmpty){e.Cancel=true;complete.TrySetResult(result);dialog.Close();}
                }
                catch(Exception){e.Cancel=true;complete.TrySetException(new InvalidDataException("微软授权返回无效，请重新登录。"));dialog.Close();}
            };
            core.NavigationCompleted+=(_,e)=>
            {
                if(!e.IsSuccess&&e.WebErrorStatus!=CoreWebView2WebErrorStatus.OperationCanceled&&!complete.Task.IsCompleted)
                {complete.TrySetException(new InvalidDataException("微软登录页面无法加载，请检查网络后重试。"));dialog.Close();}
            };
            token.ThrowIfCancellationRequested();
            if(complete.Task.IsCompleted)return await complete.Task;
            core.Navigate(uri.AbsoluteUri);
            return await complete.Task;
        }
        finally{dialog.Close();}
    }
}
