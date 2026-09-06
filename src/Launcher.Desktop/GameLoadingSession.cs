using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Boshan.Launcher;

namespace Boshan.Desktop;

/// <summary>Per-process loading presentation. Hiding is reversible; a lost reporter reveals the game.</summary>
public sealed class GameLoadingSession : IDisposable
{
    private readonly string directory,session=Guid.NewGuid().ToString("N");
    private readonly GameLoadingWindow window;
    private readonly DispatcherTimer timer;
    private readonly HashSet<IntPtr> hidden=[];
    private readonly HashSet<IntPtr> restoring=[];
    private readonly WinEvent callback;
    private Process? game;
    private IntPtr hook;
    private bool revealed,disposed,reading;
    private long lastReport=Environment.TickCount64;
    private DateTime lastWrite;
    public GameLoadingOptions Options {get;}
    public event Action<GameLoadingFrame>? FrameObserved;
    public event Action? GameRevealed;
    public string? RevealReason {get;private set;}
    public bool GameWindowVisible {get;private set;}
    public int HiddenWindowCount=>hidden.Count(handle=>IsWindow(handle)&&!IsWindowVisible(handle));
    public GameLoadingSession(string instancePath,string instance,bool reducedMotion=false)
    {
        directory=Path.Combine(instancePath,".hub","loading");Directory.CreateDirectory(directory);
        using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mojin.LoadingAgent")??throw new FileNotFoundException("加载界面组件缺失。");
        using var bytes=new MemoryStream();resource.CopyTo(bytes);var data=bytes.ToArray();var hash=Convert.ToHexStringLower(SHA256.HashData(data));
        var agent="agent-"+hash+".jar";var path=Path.Combine(directory,agent);
        if(!File.Exists(path)||!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(data)))
        {
            var temporary=path+"."+session+".tmp";File.WriteAllBytes(temporary,data);File.Move(temporary,path,true);
        }
        Options=new(".hub/loading/"+agent,session);
        window=new(instance,reducedMotion);window.RevealRequested+=()=>RevealGame(true);
        window.Closed+=(_,_)=>{if(!revealed)RevealGame(false,"closed");};
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(350),DispatcherPriority.Background,async(_,_)=>await Poll(),window.Dispatcher);timer.Stop();
        callback=(_,_,handle,objectId,_,_,_)=>{if(objectId==0&&!revealed)Inspect(handle);};
    }
    public void Show()=>window.Show();
    public void Configure(Process process)
    {
        // Do not put SW_HIDE in STARTUPINFO: Windows can apply it to a later first ShowWindow
        // call and hide the renderer again after our handoff. Hide only observed renderer HWNDs.
        process.StartInfo.WindowStyle=ProcessWindowStyle.Normal;
        process.StartInfo.RedirectStandardOutput=true;process.StartInfo.RedirectStandardError=true;
        process.OutputDataReceived+=Output;process.ErrorDataReceived+=Output;
    }
    public void Attach(Process process)
    {
        game=process;lastReport=Environment.TickCount64;
        process.BeginOutputReadLine();process.BeginErrorReadLine();
        if(revealed){timer.Start();TryRestoreWindows();return;}
        // Watch only the exact JVM launched for this instance, never another player's game/application.
        hook=SetWinEventHook(0x8002,0x8002,IntPtr.Zero,callback,(uint)process.Id,0,0); // EVENT_OBJECT_SHOW, out of context
        ScanWindows();timer.Start();
    }
    public static bool IsConnectingLine(string line)
    {
        // CmlLib configures Log4j XML output for the native process; latest.log is plain text.
        // Unwrap only a Message CDATA record, without parsing external entities or the whole log stream.
        var start=line.IndexOf("<log4j:Message><![CDATA[",StringComparison.Ordinal);
        if(start>=0)
        {
            start+="<log4j:Message><![CDATA[".Length;
            var end=line.IndexOf("]]></log4j:Message>",start,StringComparison.Ordinal);
            if(end<0)return false;
            line=line[start..end];
        }
        line=line.Trim();
        return line=="[Mojin] Client initialization complete; connecting to selected server."
            ||(line.Contains("uk.boshan.mojin.MojinAutoConnect:onClientTick:",StringComparison.Ordinal)&&line.EndsWith("[Mojin] Client initialization complete; connecting to selected server.",StringComparison.Ordinal))
            ||Regex.IsMatch(line,@"^(?:.*\]\s*:\s*)?Connecting to [A-Za-z0-9.:-]+, \d{1,5}$",RegexOptions.CultureInvariant);
    }
    private void Output(object sender,DataReceivedEventArgs e)
    {
        // Consume pipes continuously without retaining raw output: it can contain session credentials.
        if(!revealed&&!disposed&&e.Data is not null&&(e.Data.Contains("Connecting to ",StringComparison.Ordinal)||e.Data.Contains("[Mojin]",StringComparison.Ordinal))&&IsConnectingLine(e.Data))
            window.Dispatcher.BeginInvoke(()=>RevealGame(false));
    }
    private async Task Poll()
    {
        if(disposed||reading)return;
        reading=true;
        try
        {
            if(game is null)return; // A manual reveal may precede the asynchronous JVM preparation.
            if(game.HasExited){Dispose();return;}
            if(revealed){TryRestoreWindows();return;}
            ScanWindows();
            var path=Path.Combine(directory,session+".json");var info=new FileInfo(path);
            if(info.Exists&&info.Length<=4096&&info.LastWriteTimeUtc!=lastWrite)
            {
                var text=await File.ReadAllTextAsync(path);
                var frame=JsonSerializer.Deserialize<GameLoadingFrame>(text,Json.Options);
                if(frame is not null&&frame.Session==session&&frame.Phase?.Length<=220&&frame.Detail?.Length<=220)
                {lastWrite=info.LastWriteTimeUtc;lastReport=Environment.TickCount64;if(!revealed&&!disposed){window.Update(frame);FrameObserved?.Invoke(frame);}}
            }
            if(Environment.TickCount64-lastReport>15000)RevealGame(false,"reporter-unavailable");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {if(Environment.TickCount64-lastReport>15000)RevealGame(false,"reporter-unavailable");}
        finally{reading=false;}
    }
    private void ScanWindows()=>EnumWindows((handle,_)=>{Inspect(handle);return true;},IntPtr.Zero);
    private void Inspect(IntPtr handle)
    {
        if(disposed||game is null||handle==IntPtr.Zero)return;
        GetWindowThreadProcessId(handle,out var pid);
        if(pid!=(uint)game.Id)return;
        var type=new StringBuilder(256);GetClassName(handle,type,type.Capacity);
        var name=new StringBuilder(256);GetWindowText(handle,name,name.Capacity);
        var renderer=IsGameRenderer(type.ToString(),name.ToString());
        if(renderer)
        {
            // Only restore windows this session actually hid (or a new, already visible game
            // window). Never promote a renderer's intentionally hidden helper/context window.
            if(IsWindowVisible(handle))
            {hidden.Add(handle);if(!revealed)ShowWindowAsync(handle,0);}
        }
        else if(!revealed&&IsWindowVisible(handle)&&type.ToString() is "#32770" or "SunAwtDialog")RevealGame(false,"dialog");
    }
    public static bool IsGameRenderer(string type,string title)
    {
        // GLFW creates an intentionally invisible message/helper HWND in the same process.
        // It is not a renderer and must never be restored or given a taskbar button.
        if(type.Contains("Helper",StringComparison.OrdinalIgnoreCase)||type.Contains("Dummy",StringComparison.OrdinalIgnoreCase)
            ||title.Equals("GLFW message window",StringComparison.OrdinalIgnoreCase)
            ||title.Contains("Shared Drawable",StringComparison.OrdinalIgnoreCase))return false;
        return type.Equals("LWJGL",StringComparison.OrdinalIgnoreCase)||type.Equals("GLFW30",StringComparison.OrdinalIgnoreCase)
            ||title.StartsWith("Minecraft",StringComparison.Ordinal)||title is "DeceasedCraft" or "MeatballCraft";
    }
    public void RevealGame(bool userRequested,string reason="connecting")
    {
        if(revealed){TryRestoreWindows();return;}
        revealed=true;RevealReason=userRequested?"manual":reason;
        if(hook!=IntPtr.Zero){UnhookWinEvent(hook);hook=IntPtr.Zero;}
        try{File.WriteAllText(Path.Combine(directory,session+".stop"),"");}catch(IOException){}
        window.Update(new(session,reason=="connecting"&&!userRequested?"connecting":"loading","",0,0));
        timer.Start();TryRestoreWindows();
    }
    private void TryRestoreWindows()
    {
        if(disposed||!revealed||GameWindowVisible)return;
        // The renderer can be recreated during startup, or manual reveal can be requested before
        // any window exists. Rescan and verify, rather than closing the splash after one async call.
        ScanWindows();
        var foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out var foregroundPid);
        IntPtr visible=IntPtr.Zero;
        foreach(var handle in hidden.ToArray())
        {
            GetWindowThreadProcessId(handle,out var pid);
            if(game is not null&&pid==(uint)game.Id&&IsWindow(handle))
            {
                // Hiding a renderer before its first shell registration can leave it without a
                // taskbar button. Declare the restored top-level renderer as an application window.
                var style=unchecked((uint)GetWindowLongPtr(handle,-20).ToInt64()); // GWL_EXSTYLE
                var taskbarStyle=(style&~0x80u)|0x40000u; // clear TOOLWINDOW, set APPWINDOW
                if(style!=taskbarStyle||!IsWindowVisible(handle)||IsIconic(handle))
                {if(restoring.Add(handle))_=RestoreWindow(handle,taskbarStyle);}
                else visible=handle;
            }
            else hidden.Remove(handle);
        }
        if(visible==IntPtr.Zero)return;
        GameWindowVisible=true;timer.Stop();
        if(RevealReason=="manual"||foregroundPid==(uint)Environment.ProcessId)SetForegroundWindow(visible);
        hidden.Clear();window.Close();GameRevealed?.Invoke();
    }
    private async Task RestoreWindow(IntPtr handle,uint style)
    {
        var expected=game!.Id;
        try
        {
            // Changing an extended style sends messages to the render thread. Keep that wait
            // off the launcher UI, which must continue painting the completed progress bar.
            await Task.Run(()=>
            {
                GetWindowThreadProcessId(handle,out var pid);
                if(pid!=(uint)expected||!IsWindow(handle))return;
                SetWindowLongPtr(handle,-20,new IntPtr(style));
                SetWindowPos(handle,IntPtr.Zero,0,0,0,0,0x4037); // ASYNCWINDOWPOS + FRAMECHANGED, preserve bounds/order
                ShowWindowAsync(handle,9);
            });
        }
        finally{restoring.Remove(handle);}
    }
    public void Dispose()
    {
        if(disposed)return;
        RevealGame(false,"disposed");disposed=true;timer.Stop();window.Close();
        if(game is not null)
        {game.OutputDataReceived-=Output;game.ErrorDataReceived-=Output;}
        // Keep stop marker while a live agent might still be exiting; clean this session on process exit.
        if(game is null||game.HasExited)
            foreach(var suffix in new[]{".json",".tmp",".stop"})
                try{File.Delete(Path.Combine(directory,session+suffix));}catch(IOException){}
    }
    private delegate void WinEvent(IntPtr hook,uint evt,IntPtr window,int objectId,int childId,uint thread,uint time);
    private delegate bool EnumWindow(IntPtr window,IntPtr state);
    [DllImport("user32.dll")]private static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll",EntryPoint="GetWindowTextW",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShowWindowAsync(IntPtr handle,int command);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr handle,int index,IntPtr value);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
}
