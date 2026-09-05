using System.Diagnostics;
using System.IO;
using Boshan.Launcher;
using Microsoft.Win32;

namespace Boshan.Desktop;

internal sealed record UpdateReady(int ProcessId,string Directory);
internal static class UpdateStartup
{
    internal static string? Nonce;
    internal static string DataRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher","updates");
    internal const string ProgramDirectoryVariable="MOJIN_INSTALL_DIRECTORY";
    internal static string ProgramDirectory
    {
        get
        {
            var current=Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if(!current.StartsWith(Path.GetFullPath(DataRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return current;
            var inherited=Environment.GetEnvironmentVariable(ProgramDirectoryVariable);
            if(!string.IsNullOrWhiteSpace(inherited)&&Path.IsPathFullyQualified(inherited)&&File.Exists(Path.Combine(inherited,"MojinDashuai.Launcher.exe")))return Path.GetFullPath(inherited);
            // beta.6 and earlier handoffs did not pass the original program directory.
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MojinDashuai.Launcher_is1");
            var installed=key?.GetValue("InstallLocation") as string;
            return !string.IsNullOrWhiteSpace(installed)&&Path.IsPathFullyQualified(installed)&&File.Exists(Path.Combine(installed,"MojinDashuai.Launcher.exe"))?Path.GetFullPath(installed):current;
        }
    }
    internal static void MarkReady()
    {
        if(Nonce is null)return;
        Json.Write(ContentSecurity.SafePath(DataRoot,"ready/"+Nonce+".json"),new UpdateReady(Environment.ProcessId,AppContext.BaseDirectory));
        Nonce=null;
    }
    internal static async Task<bool> Start(LauncherUpdates updates,PreparedLauncher prepared)
    {
        var nonce=Guid.NewGuid().ToString("N");
        var readyFile=ContentSecurity.SafePath(updates.Root,"ready/"+nonce+".json");
        var info=new ProcessStartInfo(prepared.Executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=prepared.Directory};
        info.Environment[ProgramDirectoryVariable]=ProgramDirectory;
        info.ArgumentList.Add("--update-ready");info.ArgumentList.Add(nonce);
        Process? started;
        try{started=Process.Start(info);}
        catch(System.ComponentModel.Win32Exception){updates.Reject(prepared);return false;}
        if(started is null){updates.Reject(prepared);return false;}
        using var process=started;
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(45);
            while(DateTime.UtcNow<deadline&&!process.HasExited)
            {
                if(File.Exists(readyFile))
                {
                    var ready=Json.Read<UpdateReady>(readyFile);
                    if(ready.ProcessId==process.Id&&Path.GetFullPath(ready.Directory).TrimEnd(Path.DirectorySeparatorChar).Equals(prepared.Directory,StringComparison.OrdinalIgnoreCase))
                    {updates.Activate(prepared);return true;}
                }
                await Task.Delay(150);
            }
            // Only this newly created launcher is asked to close. Games and the
            // current launcher remain running if the replacement cannot open.
            if(!process.HasExited)process.CloseMainWindow();
            updates.Reject(prepared);
            return false;
        }
        finally{if(File.Exists(readyFile))File.Delete(readyFile);}
    }
}
