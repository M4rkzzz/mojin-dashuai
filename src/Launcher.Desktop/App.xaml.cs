using System.Windows;
using System.IO;
using Boshan.Launcher;
namespace Boshan.Desktop;
public partial class App : Application
{
    private static bool legacyUpdatePending;
    internal static object StartupUpdateState { get; private set; }=new {Phase="current",Version=(string?)null,Downloaded=0L,Total=0L};
    internal static string ReleaseVersion=>System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion
        ??typeof(App).Assembly.GetName().Version!.ToString(3);
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==2&&e.Args[0]=="--update-ready"&&Guid.TryParseExact(e.Args[1],"N",out _))UpdateStartup.Nonce=e.Args[1];
        legacyUpdatePending=UpdateStartup.Nonce is not null&&!UpdateStartup.ParentChecked;
        // Older installed shortcuts first forward through beta.7. Acknowledge their
        // short health timeout in bootstrap, then check before returning any account.
        if(!legacyUpdatePending&&await CheckAndStart(UpdateStartup.Nonce is null)){Shutdown();return;}
        MainWindow=new MainWindow();MainWindow.Show();
    }
    internal static async Task<bool> CompleteLegacyStartupUpdate()
    {
        if(!legacyUpdatePending)return false;
        legacyUpdatePending=false;
        return await CheckAndStart(checkNetwork:true);
    }
    private static async Task<bool> CheckAndStart(bool checkNetwork)
    {
        try
        {
            var config=Json.Read<AppConfig>(Path.Combine(AppContext.BaseDirectory,"launcher.json"));
            var updates=new LauncherUpdates(UpdateStartup.DataRoot,config.PublicKeys);
            // A replacement launched for the health handshake must open immediately;
            // the parent already checked once for this user-initiated startup.
            if(checkNetwork)
            {
                try
                {
                    var settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher","settings.json");
                    var settings=File.Exists(settingsPath)?Json.Read<LauncherSettings>(settingsPath):new LauncherSettings();settings.Validate();
                    NetworkPolicy.Configure(settings);
                    using var checkTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(22));
                    var envelope=await updates.Fetch(config.Api,checkTimeout.Token);
                    if(envelope is not null)
                    {
                        var release=updates.AcceptMetadata(envelope);
                        if(LauncherVersion.Compare(release.Version,ReleaseVersion)>0&&!updates.HasFailed(release))
                        {
                            using var downloadTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
                            using var downloader=new Downloader(Path.Combine(updates.Root,"cache"),settings,origin:NetworkPolicy.DirectApi);
                            await updates.Prepare(envelope,downloader,token:downloadTimeout.Token);
                        }
                    }
                    Json.Write(Path.Combine(updates.Root,"startup-check.json"),new {At=DateTimeOffset.UtcNow,Version=ReleaseVersion,Phase="checked",BeforeLogin=true});
                }
                catch(Exception ex)
                {
                    var diagnostic=NetworkPolicy.Find(ex);
                    StartupUpdateState=new {Phase="failed",Version=(string?)null,Downloaded=0L,Total=0L,Error=diagnostic is null?"自动更新暂未完成，当前版本可继续使用。":NetworkPolicy.Message(diagnostic),Diagnostic=diagnostic};
                    // Keep diagnostic metadata without serializing URLs or credentials
                    // from exception messages, inner exceptions or the user's settings.
                    try{Json.Write(Path.Combine(updates.Root,"startup-check.json"),new {At=DateTimeOffset.UtcNow,Version=ReleaseVersion,Phase="failed",BeforeLogin=true,ErrorType=ex.GetType().Name,ex.HResult,Diagnostic=diagnostic});}catch(IOException){ }
                }
            }
            for(var attempt=0;attempt<2;attempt++)
            {
                var ready=await updates.Ready(AppContext.BaseDirectory,ReleaseVersion);
                if(ready is null)break;
                if(await UpdateStartup.Start(updates,ready))return true;
            }
        }
        catch(Exception){ /* A damaged/unavailable update must not prevent the installed launcher opening. */ }
        return false;
    }
}
