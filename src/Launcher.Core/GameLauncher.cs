using System.Diagnostics;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;

namespace Boshan.Launcher;

public sealed class GameLauncher
{
    public static MinecraftLauncher FromInstalledFiles(string instance)
    {
        var path=new MinecraftPath(instance);
        var parameters=MinecraftLauncherParameters.CreateDefault(path);
        parameters.VersionLoader=new LocalJsonVersionLoader(path);
        return new MinecraftLauncher(parameters);
    }
    public async Task<Process> Launch(PackManifest manifest,LauncherSettings settings,MSession session,RouteEndpoint route,CancellationToken token=default)
    {
        var process=await Prepare(manifest,settings,session,route,token).ConfigureAwait(false);process.Start();return process;
    }
    public async Task<Process> Prepare(PackManifest manifest,LauncherSettings settings,MSession session,RouteEndpoint route,CancellationToken token=default)
    {
        await Task.Run(()=>{ContentSecurity.Validate(manifest);settings.Validate();},token).ConfigureAwait(false);
        var installer=new TransactionalInstaller(settings.Root);var instance=installer.InstancePath(manifest.Instance);
        var java=string.IsNullOrEmpty(settings.Java[manifest.Instance])?ContentSecurity.SafePath(RuntimeManager.RuntimeRoot(settings.Root,manifest.Runtime),manifest.Runtime.JavaPath):settings.Java[manifest.Instance];
        await RuntimeManager.Validate(java,manifest.Runtime.Major,token).ConfigureAwait(false);
        await Task.Run(()=>MapIdentity.Prepare(instance,manifest.Instance),token).ConfigureAwait(false);
        await Task.Run(()=>NewModDefaults.Prepare(instance,manifest),token).ConfigureAwait(false);
        // Skin services are optional. Their availability never gates game startup.
        ThirdPartySkins.ConfigureInstance(instance,settings.SkinSource);
        if(manifest.Instance=="mb")CleanroomAdapter.ValidatePrepared(instance,manifest);
        var launcher=FromInstalledFiles(instance);
        var adapter=manifest.Instance switch {"m3e" or "vw"=>"mods/mojin-autoconnect-1.7.10-0.1.0.jar","mb"=>"mods/mojin-autoconnect-cleanroom-0.1.0.jar",_=>null};
        var delayedJoin=adapter is not null;
        if(delayedJoin&&!manifest.Files.Any(f=>f.Path==adapter))throw new InvalidDataException("服务器连接组件尚未安装，请先更新客户端。");
        if(delayedJoin&&(!System.Text.RegularExpressions.Regex.IsMatch(route.Host,"^[A-Za-z0-9.-]+$")||route.Port is <1 or >65535))throw new InvalidDataException("服务器地址无效。");
        var jvm=new List<MArgument>{MArgument.FromCommandLine(JavaLaunchArguments.ForInstance(manifest.Instance,manifest.Runtime.Major,settings.Jvm[manifest.Instance],Environment.ProcessorCount))};
        if(delayedJoin)jvm.Add(MArgument.FromCommandLine($"-Dmojin.join.host={route.Host} -Dmojin.join.port={route.Port}"));
        if(OperatingSystem.IsWindows()&&manifest.Runtime.Major>=16)
        {
            // Windows packaged-app temp redirection can break AF_UNIX selector
            // wakeup sockets. A relative path also avoids the socket path limit.
            Directory.CreateDirectory(ContentSecurity.SafePath(instance,".hub/socket-temp"));
            jvm.Add(MArgument.FromCommandLine("-Djdk.net.unixdomain.tmpdir=.hub/socket-temp"));
        }
        var options=new MLaunchOption {
            Session=session,JavaPath=java,MaximumRamMb=settings.Memory[manifest.Instance],MinimumRamMb=Math.Min(2048,settings.Memory[manifest.Instance]),
            ScreenWidth=settings.Width,ScreenHeight=settings.Height,FullScreen=settings.Fullscreen,ServerIp=delayedJoin?null:route.Host,ServerPort=route.Port,
            GameLauncherName="MojinDashuai",GameLauncherVersion="0.1.0",
            ExtraJvmArguments=jvm
        };
        // Installation is performed exclusively from the signed file inventory. CmlLib only builds the launch process.
        var process=await launcher.BuildProcessAsync(manifest.LaunchVersion,options).ConfigureAwait(false);
        process.StartInfo.WorkingDirectory=instance;
        process.StartInfo.CreateNoWindow=true; process.StartInfo.UseShellExecute=false;
        return process;
    }
}
public static class CleanroomAdapter
{
    public static async Task CompletePrepared(string instance,string launchVersion)
    {
        var jsonPath=ContentSecurity.SafePath(instance,$"versions/{launchVersion}/{launchVersion}.json");
        using var document=JsonDocument.Parse(File.ReadAllText(jsonPath));
        foreach(var library in document.RootElement.GetProperty("libraries").EnumerateArray())
        {
            if(!library.TryGetProperty("downloads",out var downloads)||!downloads.TryGetProperty("artifact",out var artifact)||!artifact.TryGetProperty("path",out var downloadPath))continue;
            var destination=ContentSecurity.SafePath(instance,"libraries/"+downloadPath.GetString());
            if(File.Exists(destination))continue;
            var coordinate=library.GetProperty("name").GetString()!.Split(':');
            if(coordinate.Length<3)throw new InvalidDataException("Cleanroom 依赖坐标无效。");
            var filename=coordinate[1]+"-"+coordinate[2]+(coordinate.Length>3?"-"+coordinate[3]:"")+".jar";
            var relative="libraries/"+coordinate[0].Replace('.','/')+"/"+coordinate[1]+"/"+coordinate[2]+"/"+filename;
            var source=ContentSecurity.SafePath(instance,relative);
            if(!File.Exists(source))continue;
            await using var stream=File.OpenRead(source);var hash=Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream));
            if(!hash.Equals(artifact.GetProperty("sha1").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Cleanroom 依赖校验失败。");
            TransactionalInstaller.AtomicCopy(source,destination);
        }
        var vanilla=ContentSecurity.SafePath(instance,"versions/1.12.2/1.12.2.jar");
        var client=ContentSecurity.SafePath(instance,$"versions/{launchVersion}/{launchVersion}.jar");
        if(!File.Exists(client))
        {
            await using var stream=File.OpenRead(vanilla);var hash=Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream));
            if(!hash.Equals(document.RootElement.GetProperty("downloads").GetProperty("client").GetProperty("sha1").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Minecraft 原版文件校验失败。");
            TransactionalInstaller.AtomicCopy(vanilla,client);
        }
    }
    public static void ValidatePrepared(string instance,PackManifest manifest)
    {
        if(manifest.Loader!="cleanroom"||manifest.Runtime.Major!=25)throw new InvalidDataException("肉丸工艺仅支持 Cleanroom + Java 25。");
        var versionFile=ContentSecurity.SafePath(instance,$"versions/{manifest.LaunchVersion}/{manifest.LaunchVersion}.json");
        using var version=JsonDocument.Parse(File.ReadAllText(versionFile));
        var text=version.RootElement.GetRawText();
        if(!text.Contains("cleanroom",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("三服启动配置缺少 Cleanroom 加载器。");
        if(!manifest.Files.Any(x=>x.Path.Contains("cleanroom",StringComparison.OrdinalIgnoreCase)&&x.Path.EndsWith(".jar",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("三服 Cleanroom 文件尚未准备完成。");
    }
}
