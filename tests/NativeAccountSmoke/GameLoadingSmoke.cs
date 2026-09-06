using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Boshan.Desktop;

internal static class GameLoadingSmoke
{
    public static void Run(string output)
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            _=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            dispatcher.BeginInvoke(async()=>
            {
                try
                {
                    Directory.CreateDirectory(output);
                    foreach(var id in new[]{"m3e","dc2","mb","vw"})
                    {
                        var window=new GameLoadingWindow(id,true);
                        if(window.WindowStyle!=WindowStyle.None)throw new Exception("Unexpected title bar");
                        if(window.Icon is null)throw new Exception("Loading window icon missing");
                        if(window.ShowInTaskbar)throw new Exception("Duplicate launcher taskbar entry");
                        window.Update(new("test","loading","",3,7));
                        if(window.DisplayedPercent!=42)throw new Exception("Overall counter not displayed");
                        window.Update(new("test","unknown","internal text must never be displayed",0,0));
                        if(window.DisplayedPercent!=42)throw new Exception("Progress reset between reports");
                        var visual=(FrameworkElement)window.Content;
                        visual.Measure(new Size(960,540));visual.Arrange(new Rect(0,0,960,540));visual.UpdateLayout();
                        var bitmap=new RenderTargetBitmap(960,540,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using(var file=File.Create(Path.Combine(output,"loading-"+id+".png")))encoder.Save(file);
                        window.Update(new("test","loading","",7,7));
                        if(window.DisplayedPercent!=99)throw new Exception("Premature completion");
                        window.Update(new("test","connecting","",0,0));
                        window.Update(new("test","unknown","",0,0));
                        if(window.DisplayedPercent!=100)throw new Exception("Completed progress was erased");
                        window.Close();
                    }
                    if(new GameLoadingFrame("x","stage","",24,93).Percent!=25||new GameLoadingFrame("x","stage","",1,0).Percent is not null
                        ||new GameLoadingFrame("x","stage","",11,10).Percent is not null)throw new Exception("Counter arithmetic");
                    foreach(var line in new[]{"[Mojin] Client initialization complete; connecting to selected server.",
                        "[03:09:39] [Client thread/INFO]: [uk.boshan.mojin.MojinAutoConnect:onClientTick:56]: [Mojin] Client initialization complete; connecting to selected server.",
                        "[03:10:01] [Render thread/INFO]: Connecting to dc2.mc.boshan.uk, 25502",
                        "  <log4j:Message><![CDATA[Connecting to 127.0.0.1, 2839]]></log4j:Message>",
                        "  <log4j:Message><![CDATA[[uk.boshan.mojin.MojinAutoConnect:onClientTick:56]: [Mojin] Client initialization complete; connecting to selected server.]]></log4j:Message>"})
                        if(!GameLoadingSession.IsConnectingLine(line))throw new Exception("Connection signal missing");
                    if(GameLoadingSession.IsConnectingLine("Loading mods 100%"))throw new Exception("Guessed handoff");
                    if(GameLoadingSession.IsGameRenderer("GLFW30Helper","GLFW message window")
                        ||GameLoadingSession.IsGameRenderer("GLFW3 Helper","GLFW message window")
                        ||GameLoadingSession.IsGameRenderer("GLFW30","MC - Shared Drawable")
                        ||GameLoadingSession.IsGameRenderer("GLFW30","GLFW message window")
                        ||!GameLoadingSession.IsGameRenderer("GLFW30","DeceasedCraft"))throw new Exception("GLFW helper mistaken for game window");

                    using var loading=new GameLoadingSession(Path.Combine(output,"fixture"),"vw",true);
                    var splash=(Window)typeof(GameLoadingSession).GetField("window",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(loading)!;
                    splash.Opacity=0;splash.ShowInTaskbar=false;splash.ShowActivated=false;loading.Show();
                    using var child=new Process();child.StartInfo.FileName=Environment.ProcessPath!;
                    if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")child.StartInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                    child.StartInfo.ArgumentList.Add("--loading-window-fixture");child.StartInfo.UseShellExecute=false;child.StartInfo.CreateNoWindow=true;
                    loading.Configure(child);
                    var handle=new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
                    child.OutputDataReceived+=(_,e)=>{if(e.Data?.StartsWith("HANDLE:")==true)handle.TrySetResult(new IntPtr(long.Parse(e.Data[7..])));};
                    child.Start();loading.Attach(child);
                    var hwnd=await handle.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await Task.Delay(600);
                    if(IsWindowVisible(hwnd))throw new Exception("Game window was not hidden during loading");
                    await Task.Delay(1600);
                    if(!IsWindowVisible(hwnd))throw new Exception("Game window was not restored on connection");
                    await child.WaitForExitAsync();loading.Dispose();
                    Console.WriteLine("PASS four branded previews; no topbar; overall progress; holds 99/100; XML handoff; native hide/restore");
                }
                catch(Exception ex){error=ex;}
                finally{dispatcher.InvokeShutdown();}
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)throw new InvalidOperationException("Loading smoke failed",error);
    }
    public static void Fixture()
    {
        var thread=new Thread(()=>
        {
            var window=new Window{Title="Minecraft Loading Fixture",Width=500,Height=300,Opacity=0,ShowInTaskbar=false,ShowActivated=false};
            window.Show();Console.WriteLine("HANDLE:"+new WindowInteropHelper(window).Handle.ToInt64());
            var connect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1500)};
            connect.Tick+=(_,_)=>{connect.Stop();Console.WriteLine("[Mojin] Client initialization complete; connecting to selected server.");};connect.Start();
            var exit=new DispatcherTimer{Interval=TimeSpan.FromSeconds(4)};exit.Tick+=(_,_)=>{exit.Stop();window.Close();Dispatcher.CurrentDispatcher.InvokeShutdown();};exit.Start();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
    }
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(IntPtr handle);
}
