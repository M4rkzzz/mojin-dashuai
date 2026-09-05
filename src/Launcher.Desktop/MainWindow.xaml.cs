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
public partial class MainWindow : Window
{
    private readonly string appData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
    private readonly Dictionary<string,Process> games=[];
    private readonly Dictionary<string,CancellationTokenSource> transfers=[];
    private readonly Dictionary<string,TransferProgress> transferProgress=[];
    private readonly Dictionary<string,PackManifest> pendingPacks=[];
    private readonly HashSet<string> launchAfterDownload=[];
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
    private object launcherUpdate=new {Phase="idle",Version=(string?)null,Downloaded=0L,Total=0L};
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
            var settingsPath=Path.Combine(appData,"settings.json");settings=ContentDirectorySetup.LoadSettings(settingsPath);settings.Validate();
            var microsoftId=config.MicrosoftClientId??"";
            accounts=new(new Vault(appData),config.Api,microsoftId.Trim(),()=>new MicrosoftWebUi(this,Path.Combine(appData,"MicrosoftAuth")));
            catalog=new(config.Api,config.PublicKeys,Path.Combine(appData,"catalog-checkpoint.json"));
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
            core.NavigationCompleted+=(_,_)=>{Loading.Visibility=Visibility.Collapsed;Event("window-state",new {Maximized=WindowState==WindowState.Maximized});_=CheckLauncherUpdate();};
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
            if(initialized&&request is not null)Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {request.Id,Ok=false,Error=Friendly(ex)},Json.Options));
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
            case "bootstrap": UpdateStartup.MarkReady();return new {Profile=await accounts.Restore(),Settings=settings,LauncherUpdate=launcherUpdate,WindowMaximized=WindowState==WindowState.Maximized,Installs=Routes.Domains.Keys.Select(id=>new {Id=id,Pack=new TransactionalInstaller(settings.Root).ReadInstalled(id)}).Where(x=>x.Pack is not null).ToDictionary(x=>x.Id,x=>new {Version=x.Pack!.Manifest.Version,State="installed"})};
            case "launcher.update.check":return await CheckLauncherUpdate(true);
            case "launcher.update.status":return launcherUpdate;
            case "launcher.update.restart":
                if(checkingLauncherUpdate||games.Count>0||transfers.Count>0||pendingPacks.Count>0||microsoftLogin is not null)throw new InvalidDataException("请先结束游戏、下载或登录任务。");
                var update=await launcherUpdates.Ready(AppContext.BaseDirectory,typeof(App).Assembly.GetName().Version!);
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
                    if(games.Count>0||transfers.Count>0||pendingPacks.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
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
                    foreach(var id in Routes.Domains.Keys)if(next.Java[id]!=settings.Java[id]&&!string.IsNullOrEmpty(next.Java[id]))await RuntimeManager.Validate(next.Java[id],id=="m3e"?8:id=="dc2"?17:25);
                    Json.Write(Path.Combine(appData,"settings.json"),next);settings=next;return null;
                }
                finally{settingsGate.Release();}
            case "routes.probe":return (await Routes.ProbeAll(Id())).Select(x=>x.Latency).ToArray();
            case "instance.install":
            {
                var id=Id();launchAfterDownload.Add(id);
                try{await Install(id);}finally{if(!pendingPacks.ContainsKey(id))launchAfterDownload.Remove(id);}
                if(!pendingPacks.ContainsKey(id))await Launch(id,false);return null;
            }
            case "instance.repair":await Install(Id());return null;
            case "instance.rollback":await Rollback(Id());return null;
            case "download.pause":
            {
                var id=Id();if(transfers.TryGetValue(id,out var transfer))transfer.Cancel();return null;
            }
            case "download.resume":
            {
                var id=Id();if(!pendingPacks.TryGetValue(id,out var pack))throw new InvalidDataException("没有可续传的任务。");await Transfer(pack);
                if(!pendingPacks.ContainsKey(id)&&launchAfterDownload.Remove(id))await Launch(id,false);return null;
            }
            case "instance.launch":await Launch(Id());return null;
            case "instance.folder":OpenFolder(new TransactionalInstaller(settings.Root).InstancePath(Id()));return null;
            case "instance.import":return await Import(Id());
            case "directory.migrate":return await Migrate();
            case "cache.clean":return CleanCache();
            case "content.manage":return ContentDialog(Id());
            case "diagnostics.export":return ExportDiagnostics();
            case "account.password":return await AccountDialog(false);
            case "account.recovery":return await AccountDialog(true);
            case "account.avatar":return await accounts.Skin();
            case "account.skin":return await SkinDialog(args);
            case "window.minimize":WindowState=WindowState.Minimized;return null;
            case "window.maximize":WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;return null;
            case "window.close":Close();return null;
            default:throw new InvalidDataException("启动器不支持此操作。");
        }
    }
    private async Task Install(string id)
    {
        pendingRollbackPins.Remove(id);
        await accounts.GameSession();
        var directory=await catalog.Fetch();var server=directory.Servers.SingleOrDefault(s=>s.Id==id);
        if(server?.Release is null)throw new InvalidDataException("这个世界正在完成安装与入服验收，正式内容尚未开放。");
        await Transfer(await catalog.GetManifest(id,server.Release));
    }
    private async Task Transfer(PackManifest pack)
    {
        if(transfers.ContainsKey(pack.Instance))throw new InvalidDataException("此世界已有下载任务。");
        pendingPacks[pack.Instance]=pack;using var cancellation=new CancellationTokenSource();transfers[pack.Instance]=cancellation;
        using var downloader=new Downloader(Path.Combine(settings.Root,"cache"),settings);
        var progress=new Progress<TransferProgress>(p=>{transferProgress[p.Instance]=p;Event("progress",p);});
        try
        {
            await new TransactionalInstaller(settings.Root).Install(pack,downloader,settings.Concurrency,progress,cancellation.Token);
            if(pendingRollbackPins.Remove(pack.Instance,out var pin))Json.Write(Path.Combine(new TransactionalInstaller(settings.Root).InstancePath(pack.Instance),".hub","rollback-pin.json"),pin);
            pendingPacks.Remove(pack.Instance);Event("installed",new{Instance=pack.Instance});
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested)
        {
            var last=transferProgress.GetValueOrDefault(pack.Instance)??new(pack.Instance,"下载已暂停",0,pack.Files.Sum(f=>f.Size),0);
            Event("progress",last with {Paused=true,BytesPerSecond=0});
        }
        finally{transfers.Remove(pack.Instance);}
    }
    private async Task Rollback(string id)
    {
        var installer=new TransactionalInstaller(settings.Root);var oldPath=Path.Combine(installer.InstancePath(id),".hub","previous.json");
        if(!File.Exists(oldPath))throw new InvalidDataException("没有可回退的上一版本。");
        var old=Json.Read<InstalledPack>(oldPath);var directory=await catalog.Fetch();var server=directory.Servers.Single(s=>s.Id==id);
        var release=server.Rollbacks.SingleOrDefault(r=>r.Version==old.Manifest.Version&&r.Compatibility==server.Release?.Compatibility);
        if(release is null)throw new InvalidDataException("上一版本未被列为当前服务器可用的回退版本。");
        var pack=await catalog.GetManifest(id,release);
        launchAfterDownload.Remove(id);
        pendingRollbackPins[id]=new(pack.Version,pack.Sequence,server.Release!.Sequence);
        await Transfer(pack);
    }
    private async Task<object> CheckLauncherUpdate(bool retry=false)
    {
        if(checkingLauncherUpdate||restartingLauncher)return launcherUpdate;
        checkingLauncherUpdate=true;
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
                if(retry)launcherUpdates.Retry(release);
                if(launcherUpdates.HasFailed(release))throw new InvalidDataException("新版启动失败，已保留原版本。可以重新检查更新。");
                if(Version.Parse(release.Version.Split('-')[0])>=new Version(typeof(App).Assembly.GetName().Version!.ToString(3)))
                {
                    State("downloading",release.Version,total:release.Archive.Size);
                    using var downloader=new Downloader(Path.Combine(launcherUpdates.Root,"cache"),settings);
                    long received=0,last=0;
                    await launcherUpdates.Prepare(envelope,downloader,bytes=>{received+=bytes;var now=Environment.TickCount64;if(now-last>500){last=now;State("downloading",release.Version,received,release.Archive.Size);}});
                }
            }
            var ready=await launcherUpdates.Ready(AppContext.BaseDirectory,typeof(App).Assembly.GetName().Version!);
            State(ready is null?"current":"ready",ready?.Release.Version);
        }
        catch(Exception ex){State("failed",error:Friendly(ex));Log("launcher-update",ex);}
        finally{checkingLauncherUpdate=false;}
        return launcherUpdate;
    }
    private async Task Launch(string id,bool checkForUpdates=true)
    {
        if(games.ContainsKey(id))throw new InvalidDataException("此世界已在运行。");
        if(pendingPacks.ContainsKey(id))throw new InvalidDataException("请先完成当前下载。");
        await accounts.GameSession();var installer=new TransactionalInstaller(settings.Root);
        if(checkForUpdates)
        {
            PackManifest installed;
            using(var preparation=installer.Acquire(id))
            {
                installer.Recover(id);installed=(installer.ReadInstalled(id)??throw new InvalidDataException("请先安装这个世界。")).Manifest;
            }
            var pinPath=Path.Combine(installer.InstancePath(id),".hub","rollback-pin.json");
            var pin=File.Exists(pinPath)?Json.Read<RollbackPin>(pinPath):null;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var release=await LaunchUpdates.Check(installed,pin,catalog.Fetch,timeout.Token);
            if(release is not null)
            {
                var updated=await catalog.GetManifest(id,release);
                pendingRollbackPins.Remove(id);launchAfterDownload.Add(id);await Transfer(updated);
                if(pendingPacks.ContainsKey(id))return;
                launchAfterDownload.Remove(id);
            }
        }
        // Installation can outlive an access token; obtain a current session just before launching.
        var session=await accounts.GameSession();var gate=installer.Acquire(id);
        try
        {
            installer.Recover(id);var pack=installer.ReadInstalled(id)??throw new InvalidDataException("请先安装这个世界。");
            var route=await Routes.Select(id,settings.SelectedRoutes[id]);
            var process=await new GameLauncher().Launch(pack.Manifest,settings,session,route);
            games[id]=process;Json.Write(Path.Combine(installer.InstancePath(id),".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime()));
            _=ObserveGame(id,process,gate);
            if(settings.WindowBehavior=="minimize")WindowState=WindowState.Minimized;
            if(settings.WindowBehavior=="hide")Hide();
        }
        catch{gate.Dispose();throw;}
    }
    private async Task ObserveGame(string id,Process process,FileStream gate)
    {
        await process.WaitForExitAsync();var code=process.ExitCode;
        await Dispatcher.InvokeAsync(()=>{games.Remove(id);gate.Dispose();process.Dispose();Show();if(code!=0)Event("error","游戏已退出。可导出诊断日志帮助定位问题。");});
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
    private async Task<object> Import(string id)
    {
        var dialog=new OpenFolderDialog {Title="选择旧客户端的游戏实例目录"};if(dialog.ShowDialog(this)!=true)return new {Message="已取消导入"};
        var source=dialog.FolderName;var installer=new TransactionalInstaller(settings.Root);using var gate=installer.Acquire(id);var target=installer.InstancePath(id);
        var count=await Task.Run(()=>PlayerFiles.Import(source,target));
        return new {Message=$"已导入 {count} 个个人文件。游戏模组由对应服务器的正式清单安装。"};
    }
    private async Task<object> Migrate()
    {
        if(games.Count>0||transfers.Count>0||pendingPacks.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
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
        if(games.Count>0||transfers.Count>0||pendingPacks.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
        var cache=Path.Combine(settings.Root,"cache");long bytes=0;
        if(Directory.Exists(cache))foreach(var file in Directory.EnumerateFiles(cache))
        {
            if(!ContentSecurity.HashPattern().IsMatch(Path.GetFileName(file)))continue;
            ContentSecurity.SafePath(cache,Path.GetFileName(file));bytes+=new FileInfo(file).Length;File.Delete(file);
        }
        return new {Message=$"已清理 {bytes/(1024.0*1024):F1} MiB 缓存。"};
    }
    private object ContentDialog(string id)
    {
        var installer=new TransactionalInstaller(settings.Root);var path=installer.InstancePath(id);
        new ContentWindow(path,()=>installer.Acquire(id)){Owner=this}.ShowDialog();return new {Message="内容管理已关闭"};
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
        var info=zip.CreateEntry("environment.json");using(var writer=new StreamWriter(info.Open()))writer.Write(JsonSerializer.Serialize(new{Launcher=typeof(MainWindow).Assembly.GetName().Version?.ToString(),OS=Environment.OSVersion.VersionString,X64=Environment.Is64BitOperatingSystem,settings.Memory,settings.Width,settings.Height,settings.Concurrency},Json.Options));
        return new {Message="诊断日志已导出"};
    }
    private void Log(string command,Exception ex)
    {
        // Do not serialize exception messages, request bodies, game command lines, URLs or account objects.
        File.AppendAllText(Path.Combine(appData,"diagnostic.log"),$"{DateTimeOffset.UtcNow:O} {command} {ex.GetType().Name}\n");
    }
    private static string Friendly(Exception ex)=>ex is InvalidDataException or FileNotFoundException?ex.Message:ex is UnauthorizedAccessException?"没有写入这个文件夹的权限，请选择其他位置。":ex is IOException?"文件操作未完成，请检查磁盘空间、目录权限或文件占用。":ex is HttpRequestException or TaskCanceledException?"网络连接暂时不可用，请稍后重试。":"操作未完成。可导出诊断日志并联系管理员。";
}
