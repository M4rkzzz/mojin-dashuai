using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

if(args is ["--activities-live"]){await ActivityLive.Run();return;}
if(args is ["--loading-smoke",var loadingOutput]){GameLoadingSmoke.Run(Path.GetFullPath(loadingOutput));return;}
if(args is ["--loading-window-fixture"]){GameLoadingSmoke.Fixture();return;}
if(args is ["--loading-live",var loadingRoot,var loadingInstance]){GameLoadingLive.Run(loadingRoot,loadingInstance);return;}
if(args is ["--join-ipc-smoke",var joinRoot]){await GameJoinSmoke.Run(joinRoot);return;}
if(args is ["--join-server-live",var joinServerInstance,var joinLiveRoot]){await GameJoinLive.Run(joinServerInstance,joinLiveRoot);return;}
if(args is ["--join-api-live",var joinInstance]){await JoinApiLive.Run(joinInstance);return;}
if(args is ["--join-api-identity"]){await JoinApiLive.Run(null);return;}
if(args is ["--window-viewport-smoke"]){WindowViewportSmoke.Run();return;}
if(args is ["--launcher-update-smoke",var fixture]){await LauncherUpdateSmoke.Run(fixture);return;}
if(args is ["--instance-state-smoke"]){InstanceStateSmoke.Run();return;}
if(args is ["--dispatcher-install-smoke"]){DispatcherInstallSmoke.Run();return;}
if(args is ["--content-layout-smoke",var layoutRoot]){ContentDialogLayoutSmoke.Run(layoutRoot);return;}
if(args is ["--microsoft-resume"]){await MicrosoftResume.Run();return;}
if(args is ["--microsoft-contract"]){await MicrosoftContract.Run();return;}
if(args is ["--content-path-smoke",var contentRoot]){await ContentPathSmoke.Run(contentRoot);return;}
if(args is ["--route-latency-smoke"]){foreach(var instance in Routes.Domains.Keys)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{instance,routes=await Routes.ProbeAll(instance)}));return;}
if(args is ["--microsoft-live"]){MicrosoftLive.Run();return;}
if(args is ["--microsoft-network"]){await MicrosoftNetwork.Run();return;}
if(args.Length>0&&args[0] is "--saved-profile" or "--play-saved-account"){await SavedAccountPlay.Run(args);return;}

// Read a disposable test account from stdin; never accept credentials in arguments or print them.
var input=JsonSerializer.Deserialize<SmokeInput>(await Console.In.ReadToEndAsync(),Json.Options)!;
if(!System.Text.RegularExpressions.Regex.IsMatch(input.LoginName,"^HubQA_[a-f0-9]{10}$"))
    throw new InvalidOperationException("Only disposable smoke-test accounts are accepted.");
var root=Path.Combine(Path.GetTempPath(),"mojin-native-auth-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Accounts? accounts=null;
var passed=new List<string>();
void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);passed.Add(name);}
try
{
    NetworkPolicy.Configure(new LauncherSettings{ProxyMode="direct"});
    var vault=new Vault(root);
    accounts=new(vault,NetworkPolicy.DirectApi,"");
    var credentials=JsonSerializer.SerializeToElement(new {input.LoginName,input.Password},Json.Options);
    await accounts.Login("login",credentials);
    Check(accounts.Current?.Profile.GameName==input.LoginName,"Native login deserializes the exact fixed game name");
    Check(accounts.Current?.Profile.Kind=="hub","Group accounts retain the correct account kind");
    Check(File.Exists(Path.Combine(root,"account.dpapi")),"Native session is persisted with Windows DPAPI");
    var ciphertext=await File.ReadAllBytesAsync(Path.Combine(root,"account.dpapi"));
    Check(!System.Text.Encoding.UTF8.GetString(ciphertext).Contains(accounts.Current!.AccessToken),"Persisted file contains no plaintext access token");
    accounts=new(new Vault(root),NetworkPolicy.DirectApi,"");
    var restored=await accounts.Restore();
    Check(restored?.GameName==input.LoginName,"A new native account manager restores the session");
    var game=await accounts.GameSession();
    Check(game.Username==input.LoginName,"Native game session uses the bound game name");
    await accounts.Logout();
    Check(accounts.Current is null&&!File.Exists(Path.Combine(root,"account.dpapi")),"Native logout removes the saved session");
    Console.WriteLine(JsonSerializer.Serialize(new {passed=passed.Count,checks=passed}));
}
catch(Exception ex)
{
    Console.Error.WriteLine("Native account verification failed: "+ex.GetType().Name);
    Environment.ExitCode=1;
}
finally
{
    if(accounts?.Current is not null)try{await accounts.Logout();}catch{}
    var full=Path.GetFullPath(root);
    if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("mojin-native-auth-",StringComparison.Ordinal))
        Directory.Delete(full,true);
}
record SmokeInput(string LoginName,string Password);
