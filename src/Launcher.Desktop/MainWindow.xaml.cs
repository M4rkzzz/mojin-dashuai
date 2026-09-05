using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Boshan.Launcher;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Boshan.Shared;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Buffers.Binary;

namespace Boshan.Desktop;

public sealed record AppConfig(string Api,string MicrosoftClientId,Dictionary<string,string> PublicKeys);
public sealed record BridgeRequest(string Id,string Command,JsonElement Args);
public sealed record ResumeDownload(ReleaseRef Release,bool LaunchAfter,TransferProgress? Progress=null,RollbackPin? Rollback=null,bool Repair=false);
public partial class MainWindow : Window
{
    private readonly string appData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
    private readonly Dictionary<string,Process> games=[];
    private readonly Dictionary<string,CancellationTokenSource> transfers=[];
    private readonly Dictionary<string,TransferProgress> transferProgress=[];
    private readonly Dictionary<string,PackManifest> pendingPacks=[];
    private readonly HashSet<string> launchAfterDownload=[];
    private readonly HashSet<string> instanceOperations=[];
    private readonly HashSet<string> cancelledDownloads=[];
    private readonly HashSet<string> pausedDownloads=[];
    private readonly HashSet<string> repairDownloads=[];
    private readonly Dictionary<string,NetworkDiagnostic> diagnostics=[];
    private readonly Dictionary<string,ResumeDownload> savedDownloads=[];
    private readonly Dictionary<string,long> savedProgressAt=[];
    private readonly Dictionary<string,(DateTime Written,long Length,PackManifest Manifest)> installedVersions=[];
    private readonly Dictionary<string,ReleaseRef> availableUpdates=[];
    private readonly ContentUpdateTracker contentUpdates=new();
    private readonly SemaphoreSlim contentCatalogGate=new(1),contentStateGate=new(1);
    private readonly object logGate=new();
    private readonly Dictionary<string,RollbackPin> pendingRollbackPins=[];
    private LauncherSettings settings=new();
    private Accounts accounts=null!;
    private CatalogClient catalog=null!;
    private bool initialized;
    private readonly SemaphoreSlim settingsGate=new(1);
    private CancellationTokenSource? microsoftLogin;
    private MicrosoftDevicePrompt? microsoftPrompt;
    private LauncherUpdates launcherUpdates=null!;
    private string updateApi="";
    private bool checkingLauncherUpdate,restartingLauncher,migratingContent;
    private object launcherUpdate=App.StartupUpdateState;
    public MainWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await Initialize();
        StateChanged+=(_,_)=>{if(initialized)Event("window-state",new {Maximized=WindowState==WindowState.Maximized});};
        Closing+=(_,e)=>{if(games.Count>0){e.Cancel=true;Hide();}};
        Closed+=(_,_)=>{initialized=false;microsoftLogin?.Cancel();};
    }
    private async Task Initialize()
    {
        try
        {
            Directory.CreateDirectory(appData);
            var config=Json.Read<AppConfig>(Path.Combine(AppContext.BaseDirectory,"launcher.json"));
            launcherUpdates=new(UpdateStartup.DataRoot,config.PublicKeys);updateApi=config.Api;
            var settingsPath=Path.Combine(appData,"settings.json");settings=ContentDirectorySetup.LoadSettings(settingsPath,UpdateStartup.ProgramDirectory);settings.Validate();NetworkPolicy.Configure(settings);
            var microsoftId=config.MicrosoftClientId??"";
            accounts=new(new Vault(appData),config.Api,microsoftId.Trim(),()=>new MicrosoftWebUi(this,Path.Combine(appData,"MicrosoftAuth")));
            catalog=new(config.Api,config.PublicKeys,Path.Combine(appData,"catalog-checkpoint.json"));
            RestoreDownloads();
            try{CoreWebView2Environment.GetAvailableBrowserVersionString();}
            catch(WebView2RuntimeNotFoundException){await InstallWebView();}
            var options=new CoreWebView2EnvironmentOptions();
            var webProfile="WebView";
#if DEBUG
            options.AdditionalBrowserArguments="--remote-debugging-port=18474";
            webProfile="WebView-Debug";
#endif
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(appData,webProfile),options:options);
            await Web.EnsureCoreWebView2Async(environment);
            var core=Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreHostObjectsAllowed=false;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.Settings.IsNonClientRegionSupportEnabled=true;
            core.SetVirtualHostNameToFolderMapping("app.boshan.local",Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="app.boshan.local")e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.WebMessageReceived+=HandleMessage;
            core.NavigationCompleted+=(_,_)=>{Loading.Visibility=Visibility.Collapsed;Event("window-state",new {Maximized=WindowState==WindowState.Maximized});};
            initialized=true;core.Navigate("https://app.boshan.local/index.html");
        }
        catch(Exception ex){Loading.Text="启动器准备失败："+Friendly(ex);File.WriteAllText(Path.Combine(appData,"startup-diagnostic.txt"),ex.GetType().FullName+"\n"+ex.HResult+"\n"+ex.StackTrace);}
    }
    private async Task InstallWebView()
    {
        Loading.Text="正在自动安装显示组件…";
        using var http=new HttpClient();var setup=Path.Combine(appData,"MicrosoftEdgeWebview2Setup.exe");
        await using(var output=File.Create(setup))await (await http.GetStreamAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703")).CopyToAsync(output);
        using var verify=new Process {StartInfo=new("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true}};
        verify.StartInfo.ArgumentList.Add("-NoProfile");verify.StartInfo.ArgumentList.Add("-NonInteractive");verify.StartInfo.ArgumentList.Add("-Command");
        verify.StartInfo.ArgumentList.Add("$s=Get-AuthenticodeSignature -LiteralPath '"+setup.Replace("'","''")+"'; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notlike '*O=Microsoft Corporation*') { exit 1 }");
        verify.Start();await verify.WaitForExitAsync();if(verify.ExitCode!=0)throw new InvalidDataException("WebView2 安装文件签名无效。");
        using var installer=Process.Start(new ProcessStartInfo(setup,"/silent /install"){UseShellExecute=false,CreateNoWindow=true})!;
        await installer.WaitForExitAsync();if(installer.ExitCode!=0)throw new IOException("显示组件安装失败，请检查网络后重试。");
    }
    private async void HandleMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(!initialized||!Uri.TryCreate(e.Source,UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.Host!="app.boshan.local")return;
        BridgeRequest? request=null;
        try
        {
            if(e.WebMessageAsJson.Length>32*1024)throw new InvalidDataException("请求过大。");
            request=JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson,Json.Options);
            if(request is null||!Guid.TryParse(request.Id,out _))return;
            var result=await Dispatch(request.Command,request.Args);
            if(!initialized)return;
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {request.Id,Ok=true,Result=result},Json.Options));
        }
        catch(Exception ex)
        {
            var diagnostic=NetworkPolicy.Find(ex)??(NetworkPolicy.IsNetwork(ex)?NetworkPolicy.Failure(ex,request?.Command??"连接服务").Diagnostic:null);
            if(diagnostic is not null)RememberDiagnostic(diagnostic);
            if(initialized&&request is not null)Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {request.Id,Ok=false,Error=Friendly(ex),Diagnostic=diagnostic},Json.Options));
            Log(request?.Command??"bridge",ex);
        }
    }
    private async Task<object?> Dispatch(string command,JsonElement args)
    {
        if(migratingContent&&!command.StartsWith("window.",StringComparison.Ordinal))throw new InvalidDataException("正在迁移内容目录，请稍候。");
        if(restartingLauncher&&!command.StartsWith("window.",StringComparison.Ordinal))throw new InvalidDataException("正在打开新版启动器。");
        string Id(){var id=args.GetProperty("instance").GetString()!;if(!Routes.Domains.ContainsKey(id))throw new InvalidDataException("未知服务器。");return id;}
        if(!settings.ContentDirectoryConfigured&&(command.StartsWith("instance.",StringComparison.Ordinal)||command.StartsWith("download.",StringComparison.Ordinal)||command is "settings.save" or "directory.migrate" or "cache.clean" or "content.manage"))
            throw new InvalidDataException("请先设置游戏文件保存位置。");
        switch(command)
        {
            case "bootstrap":
                UpdateStartup.MarkReady();
                if(await App.CompleteLegacyStartupUpdate()){Application.Current.Shutdown();return null;}
                launcherUpdate=App.StartupUpdateState;
                return new {LauncherVersion=App.ReleaseVersion.Split('+')[0],Profile=await accounts.Restore(),Settings=settings,LauncherUpdate=launcherUpdate,WindowMaximized=WindowState==WindowState.Maximized,Installs=await InstalledStates(),Progress=transferProgress,States=InstanceStates(),AvailableUpdates=availableUpdates};
            case "instances.status":return new {Installs=await InstalledStates(),Progress=transferProgress,States=InstanceStates(),AvailableUpdates=availableUpdates};
            case "instances.updates.check":
            {
                if(accounts.Current is null||!settings.ContentDirectoryConfigured)return null;
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try{await FetchContentCatalog(timeout.Token);}
                catch(Exception ex){Log("content-updates.check",ex);}
                return new {Installs=await InstalledStates(),Progress=transferProgress,States=InstanceStates(),AvailableUpdates=availableUpdates};
            }
            case "community.join":
                Process.Start(new ProcessStartInfo("https://qm.qq.com/q/Bfat8qcPvO"){UseShellExecute=true});return null;
            case "launcher.update.check":return await CheckLauncherUpdate(true);
            case "launcher.update.status":return launcherUpdate;
            case "launcher.update.restart":
                if(checkingLauncherUpdate||games.Count>0||instanceOperations.Count>0||transfers.Count>0||savedDownloads.Count>0||microsoftLogin is not null)throw new InvalidDataException("请先结束游戏、下载或登录任务。");
                var update=await launcherUpdates.Ready(AppContext.BaseDirectory,App.ReleaseVersion);
                if(update is null)throw new InvalidDataException("没有已准备好的启动器更新。");
                restartingLauncher=true;
                try{if(!await UpdateStartup.Start(launcherUpdates,update))throw new InvalidDataException("新版启动失败，当前版本已保留。");Application.Current.Shutdown();return null;}
                finally{restartingLauncher=false;}
            case "auth.login":if(microsoftLogin is not null)throw new InvalidDataException("请先取消正在进行的微软登录。");return await accounts.Login("login",args);
            case "auth.register":if(microsoftLogin is not null)throw new InvalidDataException("请先取消正在进行的微软登录。");return await accounts.Login("register",args);
            case "auth.recover":return await accounts.Recover(args);
            case "auth.microsoft":return await MicrosoftSignIn();
            case "auth.microsoft.cancel":microsoftLogin?.Cancel();return null;
            case "auth.microsoft.copy":Clipboard.SetText(ActiveMicrosoftPrompt().UserCode);return null;
            case "auth.microsoft.open":Process.Start(new ProcessStartInfo(ActiveMicrosoftPrompt().VerificationUrl){UseShellExecute=true});return null;
            case "auth.logout":microsoftLogin?.Cancel();await accounts.Logout();return null;
            case "directory.choose":
            {
                if(accounts.Current is null)throw new InvalidDataException("请先登录账号。");
                var picker=new OpenFolderDialog{Title="选择游戏文件保存位置"};
                return picker.ShowDialog(this)==true?picker.FolderName:null;
            }
            case "directory.initialize":
                await settingsGate.WaitAsync();
                try
                {
                    if(accounts.Current is null)throw new InvalidDataException("请先登录账号。");
                    if(games.Count>0||instanceOperations.Count>0||transfers.Count>0||savedDownloads.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
                    settings=ContentDirectorySetup.Complete(settings,Path.Combine(appData,"settings.json"),args.GetProperty("root").GetString()??"");
                    return settings;
                }
                finally{settingsGate.Release();}
            case "settings.save":
                await settingsGate.WaitAsync();
                try
                {
                    var next=args.Deserialize<LauncherSettings>(Json.Options)!;next.Validate();
                    next.ContentDirectoryConfigured=settings.ContentDirectoryConfigured;
                    if(!Path.GetFullPath(next.Root).Equals(Path.GetFullPath(settings.Root),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请通过目录迁移功能更换内容目录。");
                    foreach(var id in Routes.Domains.Keys)if(next.Java[id]!=settings.Java[id]&&!string.IsNullOrEmpty(next.Java[id]))await RuntimeManager.Validate(next.Java[id],id is "m3e" or "vw"?8:id=="dc2"?17:25);
                    var restoreGraphics=settings.PreferDedicatedGpu&&!next.PreferDedicatedGpu;
                    Json.Write(Path.Combine(appData,"settings.json"),next);settings=next;NetworkPolicy.Configure(settings);accounts.ReconfigureNetwork();
                    if(restoreGraphics)foreach(var graphics in await Task.Run(()=>GraphicsPreference.RestoreAll(appData)))LogGraphics(graphics.Status,graphics.Success,graphics.Message);
                    return null;
                }
                finally{settingsGate.Release();}
            case "routes.probe":return (await Routes.ProbeAll(Id())).Select(x=>x.Latency).ToArray();
            case "instance.install":
            {
                var id=Id();var downloadOnly=args.TryGetProperty("downloadOnly",out var only)&&only.GetBoolean();
                await InstanceOperation(id,async()=>
                {
                    repairDownloads.Remove(id);if(downloadOnly)launchAfterDownload.Remove(id);else launchAfterDownload.Add(id);
                    await Install(id);
                    if(!pendingPacks.ContainsKey(id)&&launchAfterDownload.Remove(id)&&!cancelledDownloads.Contains(id)&&!pausedDownloads.Contains(id))await Launch(id,false);
                });return null;
            }
            case "instance.repair":
            {
                var id=Id();InstallationSummary? summary=null;
                if(!File.Exists(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(id),".hub","installed.json")))throw new InvalidDataException("下载客户端后才可以检查并修复。");
                await InstanceOperation(id,async()=>{launchAfterDownload.Remove(id);repairDownloads.Add(id);summary=await Install(id);});
                return summary is null?new{Paused=transferProgress.ContainsKey(id),Cancelled=!transferProgress.ContainsKey(id),Message=(string?)null}:new{Summary=summary,Message=RepairMessage(summary,id)};
            }
            case "instance.update":
            {
                var id=Id();
                if(!File.Exists(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(id),".hub","installed.json")))throw new InvalidDataException("请先安装这个世界。");
                await InstanceOperation(id,async()=>{launchAfterDownload.Remove(id);repairDownloads.Remove(id);await Install(id,onlyUpdates:true);});
                return null;
            }
            case "instance.rollback":{var id=Id();await InstanceOperation(id,()=>Rollback(id));return null;}
            case "download.pause":
            {
                var id=Id();pausedDownloads.Add(id);if(transfers.TryGetValue(id,out var transfer))transfer.Cancel();return null;
            }
            case "download.resume":
            {
                var id=Id();await InstanceOperation(id,async()=>
                {
                    if(!savedDownloads.TryGetValue(id,out var saved))throw new InvalidDataException("没有可续传的任务。");
                    await accounts.GameSession();
                    if(cancelledDownloads.Contains(id))return;
                    var directory=await FetchContentCatalog();var server=directory.Servers.Single(x=>x.Id==id);
                    if(cancelledDownloads.Contains(id))return;
                    var release=DownloadResumePolicy.SelectRelease(server,saved.Release,saved.Rollback is not null);
                    // Refresh even in-memory tasks: a paused transfer can outlive a publication.
                    // Verified objects and partial downloads remain reusable by their hashes.
                    var pack=await catalog.GetManifest(id,release);
                    if(cancelledDownloads.Contains(id))return;
                    TrackDownload(id,release);
                    await Transfer(pack);
                    if(!pendingPacks.ContainsKey(id)&&launchAfterDownload.Remove(id)&&!cancelledDownloads.Contains(id)&&!pausedDownloads.Contains(id))await Launch(id,false);
                });return null;
            }
            case "download.cancel":
            {
                var id=Id();launchAfterDownload.Remove(id);cancelledDownloads.Add(id);
                if(transfers.TryGetValue(id,out var transfer))transfer.Cancel();
                else{pendingPacks.Remove(id);pendingRollbackPins.Remove(id);transferProgress.Remove(id);ForgetDownload(id);Event("download-cancelled",new{Instance=id});Event("instance-state",new{Instance=id,State=InstanceState(id)});}
                return null;
            }
            case "instance.launch":{var id=Id();await InstanceOperation(id,()=>Launch(id));return null;}
            case "instance.folder":OpenFolder(new TransactionalInstaller(settings.Root).InstancePath(Id()));return null;
            case "instance.import":return await Import(Id(),args);
            case "instance.import.backups":OpenFolder(ContentSecurity.SafePath(new TransactionalInstaller(settings.Root).InstancePath(Id()),".hub/personal-import"));return null;
            case "directory.migrate":return await Migrate();
            case "cache.clean":return CleanCache();
            case "content.manage":return ContentDialog(Id());
            case "diagnostics.export":return ExportDiagnostics();
            case "diagnostics.copy":
                var diagnosticId=args.GetProperty("id").GetString()??"";
                if(!diagnostics.TryGetValue(diagnosticId,out var diagnostic))throw new InvalidDataException("诊断记录已过期，请重新检测。");
                Clipboard.SetText(JsonSerializer.Serialize(diagnostic,Json.Options));return null;
            case "network.check":
                var checks=await NetworkChecks.Run(settings);
                foreach(var check in checks)if(check.Diagnostic is not null)RememberDiagnostic(check.Diagnostic);
                return checks;
            case "account.password":return await AccountDialog(false);
            case "account.recovery":return await AccountDialog(true);
            case "account.avatar":return await accounts.Skin(settings.SkinSource,args.TryGetProperty("refresh",out var refreshSkin)&&refreshSkin.GetBoolean());
            case "account.skin.preview":return await accounts.SkinPreview(settings.SkinSource,args.TryGetProperty("refresh",out var refreshPreview)&&refreshPreview.GetBoolean());
            case "account.skin.source":
                var source=args.GetProperty("source").GetString();
                if(source is not ("account" or "littleskin"))throw new InvalidDataException("不支持的皮肤来源。");
                settings.SkinSource=source;Json.Write(Path.Combine(appData,"settings.json"),settings);
                return null;
            case "account.skin.open":
                Process.Start(new ProcessStartInfo("https://littleskin.cn/user"){UseShellExecute=true});return null;
            case "account.skin":return await SkinDialog(args);
            case "window.minimize":WindowState=WindowState.Minimized;return null;
            case "window.maximize":WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;return null;
            case "window.close":Close();return null;
            default:throw new InvalidDataException("启动器不支持此操作。");
        }
    }
    private string InstanceState(string id)
    {
        if(games.ContainsKey(id)||new TransactionalInstaller(settings.Root).IsRunning(id))return "running";
        if(transferProgress.TryGetValue(id,out var p))return p.Paused?"paused":"downloading";
        if(instanceOperations.Contains(id))return "preparing";
        return File.Exists(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(id),".hub","installed.json"))?(availableUpdates.ContainsKey(id)?"update-available":"installed"):"not-installed";
    }
    private Dictionary<string,string> InstanceStates()=>Routes.Domains.Keys.ToDictionary(id=>id,InstanceState);
    private async Task<object> InstalledStates()
    {
        await contentStateGate.WaitAsync();
        try
        {
        var found=new Dictionary<string,object>();
        foreach(var id in Routes.Domains.Keys)
        {
            var path=Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(id),".hub","installed.json");
            var info=new FileInfo(path);if(!info.Exists){installedVersions.Remove(path);availableUpdates.Remove(id);continue;}
            if(!installedVersions.TryGetValue(path,out var cached)||cached.Written!=info.LastWriteTimeUtc||cached.Length!=info.Length)
            {
                var manifest=await Task.Run(()=>Json.Read<InstalledPack>(path).Manifest);
                cached=(info.LastWriteTimeUtc,info.Length,manifest);installedVersions[path]=cached;
            }
            var pinPath=Path.Combine(Path.GetDirectoryName(path)!,"rollback-pin.json");
            var pin=await Task.Run(()=>File.Exists(pinPath)?Json.Read<RollbackPin>(pinPath):null);
            var available=await contentUpdates.Available(cached.Manifest,pin);
            if(available is null)availableUpdates.Remove(id);else availableUpdates[id]=available;
            found[id]=new{Version=cached.Manifest.Version,Sequence=cached.Manifest.Sequence,State=InstanceState(id)};
        }
        return found;
        }
        finally{contentStateGate.Release();}
    }
    private async Task<Catalog> FetchContentCatalog(CancellationToken token=default)
    {
        await contentCatalogGate.WaitAsync(token);
        try
        {
            var directory=await catalog.Fetch(token);contentUpdates.Accept(directory);return directory;
        }
        finally{contentCatalogGate.Release();}
    }
    private string DownloadRecordPath(string id)=>ContentSecurity.SafePath(new TransactionalInstaller(settings.Root).InstancePath(id),".hub/download.json");
    private void RestoreDownloads()
    {
        if(!settings.ContentDirectoryConfigured)return;
        foreach(var id in Routes.Domains.Keys)
        {
            var path=DownloadRecordPath(id);if(!File.Exists(path))continue;
            try
            {
                var saved=Json.Read<ResumeDownload>(path);savedDownloads[id]=saved;
                if(saved.Repair)repairDownloads.Add(id);
                if(saved.LaunchAfter)launchAfterDownload.Add(id);
                if(saved.Rollback is not null)pendingRollbackPins[id]=saved.Rollback;
                transferProgress[id]=(saved.Progress??new(id,"下载已暂停",0,0,0)) with {Instance=id,Paused=true,BytesPerSecond=0};
            }
            catch(Exception ex)when(ex is IOException or JsonException or InvalidDataException){Log("download.restore",ex);}
        }
    }
    private void TrackDownload(string id,ReleaseRef release)
    {
        savedDownloads[id]=new(release,launchAfterDownload.Contains(id),transferProgress.GetValueOrDefault(id),pendingRollbackPins.GetValueOrDefault(id),repairDownloads.Contains(id));
        Json.Write(DownloadRecordPath(id),savedDownloads[id]);
    }
    private void SaveDownloadProgress(TransferProgress progress,bool force=false)
    {
        if(!savedDownloads.TryGetValue(progress.Instance,out var saved))return;
        var now=Environment.TickCount64;if(!force&&now-savedProgressAt.GetValueOrDefault(progress.Instance)<2000)return;
        savedProgressAt[progress.Instance]=now;savedDownloads[progress.Instance]=saved with {Progress=progress};
        try{Json.Write(DownloadRecordPath(progress.Instance),savedDownloads[progress.Instance]);}
        catch(IOException ex){Log("download.checkpoint",ex);}
    }
    private void ForgetDownload(string id)
    {
        savedDownloads.Remove(id);savedProgressAt.Remove(id);repairDownloads.Remove(id);var path=DownloadRecordPath(id);if(File.Exists(path))File.Delete(path);
    }
    private async Task InstanceOperation(string id,Func<Task> operation)
    {
        if(!instanceOperations.Add(id))throw new InvalidDataException("此世界已有任务正在进行。");
        cancelledDownloads.Remove(id);pausedDownloads.Remove(id);Event("instance-state",new{Instance=id,State=InstanceState(id)});
        try{await operation();}
        catch
        {
            if(savedDownloads.ContainsKey(id)&&!transferProgress.ContainsKey(id))
            {
                var paused=new TransferProgress(id,"下载未完成",0,0,0,true);transferProgress[id]=paused;SaveDownloadProgress(paused,true);Event("progress",paused);
            }
            throw;
        }
        finally
        {
            instanceOperations.Remove(id);cancelledDownloads.Remove(id);pausedDownloads.Remove(id);
            if(!pendingPacks.ContainsKey(id)&&!savedDownloads.ContainsKey(id))launchAfterDownload.Remove(id);
            Event("instance-state",new{Instance=id,State=InstanceState(id)});
        }
    }
    private async Task<InstallationSummary?> Install(string id,bool onlyUpdates=false)
    {
        pendingRollbackPins.Remove(id);
        await accounts.GameSession();
        if(cancelledDownloads.Contains(id))return null;
        var directory=await FetchContentCatalog();var server=directory.Servers.SingleOrDefault(s=>s.Id==id);
        if(cancelledDownloads.Contains(id))return null;
        if(server?.Release is null)throw new InvalidDataException("这个世界正在完成安装与入服验收，正式内容尚未开放。");
        var release=server.Release;
        if(onlyUpdates)
        {
            var installer=new TransactionalInstaller(settings.Root);
            var installed=await Task.Run(()=>installer.ReadInstalled(id)?.Manifest??throw new InvalidDataException("请先安装这个世界。"));
            var pinPath=Path.Combine(installer.InstancePath(id),".hub","rollback-pin.json");
            var pin=await Task.Run(()=>File.Exists(pinPath)?Json.Read<RollbackPin>(pinPath):null);
            release=await contentUpdates.Available(installed,pin);
            if(release is null){await InstalledStates();return null;}
        }
        TrackDownload(id,release);
        var pack=await catalog.GetManifest(id,release);if(cancelledDownloads.Contains(id))return null;return await Transfer(pack);
    }
    private async Task<InstallationSummary?> Transfer(PackManifest pack)
    {
        if(cancelledDownloads.Contains(pack.Instance))return null;
        if(transfers.ContainsKey(pack.Instance))throw new InvalidDataException("此世界已有下载任务。");
        pendingPacks[pack.Instance]=pack;using var cancellation=new CancellationTokenSource();transfers[pack.Instance]=cancellation;
        using var downloader=new Downloader(Path.Combine(settings.Root,"cache"),settings,origin:NetworkPolicy.DirectApi);
        transferProgress[pack.Instance]=new(pack.Instance,"正在准备文件",0,pack.Files.Sum(f=>f.Size),0);Event("progress",transferProgress[pack.Instance]);
        var root=settings.Root;var concurrency=settings.Concurrency;string? phase=null;
        using var progress=new DispatcherTransferProgress(Dispatcher,p=>
        {
            if(transfers.TryGetValue(p.Instance,out var active)&&ReferenceEquals(active,cancellation)&&!cancellation.IsCancellationRequested){transferProgress[p.Instance]=p;SaveDownloadProgress(p);Event("progress",p);}
        },p=>
        {
            if(phase is not null)LogPhase(pack.Instance,phase,"completed");phase=p.Phase;LogPhase(pack.Instance,phase,"started");
        });
        try
        {
            InstallationSummary? summary=null;
            try{await BackgroundInstallation.Run(async()=>{summary=await new TransactionalInstaller(root).Install(pack,downloader,concurrency,progress,cancellation.Token);},cancellation.Token);}
            finally{progress.Dispose();}
            // Stop queued progress before terminal cleanup or any UI await. A late final
            // report must never recreate a completed download in the native state map.
            if(phase is not null)LogPhase(pack.Instance,phase,"completed");phase=null;
            if(pendingRollbackPins.Remove(pack.Instance,out var pin))Json.Write(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(pack.Instance),".hub","rollback-pin.json"),pin);
            if(repairDownloads.Contains(pack.Instance)&&summary is not null)Event("repair-result",new{Instance=pack.Instance,Summary=summary,Message=RepairMessage(summary,pack.Instance)});
            pendingPacks.Remove(pack.Instance);transferProgress.Remove(pack.Instance);ForgetDownload(pack.Instance);
            installedVersions.Remove(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(pack.Instance),".hub","installed.json"));
            await InstalledStates();Event("installed",new{Instance=pack.Instance});return summary;
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested)
        {
            if(progress.Current is {} latest)transferProgress[pack.Instance]=latest;
            if(phase is not null)LogPhase(pack.Instance,phase,cancelledDownloads.Contains(pack.Instance)?"cancelled":"paused");phase=null;
            if(cancelledDownloads.Contains(pack.Instance)){pendingPacks.Remove(pack.Instance);pendingRollbackPins.Remove(pack.Instance);transferProgress.Remove(pack.Instance);ForgetDownload(pack.Instance);Event("download-cancelled",new{Instance=pack.Instance});}
            else PauseProgress(pack,"下载已暂停");
            return null;
        }
        catch{if(progress.Current is {} latest)transferProgress[pack.Instance]=latest;if(phase is not null)LogPhase(pack.Instance,phase,"failed");PauseProgress(pack,"下载未完成");throw;}
        finally{transfers.Remove(pack.Instance);}
    }
    private static string RepairMessage(InstallationSummary summary,string id)
    {
        var changes=new List<string>();
        if(summary.RestoredFiles>0)changes.Add($"补齐 {summary.RestoredFiles} 个文件");
        if(summary.RepairedFiles>0)changes.Add($"修复 {summary.RepairedFiles} 个文件");
        if(summary.RemovedFiles>0)changes.Add($"移除 {summary.RemovedFiles} 个旧版文件");
        if(summary.RuntimePrepared)changes.Add($"补齐 Java {(id is "m3e" or "vw"?8:id=="dc2"?17:25)}");
        return changes.Count==0?$"检查完成，{summary.CheckedFiles} 个文件完整。":$"已检查 {summary.CheckedFiles} 个文件，{string.Join("，",changes)}。";
    }
    private void PauseProgress(PackManifest pack,string phase)
    {
        var last=transferProgress.GetValueOrDefault(pack.Instance)??new(pack.Instance,phase,0,pack.Files.Sum(f=>f.Size),0);
        var paused=last with {Paused=true,BytesPerSecond=0};transferProgress[pack.Instance]=paused;SaveDownloadProgress(paused,true);Event("progress",paused);
    }
    private async Task Rollback(string id)
    {
        var installer=new TransactionalInstaller(settings.Root);var oldPath=Path.Combine(installer.InstancePath(id),".hub","previous.json");
        if(!File.Exists(oldPath))throw new InvalidDataException("没有可回退的上一版本。");
        var old=await Task.Run(()=>Json.Read<InstalledPack>(oldPath));var directory=await FetchContentCatalog();var server=directory.Servers.Single(s=>s.Id==id);
        var release=server.Rollbacks.SingleOrDefault(r=>r.Version==old.Manifest.Version&&r.Compatibility==server.Release?.Compatibility);
        if(release is null)throw new InvalidDataException("上一版本未被列为当前服务器可用的回退版本。");
        var pack=await catalog.GetManifest(id,release);
        launchAfterDownload.Remove(id);
        pendingRollbackPins[id]=new(pack.Version,pack.Sequence,server.Release!.Sequence);
        TrackDownload(id,release);
        await Transfer(pack);
    }
    private async Task<object> CheckLauncherUpdate(bool retry=false)
    {
        if(checkingLauncherUpdate||restartingLauncher)return launcherUpdate;
        checkingLauncherUpdate=true;
        string? availableVersion=null;
        void State(string phase,string? version=null,long downloaded=0,long total=0,string? error=null)
        {
            launcherUpdate=new {Phase=phase,Version=version,Downloaded=downloaded,Total=total,Error=error};
            if(initialized)Event("launcher-update",launcherUpdate);
        }
        try
        {
            State("checking");
            var envelope=await launcherUpdates.Fetch(updateApi);
            if(envelope is not null)
            {
                var release=launcherUpdates.AcceptMetadata(envelope);
                if(LauncherVersion.Compare(release.Version,App.ReleaseVersion)>0)
                {
                    availableVersion=release.Version;
                    if(retry)launcherUpdates.Retry(release);
                    if(launcherUpdates.HasFailed(release))throw new InvalidDataException("新版启动失败，已保留原版本。可以重新检查更新。");
                    var downloadBytes=await launcherUpdates.PendingDownloadBytes(release,AppContext.BaseDirectory);
                    State("downloading",release.Version,total:downloadBytes);
                    using var downloader=new Downloader(Path.Combine(launcherUpdates.Root,"cache"),settings,origin:NetworkPolicy.DirectApi);
                    long received=0,last=0;
                    await launcherUpdates.Prepare(envelope,downloader,bytes=>{received+=bytes;var now=Environment.TickCount64;if(now-last>500){last=now;State("downloading",release.Version,received,downloadBytes);}});
                }
            }
            var ready=await launcherUpdates.Ready(AppContext.BaseDirectory,App.ReleaseVersion);
            State(ready is null?"current":"ready",ready?.Release.Version);
            if(ready is not null&&games.Count==0&&transfers.Count==0&&instanceOperations.Count==0&&savedDownloads.Count==0&&microsoftLogin is null)
            {
                restartingLauncher=true;
                try
                {
                    if(await UpdateStartup.Start(launcherUpdates,ready)){Application.Current.Shutdown();return launcherUpdate;}
                    State("failed",ready.Release.Version,error:"新版启动失败，已保留原版本。可以重新检查更新。");
                }
                finally{restartingLauncher=false;}
            }
        }
        catch(Exception ex){State("failed",availableVersion,error:Friendly(ex));Log("launcher-update",ex);}
        finally{checkingLauncherUpdate=false;}
        return launcherUpdate;
    }
    private async Task Launch(string id,bool checkForUpdates=true)
    {
        if(games.ContainsKey(id))throw new InvalidDataException("此世界已在运行。");
        if(savedDownloads.ContainsKey(id))throw new InvalidDataException("请先完成当前下载。");
        await accounts.GameSession();var installer=new TransactionalInstaller(settings.Root);
        if(checkForUpdates)
        {
            var installed=await Task.Run(()=>
            {
                using var preparation=installer.Acquire(id);
                installer.Recover(id);return (installer.ReadInstalled(id)??throw new InvalidDataException("请先安装这个世界。")).Manifest;
            });
            var pinPath=Path.Combine(installer.InstancePath(id),".hub","rollback-pin.json");
            var pin=File.Exists(pinPath)?Json.Read<RollbackPin>(pinPath):null;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var release=await LaunchUpdates.Check(installed,pin,FetchContentCatalog,timeout.Token);
            if(release is not null)
            {
                var updated=await catalog.GetManifest(id,release);
                pendingRollbackPins.Remove(id);launchAfterDownload.Remove(id);TrackDownload(id,release);await Transfer(updated);
                return;
            }
        }
        // Installation can outlive an access token; obtain a current session just before launching.
        var session=await accounts.GameSession();var gate=installer.Acquire(id);
        try
        {
            var pack=await Task.Run(()=>{installer.Recover(id);return installer.ReadInstalled(id)??throw new InvalidDataException("请先安装这个世界。");});
            var route=await Routes.Select(id,settings.SelectedRoutes[id]);
            var process=await new GameLauncher().Prepare(pack.Manifest,settings,session,route);
            var graphics=await Task.Run(()=>GraphicsPreference.Apply(process.StartInfo.FileName,settings.PreferDedicatedGpu,appData));LogGraphics(graphics.Status,graphics.Success,graphics.Message);
            process.Start();
            games[id]=process;Json.Write(Path.Combine(installer.InstancePath(id),".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime()));
            Event("instance-state",new{Instance=id,State="running"});
            _=ObserveGame(id,process,gate);
            if(settings.WindowBehavior=="minimize")WindowState=WindowState.Minimized;
            if(settings.WindowBehavior=="hide")Hide();
        }
        catch{gate.Dispose();throw;}
    }
    private async Task ObserveGame(string id,Process process,FileStream gate)
    {
        await process.WaitForExitAsync();var code=process.ExitCode;
        await Dispatcher.InvokeAsync(()=>{games.Remove(id);gate.Dispose();process.Dispose();Event("instance-state",new{Instance=id,State=InstanceState(id)});Show();if(code!=0)Event("error","游戏已退出。可导出诊断日志帮助定位问题。");});
    }
    private MicrosoftDevicePrompt ActiveMicrosoftPrompt()
    {
        if(microsoftLogin is null||microsoftLogin.IsCancellationRequested||microsoftPrompt is null||microsoftPrompt.ExpiresAt<=DateTimeOffset.UtcNow)
            throw new InvalidDataException("登录码已失效，请重新登录。");
        return microsoftPrompt;
    }
    private async Task<object> MicrosoftSignIn()
    {
        if(microsoftLogin is not null)throw new InvalidDataException("微软登录正在进行中。");
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(16));
        microsoftLogin=cancellation;
        Event("microsoft-mode",accounts.MicrosoftLoginMode);
        try
        {
            return await accounts.MicrosoftLogin(true,prompt=>Dispatcher.InvokeAsync(()=>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                microsoftPrompt=prompt;Event("microsoft-code",prompt);
            }).Task,cancellation.Token);
        }
        catch(OperationCanceledException){return new {Cancelled=true};}
        finally{microsoftPrompt=null;microsoftLogin=null;Event("microsoft-code",null);}
    }
    private void Event(string name,object? data)=>Dispatcher.Invoke(()=>
    {
        if(initialized&&!Dispatcher.HasShutdownStarted&&Web.CoreWebView2 is not null)
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {Event=name,Data=data},Json.Options));
    });
    private void OpenFolder(string path){Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private async Task<object?> Import(string id,JsonElement args)
    {
        if(games.ContainsKey(id)||instanceOperations.Contains(id)||transfers.ContainsKey(id)||savedDownloads.ContainsKey(id))throw new InvalidDataException("请先结束当前服务器的游戏和下载任务。");
        if(new TransactionalInstaller(settings.Root).ReadInstalled(id) is null)throw new InvalidDataException("下载后才可以导入。");
        var categories=args.TryGetProperty("categories",out var values)?values.Deserialize<string[]>(Json.Options)??[]:["settings","maps"];
        var dialog=new OpenFolderDialog {Title="选择旧客户端文件夹"};if(dialog.ShowDialog(this)!=true)return null;
        var source=dialog.FolderName;var installer=new TransactionalInstaller(settings.Root);using var gate=installer.Acquire(id);var target=installer.InstancePath(id);
        return await Task.Run(()=>PersonalDataImport.Import(source,target,id,categories));
    }
    private async Task<object> Migrate()
    {
        if(games.Count>0||instanceOperations.Count>0||transfers.Count>0||savedDownloads.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
        var dialog=new OpenFolderDialog{Title="选择新的内容目录（需为空目录）"};if(dialog.ShowDialog(this)!=true)return new {Message="已取消迁移"};
        migratingContent=true;
        await settingsGate.WaitAsync();
        var gates=new List<IDisposable>();
        try
        {
            var destination=Path.GetFullPath(dialog.FolderName);var old=Path.GetFullPath(settings.Root);
            var installer=new TransactionalInstaller(old);
            foreach(var id in Routes.Domains.Keys)gates.Add(installer.Acquire(id));
            await Task.Run(()=>PlayerFiles.CopyForMigration(old,destination));
            var previous=settings.Root;
            try{settings.Root=destination;Json.Write(Path.Combine(appData,"settings.json"),settings);}
            catch{settings.Root=previous;throw;}
            return new {Message="目录迁移完成。旧目录保留，可核验后自行清理。",Settings=settings};
        }
        finally{foreach(var gate in gates)gate.Dispose();settingsGate.Release();migratingContent=false;}
    }
    private object CleanCache()
    {
        if(games.Count>0||instanceOperations.Count>0||transfers.Count>0||savedDownloads.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
        var cache=Path.Combine(settings.Root,"cache");long bytes=0;
        if(Directory.Exists(cache))foreach(var file in Directory.EnumerateFiles(cache))
        {
            if(!ContentSecurity.HashPattern().IsMatch(Path.GetFileName(file)))continue;
            ContentSecurity.SafePath(cache,Path.GetFileName(file));bytes+=new FileInfo(file).Length;File.Delete(file);
        }
        return new {Message=$"已清理 {bytes/(1024.0*1024):F1} MiB 缓存。"};
    }
    private object? ContentDialog(string id)
    {
        if(games.ContainsKey(id)||new TransactionalInstaller(settings.Root).IsRunning(id)||instanceOperations.Contains(id)||savedDownloads.ContainsKey(id))throw new InvalidDataException("请先结束当前服务器的游戏和下载任务。");
        var installer=new TransactionalInstaller(settings.Root);var path=installer.InstancePath(id);
        if(!File.Exists(Path.Combine(path,".hub","installed.json")))throw new InvalidDataException("下载客户端后才可以管理模组与资源。");
        new ContentWindow(path,()=>installer.Acquire(id)){Owner=this}.ShowDialog();return null;
    }
    private async Task<object> AccountDialog(bool recovery)
    {
        if(accounts.Current?.Profile.Kind!="hub")throw new InvalidDataException("此功能用于群服账号；微软账号请通过微软账号页面管理。");
        var dialog=new PasswordWindow(recovery){Owner=this};if(dialog.ShowDialog()!=true)return new{Message="已取消"};
        if(recovery)
        {
            var result=await accounts.Authorized("/v1/account/recovery-code",new {LoginName="",Password=dialog.CurrentPassword});
            new RecoveryWindow(result!.Value.GetProperty("recoveryCode").GetString()!){Owner=this}.ShowDialog();return new {Message="恢复码已重新生成"};
        }
        await accounts.Authorized("/v1/account/password",new {CurrentPassword=dialog.CurrentPassword,NewPassword=dialog.NewPassword});await accounts.Logout();Event("account-signed-out",null);return new {Message="密码已修改，请重新登录。"};
    }
    private async Task<object> SkinDialog(JsonElement args)
    {
        if(accounts.Current?.Profile.Kind=="microsoft"){Process.Start(new ProcessStartInfo("https://www.minecraft.net/msaprofile/mygames/editskin"){UseShellExecute=true});return new{Message="已打开微软账号皮肤管理页面"};}
        if(accounts.Current is null)throw new InvalidDataException("请先登录账号。");
        var picker=new OpenFileDialog{Title="选择皮肤",Filter="PNG 皮肤|*.png",Multiselect=false};
        if(picker.ShowDialog(this)!=true)return new{Cancelled=true};
        if(new FileInfo(picker.FileName).Length>SkinImage.MaxBytes)throw new InvalidDataException("皮肤文件不能超过 128 KiB。");
        var bytes=await File.ReadAllBytesAsync(picker.FileName);
        if(bytes.Length<33||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4))!=64||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4)) is not (32 or 64))throw new InvalidDataException("请选择 64×64 或 64×32 的 PNG 皮肤。");
        using var input=new MemoryStream(bytes);
        var bitmap=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0)));
        using var output=new MemoryStream();encoder.Save(output);
        var model=args.TryGetProperty("model",out var value)?value.GetString()??"classic":"classic";
        return await accounts.SaveSkin(new(Convert.ToBase64String(output.ToArray()),model));
    }
    private object ExportDiagnostics()
    {
        var dialog=new SaveFileDialog{Title="导出脱敏诊断",Filter="ZIP 文件|*.zip",FileName="Boshan-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip"};
        if(dialog.ShowDialog(this)!=true)return new{Message="已取消导出"};
        using var zip=ZipFile.Open(dialog.FileName,ZipArchiveMode.Create);
        var logs=Path.Combine(appData,"diagnostic.log");
        if(File.Exists(logs)){var entry=zip.CreateEntry("launcher.log");using var writer=new StreamWriter(entry.Open());writer.Write(File.ReadAllText(logs));}
        var info=zip.CreateEntry("environment.json");using(var writer=new StreamWriter(info.Open()))writer.Write(JsonSerializer.Serialize(new{Launcher=typeof(MainWindow).Assembly.GetName().Version?.ToString(),OS=Environment.OSVersion.VersionString,X64=Environment.Is64BitOperatingSystem,settings.Memory,settings.Width,settings.Height,settings.Concurrency,settings.ProxyMode,Diagnostics=diagnostics.Values},Json.Options));
        return new {Message="诊断日志已导出"};
    }
    private void Log(string command,Exception ex)
    {
        // Do not serialize exception messages, request bodies, game command lines, URLs or account objects.
        try{lock(logGate)File.AppendAllText(Path.Combine(appData,"diagnostic.log"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Command=command,Type=ex.GetType().Name,Diagnostic=NetworkPolicy.Find(ex)},Json.Options)+"\n");}
        catch(Exception loggingError)when(loggingError is IOException or UnauthorizedAccessException){ }
    }
    private void LogPhase(string instance,string phase,string state)
    {
        try{lock(logGate)File.AppendAllText(Path.Combine(appData,"diagnostic.log"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Instance=instance,Phase=phase,State=state},Json.Options)+"\n");}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
    }
    private void LogGraphics(string status,bool success,string message)
    {
        try{lock(logGate)File.AppendAllText(Path.Combine(appData,"diagnostic.log"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Command="graphics-preference",Status=status,Success=success,Message=message},Json.Options)+"\n");}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
    }
    private void RememberDiagnostic(NetworkDiagnostic diagnostic)
    {
        diagnostics[diagnostic.Id]=diagnostic;if(diagnostics.Count>30)diagnostics.Remove(diagnostics.Keys.First());
    }
    private static string Friendly(Exception ex)=>NetworkPolicy.Find(ex) is {} diagnostic?NetworkPolicy.Message(diagnostic):ex is InvalidDataException or FileNotFoundException?ex.Message:ex is UnauthorizedAccessException?"没有写入这个文件夹的权限，请选择其他位置。":ex is IOException?"文件操作未完成，请检查磁盘空间、目录权限或文件占用。":ex is HttpRequestException or TaskCanceledException?"网络连接暂时不可用，请稍后重试。":"操作未完成。可导出诊断日志并联系管理员。";
}
