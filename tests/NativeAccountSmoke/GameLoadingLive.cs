using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Boshan.Desktop;
using Boshan.Launcher;
using CmlLib.Core.Auth;

internal static class GameLoadingLive
{
    public static void Run(string root,string instance)
    {
        root=Path.GetFullPath(root);
        if(!root.EndsWith(Path.Combine(".local","loading-live-20260906"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only the isolated loading test root is accepted");
        var prepared=Json.Read<Dictionary<string,Input>>(Path.Combine(root,"prepared.json"))[instance];
        var manifest=Json.Read<PackManifest>(prepared.Manifest);
        var output=Path.Combine(root,"reports",instance);Directory.CreateDirectory(output);
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            _=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            dispatcher.BeginInvoke(async()=>
            {
                Process? process=null;GameLoadingSession? loading=null;TcpListener? listener=null;
                var watch=Stopwatch.StartNew();var observations=new List<object>();
                bool nonzeroProgress=false,hiddenObserved=false,visibleAfterHandoff=false,connectedLocally=false,normalExit=false;
                var frameCount=0;var captured=false;string? lastStage=null,reason=null;double? handoffSeconds=null;
                try
                {
                    var settings=new LauncherSettings{Root=root,Width=1280,Height=720,Fullscreen=false};settings.Java[instance]=prepared.Java;
                    listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                    var port=((IPEndPoint)listener.LocalEndpoint).Port;
                    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));
                    var accept=listener.AcceptTcpClientAsync(timeout.Token).AsTask();
                    var instancePath=Path.Combine(root,"instances",instance);
                    using var gate=new TransactionalInstaller(root).Acquire(instance);
                    loading=new GameLoadingSession(instancePath,instance,true);
                    var splash=(GameLoadingWindow)typeof(GameLoadingSession).GetField("window",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(loading)!;
                    loading.FrameObserved+=frame=>
                    {
                        frameCount++;hiddenObserved|=loading.HiddenWindowCount>0;
                        nonzeroProgress|=frame.HasCount&&frame.Completed>0&&frame.Completed<frame.Total;
                        if(lastStage!=frame.Phase||frameCount%10==0)
                        {observations.Add(new{elapsed=Math.Round(watch.Elapsed.TotalSeconds,2),frame.Phase,frame.Detail,frame.Completed,frame.Total,frame.Percent,hidden=loading.HiddenWindowCount});lastStage=frame.Phase;}
                        if(frame.HasCount&&frame.Completed>0&&!captured)
                        {
                            var visual=(FrameworkElement)splash.Content;
                            visual.UpdateLayout();
                            var bitmap=new RenderTargetBitmap(960,540,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream=File.Create(Path.Combine(output,"loading-real.png"));encoder.Save(stream);
                            captured=true;
                        }
                    };
                    loading.Show();
                    process=await new GameLauncher().Prepare(manifest,settings,MSession.CreateOfflineSession("LoadingAudit"),new("127.0.0.1","127.0.0.1",port,-1),loading:loading.Options);
                    loading.Configure(process);
                    using var log=new StreamWriter(Path.Combine(output,"game.log")){AutoFlush=true};var logGate=new object();
                    // This run uses an offline test session, never a player account or production route.
                    process.OutputDataReceived+=(_,e)=>{if(e.Data is not null)lock(logGate)log.WriteLine(e.Data);};
                    process.ErrorDataReceived+=(_,e)=>{if(e.Data is not null)lock(logGate)log.WriteLine(e.Data);};
                    process.Start();loading.Attach(process);
                    Json.Write(Path.Combine(output,"process.json"),new{process.Id,started=process.StartTime.ToUniversalTime(),instance,root,port});
                    Console.WriteLine($"START {instance} PID={process.Id} localPort={port}");
                    while(!process.HasExited&&watch.Elapsed<TimeSpan.FromMinutes(12))
                    {
                        await Task.Delay(250);hiddenObserved|=loading.HiddenWindowCount>0;
                        if(accept.IsCompletedSuccessfully)
                        {
                            connectedLocally=true;
                            if(loading.GameWindowVisible)break;
                        }
                        if(loading.RevealReason is not null&&loading.RevealReason!="connecting")break;
                    }
                    reason=loading.RevealReason;handoffSeconds=Math.Round(watch.Elapsed.TotalSeconds,2);
                    if(connectedLocally)
                    {
                        using var socket=await accept;socket.Close();
                    }
                    if(!process.HasExited)
                    {
                        await Task.Delay(1500);
                        var windows=ReadWindows(process.Id);
                        visibleAfterHandoff=loading.GameWindowVisible&&windows.Count(w=>w.Visible&&w.Renderer)==1
                            &&!windows.Any(w=>w.Visible&&!w.Renderer&&w.Class.Contains("GLFW",StringComparison.OrdinalIgnoreCase));
                        Console.WriteLine($"HANDOFF {instance}: connected={connectedLocally}, visible={visibleAfterHandoff}, reason={reason}. Game stays open for visual review; close it normally when done.");
                        Json.Write(Path.Combine(output,"handoff.json"),new{instance,connectedLocally,visibleAfterHandoff,reason,handoffSeconds,frameCount,nonzeroProgress,hiddenObserved,windows});
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(15));normalExit=process.ExitCode==0;
                    }
                    else normalExit=process.ExitCode==0;
                    await process.WaitForExitAsync();
                    if(!(nonzeroProgress&&hiddenObserved&&connectedLocally&&reason=="connecting"&&visibleAfterHandoff&&normalExit))
                        throw new InvalidOperationException($"Loading verification failed: progress={nonzeroProgress}, hidden={hiddenObserved}, connected={connectedLocally}, handoff={reason}, visible={visibleAfterHandoff}, exit={process.ExitCode}");
                    Console.WriteLine($"PASS {instance}: true loader counts, hidden window, real local connection, visible handoff, normal exit; {handoffSeconds}s");
                }
                catch(Exception ex){failure=ex;Console.Error.WriteLine(ex.Message);}
                finally
                {
                    listener?.Stop();
                    if(process is not null)
                    {
                        try{if(!process.HasExited){process.CloseMainWindow();if(!process.WaitForExit(5000))process.Kill(true);}}catch(InvalidOperationException){}
                    }
                    loading?.Dispose();process?.Dispose();
                    Json.Write(Path.Combine(output,"result.json"),new{instance,prepared.Version,passed=failure is null,nonzeroProgress,hiddenObserved,connectedLocally,reason,visibleAfterHandoff,normalExit,handoffSeconds,frameCount,observations,error=failure?.Message});
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)Environment.ExitCode=1;
    }
    private static List<NativeWindow> ReadWindows(int pid)
    {
        var result=new List<NativeWindow>();
        EnumWindows((handle,_)=>
        {
            GetWindowThreadProcessId(handle,out var actual);
            if(actual==(uint)pid)
            {
                var type=new StringBuilder(256);var title=new StringBuilder(256);
                GetClassName(handle,type,256);GetWindowText(handle,title,256);
                result.Add(new(type.ToString(),title.ToString(),IsWindowVisible(handle),GameLoadingSession.IsGameRenderer(type.ToString(),title.ToString())));
            }
            return true;
        },IntPtr.Zero);return result;
    }
    private sealed record NativeWindow(string Class,string Title,bool Visible,bool Renderer);
    private sealed record Input(string Manifest,string Java,string Version);
    private delegate bool EnumWindow(IntPtr handle,IntPtr state);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll",EntryPoint="GetWindowTextW",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr handle,StringBuilder text,int count);
}
