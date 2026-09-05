using System.Text;
using System.Text.Json;
using System.Web;
using Boshan.Desktop;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.OAuth.CodeFlow;

internal static class MicrosoftContract
{
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-microsoft-contract-"+Guid.NewGuid().ToString("N"));
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            var vault=new Vault(root);
            var cache=new MicrosoftAccountStorage(vault,true,default);
            var manager=new JsonXboxGameAccountManager(cache,JEGameAccount.FromSessionStorage,JsonXboxGameAccountManager.DefaultSerializerOption);
            Check(manager.GetAccounts().Count()==0,"New encrypted account store is empty");
            var account=manager.NewAccount();
            var id="11112222333344445555666677778888";
            var secret="synthetic-test-credential-"+Guid.NewGuid().ToString("N");
            JEProfileSource.Default.Set(account.SessionStorage,new JEProfile{UUID=id,Username="ContractPlayer"});
            JETokenSource.Default.Set(account.SessionStorage,new JEToken{AccessToken=secret,ExpiresOn=DateTime.UtcNow.AddHours(1)});
            manager.SaveAccounts();
            Check(!Directory.Exists(root)||!Directory.EnumerateFiles(root).Any(),"Library saves remain staged until complete login");
            cache.Commit();
            var file=Path.Combine(root,MicrosoftAccountStorage.Key+".dpapi");
            var encrypted=File.ReadAllBytes(file);
            Check(!Encoding.UTF8.GetString(encrypted).Contains(secret),"Library credential cache uses Windows DPAPI");
            var restored=new JsonXboxGameAccountManager(new MicrosoftAccountStorage(vault,false,default),JEGameAccount.FromSessionStorage,JsonXboxGameAccountManager.DefaultSerializerOption);
            var restoredAccount=(JEGameAccount)restored.GetAccounts().Single();
            Check(restoredAccount.Profile?.UUID==id&&restoredAccount.Token?.AccessToken==secret,"Actual CmlLib manager restores UUID and credential");
            Check(new MicrosoftAccountStorage(vault,true,default).ReadAsJsonNode() is null,"New authorization excludes old accounts without deleting their cache");
            using var cancellation=new CancellationTokenSource();
            var cancelledCache=new MicrosoftAccountStorage(vault,true,cancellation.Token);
            cancelledCache.Write(JsonSerializer.SerializeToNode(new{token="cancelled"})!,null);
            cancellation.Cancel();
            try{cancelledCache.Commit();throw new InvalidOperationException("Cancelled cache was committed");}catch(OperationCanceledException){checks.Add("Cancellation prevents encrypted cache commit");}
            Check(encrypted.SequenceEqual(File.ReadAllBytes(file)),"Cancelled login preserves previous encrypted account");
            var ui=new ProbeUi((uri,checker,token)=>
            {
                var query=HttpUtility.ParseQueryString(uri.Query);
                Check(uri.Scheme=="https"&&uri.Host=="login.live.com"&&uri.AbsolutePath=="/oauth20_authorize.srf","Installed CmlLib builds the official Microsoft Windows authorization URL");
                Check(!string.IsNullOrWhiteSpace(query["client_id"])&&query["response_type"]=="code","Default Windows login does not require an Azure app registration");
                Check(query["state"]?.Length==64,"Every authorization uses a random 256-bit state");
                Check(query["prompt"]=="select_account","Microsoft authorization explicitly offers account selection");
                Check(checker.GetAuthCodeResult(new Uri("https://example.com/?code=wrong&state="+query["state"])).IsEmpty,"Only the configured Microsoft callback can finish authorization");
                try{checker.GetAuthCodeResult(new Uri("https://login.live.com/oauth20_desktop.srf?code=wrong&state=wrong"));throw new InvalidOperationException("Wrong OAuth state accepted");}catch(InvalidDataException){checks.Add("Wrong callback state is rejected");}
                var result=checker.GetAuthCodeResult(new Uri("https://login.live.com/oauth20_desktop.srf?code=test&state="+query["state"]));
                Check(result.Code=="test"&&result.State==query["state"],"Matching callback is accepted");
                throw new OperationCanceledException(token);
            });
            try{await WindowsMicrosoftLogin.Login(vault,null,true,()=>ui,default);throw new InvalidOperationException("Cancelled UI completed login");}catch(OperationCanceledException){checks.Add("Closing authorization never produces a game session");}
            Check(ui.Calls==1&&encrypted.SequenceEqual(File.ReadAllBytes(file)),"Actual native login uses the isolated UI and keeps old cache on cancellation");
            var native=new Accounts(vault,"https://launcher.boshan.uk","");
            Check(native.MicrosoftLoginMode=="window","Player configuration selects Windows login by default");
            try{await native.MicrosoftLogin(false);throw new InvalidOperationException("Silent login accepted missing account");}catch(InvalidDataException){checks.Add("Missing account requires sign-in without opening a window");}
            Check(native.Current is null,"Failed sign-in does not authenticate the launcher");
            await native.Logout();
            Check(!File.Exists(file),"Sign-out removes the Microsoft encrypted cache");
            Console.WriteLine(JsonSerializer.Serialize(new{passed=checks.Count,checks,liveMicrosoftLoginVerified=false,windowsOpened=0}));
        }
        catch(Exception ex){Console.Error.WriteLine("Microsoft contract verification failed: "+ex.GetType().Name);Environment.ExitCode=1;}
        finally
        {
            var full=Path.GetFullPath(root);
            if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("mojin-microsoft-contract-",StringComparison.Ordinal)&&Directory.Exists(full))Directory.Delete(full,true);
        }
    }
    private sealed class ProbeUi(Func<Uri,ICodeFlowUrlChecker,CancellationToken,CodeFlowAuthorizationResult> probe) : IWebUI
    {
        public int Calls {get;private set;}
        public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri,ICodeFlowUrlChecker checker,CancellationToken token){Calls++;return Task.FromResult(probe(uri,checker,token));}
        public Task DisplayDialogAndNavigateUri(Uri uri,CancellationToken token)=>throw new InvalidOperationException("Unexpected browser sign-out");
    }
}
