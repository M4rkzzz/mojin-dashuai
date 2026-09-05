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
    private LauncherSettings settings=new();
    private Accounts accounts=null!;
    private CatalogClient catalog=null!;
    private bool initialized;
    private readonly SemaphoreSlim settingsGate=new(1);
    public MainWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await Initialize();
        Closing+=(_,e)=>{if(games.Count>0){e.Cancel=true;Hide();}};
    }
    private async Task Initialize()
    {
        try
        {
            Directory.CreateDirectory(appData);
            var config=Json.Read<AppConfig>(Path.Combine(AppContext.BaseDirectory,"launcher.json"));
            var settingsPath=Path.Combine(appData,"settings.json");settings=File.Exists(settingsPath)?Json.Read<LauncherSettings>(settingsPath):new();settings.Validate();
            accounts=new(new Vault(appData),config.Api,config.MicrosoftClientId);
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
            core.SetVirtualHostNameToFolderMapping("app.boshan.local",Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="app.boshan.local")e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.WebMessageReceived+=HandleMessage;
            core.NavigationCompleted+=(_,_)=>Loading.Visibility=Visibility.Collapsed;
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
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {request.Id,Ok=true,Result=result},Json.Options));
        }
        catch(Exception ex)
        {
            if(request is not null)Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {request.Id,Ok=false,Error=Friendly(ex)},Json.Options));
            Log(request?.Command??"bridge",ex);
        }
    }
    private async Task<object?> Dispatch(string command,JsonElement args)
    {
        string Id(){var id=args.GetProperty("instance").GetString()!;if(!Routes.Domains.ContainsKey(id))throw new InvalidDataException("未知服务器。");return id;}
        switch(command)
        {
            case "bootstrap": return new {Profile=await accounts.Restore(),Settings=settings,Installs=Routes.Domains.Keys.Select(id=>new {Id=id,Pack=new TransactionalInstaller(settings.Root).ReadInstalled(id)}).Where(x=>x.Pack is not null).ToDictionary(x=>x.Id,x=>new {Version=x.Pack!.Manifest.Version,State="installed"})};
            case "auth.login":return await accounts.Login("login",args);
            case "auth.register":return await accounts.Login("register",args);
            case "auth.recover":return await accounts.Recover(args);
            case "auth.microsoft":return await accounts.MicrosoftLogin();
            case "auth.logout":await accounts.Logout();return null;
            case "settings.save":
                await settingsGate.WaitAsync();
                try
                {
                    var next=args.Deserialize<LauncherSettings>(Json.Options)!;next.Validate();
                    if(!Path.GetFullPath(next.Root).Equals(Path.GetFullPath(settings.Root),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请通过目录迁移功能更换内容目录。");
                    foreach(var id in Routes.Domains.Keys)if(next.Java[id]!=settings.Java[id]&&!string.IsNullOrEmpty(next.Java[id]))await RuntimeManager.Validate(next.Java[id],id=="m3e"?8:id=="dc2"?17:25);
                    settings=next;Json.Write(Path.Combine(appData,"settings.json"),settings);return null;
                }
                finally{settingsGate.Release();}
            case "routes.probe":return (await Routes.ProbeAll(Id())).Select(x=>x.Latency).ToArray();
            case "instance.install":
            {
                var id=Id();await Install(id);if(!pendingPacks.ContainsKey(id))await Launch(id);return null;
            }
            case "instance.repair":await Install(Id());return null;
            case "instance.rollback":await Rollback(Id());return null;
            case "download.pause":
            {
                var id=Id();if(transfers.TryGetValue(id,out var transfer))transfer.Cancel();return null;
            }
            case "download.resume":
            {
                var id=Id();if(!pendingPacks.TryGetValue(id,out var pack))throw new InvalidDataException("没有可续传的任务。");await Transfer(pack);return null;
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
            case "account.skin":return SkinDialog();
            case "window.minimize":WindowState=WindowState.Minimized;return null;
            case "window.maximize":WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;return null;
            case "window.close":Close();return null;
            default:throw new InvalidDataException("启动器不支持此操作。");
        }
    }
    private async Task Install(string id)
    {
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
        await Transfer(await catalog.GetManifest(id,release));
    }
    private async Task Launch(string id)
    {
        if(games.ContainsKey(id))throw new InvalidDataException("此世界已在运行。");
        var session=await accounts.GameSession();var installer=new TransactionalInstaller(settings.Root);var gate=installer.Acquire(id);
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
    private void Event(string name,object data)=>Dispatcher.Invoke(()=>Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {Event=name,Data=data},Json.Options)));
    private void OpenFolder(string path){Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private async Task<object> Import(string id)
    {
        var dialog=new OpenFolderDialog {Title="选择旧客户端的游戏实例目录"};if(dialog.ShowDialog(this)!=true)return new {Message="已取消导入"};
        var source=dialog.FolderName;var installer=new TransactionalInstaller(settings.Root);using var gate=installer.Acquire(id);var target=installer.InstancePath(id);
        if(Path.GetFullPath(source).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("旧客户端与当前实例不能使用同一个目录。");
        var count=0;
        // Only known player-owned data is imported. Old launchers, authentication caches and FRP files are never copied.
        foreach(var name in new[]{"saves","screenshots","journeymap","XaeroWaypoints","XaeroWorldMap","resourcepacks","shaderpacks","options.txt","optionsof.txt"})
        {
            var from=ContentSecurity.SafePath(source,name);if(!File.Exists(from)&&!Directory.Exists(from))continue;
            var files=File.Exists(from)?new[]{from}:Directory.GetFiles(from,"*",SearchOption.AllDirectories);
            foreach(var file in files)
            {
                var relative=Path.GetRelativePath(source,file).Replace('\\','/');ContentSecurity.SafePath(source,relative);
                var dest=ContentSecurity.SafePath(target,relative);
                if(File.Exists(dest))dest=ContentSecurity.SafePath(target,".hub/import-conflicts/"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"/"+relative);
                await Task.Run(()=>TransactionalInstaller.AtomicCopy(file,dest));count++;
            }
        }
        return new {Message=$"已导入 {count} 个个人文件。游戏模组由对应服务器的正式清单安装。"};
    }
    private async Task<object> Migrate()
    {
        if(games.Count>0||transfers.Count>0)throw new InvalidDataException("请先结束游戏和下载任务。");
        var dialog=new OpenFolderDialog{Title="选择新的内容目录（需为空目录）"};if(dialog.ShowDialog(this)!=true)return new {Message="已取消迁移"};
        var destination=Path.GetFullPath(dialog.FolderName);var old=Path.GetFullPath(settings.Root);
        if(destination.Equals(old,StringComparison.OrdinalIgnoreCase)||destination.StartsWith(old+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.EnumerateFileSystemEntries(destination).Any())throw new InvalidDataException("请选择独立的空目录。");
        if(Directory.Exists(old))foreach(var file in Directory.EnumerateFiles(old,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(old,file).Replace('\\','/');ContentSecurity.SafePath(old,relative);
            if(relative.EndsWith("run.lock",StringComparison.OrdinalIgnoreCase))continue;
            await Task.Run(()=>TransactionalInstaller.AtomicCopy(file,ContentSecurity.SafePath(destination,relative)));
        }
        settings.Root=destination;Json.Write(Path.Combine(appData,"settings.json"),settings);
        return new {Message="目录迁移完成。旧目录保留，可核验后自行清理。"};
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
        await accounts.Authorized("/v1/account/password",new {CurrentPassword=dialog.CurrentPassword,NewPassword=dialog.NewPassword});await accounts.Logout();return new {Message="密码已修改，请退出当前界面后重新登录。"};
    }
    private object SkinDialog()
    {
        if(accounts.Current?.Profile.Kind=="microsoft"){Process.Start(new ProcessStartInfo("https://www.minecraft.net/msaprofile/mygames/editskin"){UseShellExecute=true});return new{Message="已打开微软账号皮肤管理页面"};}
        throw new InvalidDataException("群服皮肤功能正在逐服验证客户端模组，验证完成后开放。");
    }
    private object ExportDiagnostics()
    {
        var dialog=new SaveFileDialog{Title="导出脱敏诊断",Filter="ZIP 文件|*.zip",FileName="Boshan-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip"};
        if(dialog.ShowDialog(this)!=true)return new{Message="已取消导出"};
        using var zip=ZipFile.Open(dialog.FileName,ZipArchiveMode.Create);
        var logs=Path.Combine(appData,"diagnostic.log");
        if(File.Exists(logs)){var entry=zip.CreateEntry("launcher.log");using var writer=new StreamWriter(entry.Open());writer.Write(File.ReadAllText(logs));}
        var info=zip.CreateEntry("environment.json");using(var writer=new StreamWriter(info.Open()))writer.Write(JsonSerializer.Serialize(new{Launcher="0.1.0",OS=Environment.OSVersion.VersionString,X64=Environment.Is64BitOperatingSystem,settings.Memory,settings.Width,settings.Height,settings.Concurrency},Json.Options));
        return new {Message="诊断日志已导出"};
    }
    private void Log(string command,Exception ex)
    {
        // Do not serialize exception messages, request bodies, game command lines, URLs or account objects.
        File.AppendAllText(Path.Combine(appData,"diagnostic.log"),$"{DateTimeOffset.UtcNow:O} {command} {ex.GetType().Name}\n");
    }
    private static string Friendly(Exception ex)=>ex is InvalidDataException or FileNotFoundException?ex.Message:ex is IOException?"文件操作未完成，请检查磁盘空间、目录权限或文件占用。":ex is HttpRequestException or TaskCanceledException?"网络连接暂时不可用，请稍后重试。":"操作未完成。可导出诊断日志并联系管理员。";
}
