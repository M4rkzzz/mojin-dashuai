using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Boshan.Launcher;
using CmlLib.Core.Auth;

internal static class ContentPathSmoke
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    private static string[] Arguments(ProcessStartInfo info)
    {
        if(info.ArgumentList.Count>0)return info.ArgumentList.ToArray();
        var block=CommandLineToArgvW("java "+info.Arguments,out var count);
        if(block==IntPtr.Zero)throw new InvalidOperationException("Cannot parse the Windows launch command");
        try{return Enumerable.Range(1,count-1).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(block,i*IntPtr.Size))!).ToArray();}
        finally{LocalFree(block);}
    }
    public static async Task Run(string root)
    {
        root=Path.GetFullPath(root);
        if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar)||!root.Contains("路径验证")||!root.Contains(' '))throw new InvalidDataException("Use the isolated Unicode and space path fixture");
        var results=new List<object>();
        var settings=new LauncherSettings{Root=root};
        foreach(var id in new[]{"m3e","dc2","mb"})
        {
            settings.Memory[id]=1024;
            var manifest=new TransactionalInstaller(root).ReadInstalled(id)!.Manifest;
            using var prepared=await new GameLauncher().Prepare(manifest,settings,MSession.CreateOfflineSession("PathCheck"),new("127.0.0.1","127.0.0.1",9,0));
            var arguments=Arguments(prepared.StartInfo);
            var cp=Array.FindIndex(arguments,value=>value is "-cp" or "-classpath" or "--class-path");
            if(cp<0||cp+2>=arguments.Length)throw new InvalidDataException("Classpath was not present");
            var mainIndex=Array.FindIndex(arguments,value=>value is "net.minecraft.launchwrapper.Launch" or "cpw.mods.bootstraplauncher.BootstrapLauncher" or "top.outlands.foundation.boot.Foundation");
            if(mainIndex<0)throw new InvalidDataException("Unknown game main class");
            var main=arguments[mainIndex];
            foreach(var library in arguments[cp+1].Split(Path.PathSeparator))if(!File.Exists(library))throw new FileNotFoundException("Missing classpath file",library);
            var instance=new TransactionalInstaller(root).InstancePath(id);
            var native=Directory.EnumerateFiles(instance,"*.dll",SearchOption.AllDirectories).FirstOrDefault(path=>Path.GetFileName(path).Equals(id=="m3e"?"lwjgl64.dll":"lwjgl.dll",StringComparison.OrdinalIgnoreCase));
            if(native is null)
            {
                var archive=Directory.EnumerateFiles(Path.Combine(instance,"libraries"),"*natives-windows.jar",SearchOption.AllDirectories).First(path=>Regex.IsMatch(Path.GetFileName(path),@"^lwjgl-[0-9.]+-natives-windows.jar$"));
                using var zip=ZipFile.OpenRead(archive);
                var entry=zip.Entries.First(e=>e.Name=="lwjgl.dll");
                native=ContentSecurity.SafePath(instance,".hub/path-probe/lwjgl.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(native)!);entry.ExtractToFile(native,true);
            }
            var info=new ProcessStartInfo(prepared.StartInfo.FileName){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=instance,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var value in new[]{"-Xmx256m","-Djava.awt.headless=true","-cp",Path.Combine(root,"probe")+Path.PathSeparator+arguments[cp+1],"ContentPathProbe",instance,main,native})info.ArgumentList.Add(value);
            using var process=Process.Start(info)!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try{await process.WaitForExitAsync(timeout.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}
            var stdout=await output;var stderr=await error;
            File.WriteAllText(Path.Combine(root,id+"-probe.log"),stdout+stderr);
            if(process.ExitCode!=0||!stdout.Contains("PATH_CHECK_OK"))throw new InvalidOperationException(id+" Unicode path check failed; inspect the isolated probe log");
            results.Add(new{instance=id,javaMajor=manifest.Runtime.Major,launchArgumentsPrepared=true,classpathFilesPresent=true,gameLoaderClassLoaded=true,nativeLibraryLoaded=true,unicodeFileRoundTrip=true});
        }
        var report=new{passed=true,root,checks=results,gameWindowOpened=false,serverConnected=false,fullGameRenderVerified=false};
        Json.Write(Path.Combine(root,"path-report.json"),report);Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
