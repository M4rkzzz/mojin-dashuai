using System.Windows;
using System.IO;
using Boshan.Launcher;
namespace Boshan.Desktop;
public partial class App : Application
{
    public App()
    {
        StartupDiagnostics.Record("app.constructed");
        if(StartupDiagnostics.Enabled)DispatcherUnhandledException+=(_,e)=>StartupDiagnostics.Error("exception.dispatcher",e.Exception);
    }
    private static bool legacyUpdatePending;
    internal static object StartupUpdateState { get; private set; }=new {Phase="current",Version=(string?)null,Downloaded=0L,Total=0L};
    internal static string ReleaseVersion=>System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion
        ??typeof(App).Assembly.GetName().Version!.ToString(3);
    protected override async void OnStartup(StartupEventArgs e)
    {
        StartupDiagnostics.AttachDispatcher(Dispatcher);
        using var startup=StartupDiagnostics.Begin("app.on-startup");
        if(StartupDiagnostics.Compatibility)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            StartupDiagnostics.Record("compatibility.software-rendering");
        }
        base.OnStartup(e);
        StartupDiagnostics.Record("app.base-startup.completed");
        if(StartupDiagnostics.Enabled&&!StartupDiagnostics.CanWrite)
            MessageBox.Show("诊断日志目录无法写入，请把诊断包解压到可写入的本地文件夹后重试。","启动诊断",MessageBoxButton.OK,MessageBoxImage.Warning);
        if(e.Args.Length==2&&e.Args[0]=="--update-ready"&&Guid.TryParseExact(e.Args[1],"N",out _))UpdateStartup.Nonce=e.Args[1];
        legacyUpdatePending=UpdateStartup.Nonce is not null&&!UpdateStartup.ParentChecked;
        // Older installed shortcuts first forward through beta.7. Acknowledge their
        // short health timeout in bootstrap, then check before returning any account.
        if(StartupDiagnostics.Compatibility)StartupDiagnostics.Record("update.startup.skipped-for-compatibility");
        else if(!legacyUpdatePending&&await CheckAndStart(UpdateStartup.Nonce is null)){Shutdown();return;}
        using(StartupDiagnostics.Begin("window.construct"))MainWindow=new MainWindow();
        using(StartupDiagnostics.Begin("window.show"))MainWindow.Show();
        StartupDiagnostics.Record("window.show.returned");
    }
    protected override void OnExit(ExitEventArgs e)
    {
        StartupDiagnostics.Record("app.on-exit",new {exitCode=e.ApplicationExitCode});
        base.OnExit(e);
        StartupDiagnostics.Stop("app.exit.completed");
    }
    internal static async Task<bool> CompleteLegacyStartupUpdate()
    {
        if(!legacyUpdatePending)return false;
        legacyUpdatePending=false;
        if(StartupDiagnostics.Compatibility){StartupDiagnostics.Record("update.legacy.skipped-for-compatibility");return false;}
        return await CheckAndStart(checkNetwork:true);
    }
    private static async Task<bool> CheckAndStart(bool checkNetwork)
    {
        using var startupUpdate=StartupDiagnostics.Begin("update.startup");
        StartupDiagnostics.Record("update.check.mode",new {checkNetwork});
        try
        {
            using var configuration=StartupDiagnostics.Begin("update.configuration");
            var config=Json.Read<AppConfig>(Path.Combine(AppContext.BaseDirectory,"launcher.json"));
            var updates=new LauncherUpdates(UpdateStartup.DataRoot,config.PublicKeys);
            configuration.Dispose();
            // A replacement launched for the health handshake must open immediately;
            // the parent already checked once for this user-initiated startup.
            if(checkNetwork)
            {
                string? availableVersion=null;
                try
                {
                    var settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher","settings.json");
                    var settings=File.Exists(settingsPath)?Json.Read<LauncherSettings>(settingsPath):new LauncherSettings();settings.Validate();
                    NetworkPolicy.Configure(settings);
                    using var checkTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(22));
                    SignedEnvelope? envelope;
                    using(StartupDiagnostics.Begin("update.metadata.fetch",network:true))envelope=await updates.Fetch(config.Api,checkTimeout.Token);
                    StartupDiagnostics.Record("update.metadata.received",new {available=envelope is not null});
                    if(envelope is not null)
                    {
                        var release=updates.AcceptMetadata(envelope);
                        StartupDiagnostics.Record("update.metadata.accepted",new {newer=LauncherVersion.Compare(release.Version,ReleaseVersion)>0});
                        if(LauncherVersion.Compare(release.Version,ReleaseVersion)>0)
                        {
                            availableVersion=release.Version;
                            if(updates.HasFailed(release))throw new InvalidDataException("新版启动失败，已保留原版本。");
                            using var downloadTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
                            using var downloader=new Downloader(Path.Combine(updates.Root,"cache"),settings,origin:NetworkPolicy.DirectApi);
                            using(StartupDiagnostics.Begin("update.prepare",network:true))await updates.Prepare(envelope,downloader,token:downloadTimeout.Token);
                            StartupDiagnostics.Record("update.prepared");
                        }
                    }
                    Json.Write(Path.Combine(updates.Root,"startup-check.json"),new {At=DateTimeOffset.UtcNow,Version=ReleaseVersion,Phase="checked",BeforeLogin=true});
                    StartupDiagnostics.Record("update.network-check.completed");
                }
                catch(Exception ex)
                {
                    StartupDiagnostics.Error("update.network-check.failed",ex);
                    var diagnostic=NetworkPolicy.Find(ex);
                    StartupUpdateState=new {Phase="failed",Version=availableVersion,Downloaded=0L,Total=0L,Error=diagnostic is null?"自动更新暂未完成，当前版本可继续使用。":NetworkPolicy.Message(diagnostic),Diagnostic=diagnostic};
                    // Keep diagnostic metadata without serializing URLs or credentials
                    // from exception messages, inner exceptions or the user's settings.
                    try{Json.Write(Path.Combine(updates.Root,"startup-check.json"),new {At=DateTimeOffset.UtcNow,Version=ReleaseVersion,Phase="failed",BeforeLogin=true,ErrorType=ex.GetType().Name,ex.HResult,Diagnostic=diagnostic});}catch(IOException){ }
                }
            }
            for(var attempt=0;attempt<2;attempt++)
            {
                PreparedLauncher? ready;
                using(StartupDiagnostics.Begin("update.ready.inspect"))ready=await updates.Ready(AppContext.BaseDirectory,ReleaseVersion);
                if(ready is null)break;
                if(StartupDiagnostics.Enabled)
                {
                    StartupDiagnostics.Record("update.handoff.suppressed");
                    StartupUpdateState=new {Phase="ready",Version=ready.Release.Version,Downloaded=0L,Total=0L};
                    break;
                }
                if(await UpdateStartup.Start(updates,ready))return true;
                StartupUpdateState=new {Phase="failed",Version=ready.Release.Version,Downloaded=0L,Total=0L,Error="新版启动失败，已保留原版本。可以重新检查更新。"};
            }
        }
        catch(Exception ex){StartupDiagnostics.Error("update.startup.failed",ex); /* A damaged/unavailable update must not prevent the installed launcher opening. */ }
        return false;
    }
}
