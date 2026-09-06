using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Boshan.Desktop;

internal static class WindowViewportSmoke
{
    public static void Run()
    {
        var screens=new[]{new Rect(0,0,1366,728),new Rect(0,0,1280,680),new Rect(-1280,40,1280,984),new Rect(1920,0,1080,1880)};
        var cases=0;
        foreach(var work in screens)foreach(var scale in new[]{1d,1.25,1.5,2})foreach(var initial in new[]{true,false})
        {
            var rect=WindowViewport.FitBounds(new Rect(0,0,1920,1080),work,new Size(1280,820),new Vector(scale,scale),initial);
            if(!work.Contains(rect)||rect.Width<=0||rect.Height<=0)throw new InvalidOperationException("Window exceeds the usable monitor area.");
            cases++;
        }
        Exception? error=null;
        var thread=new Thread(()=>
        {
            Window? window=null;
            try
            {
                // Exercise real WPF startup sizing without showing a visible window or loading WebView/accounts.
                window=new Window{Width=1280,Height=820,MinWidth=820,MinHeight=650,WindowStyle=WindowStyle.None,
                    ResizeMode=ResizeMode.CanResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,
                    Opacity=0,ShowInTaskbar=false,ShowActivated=false};
                WindowChrome.SetWindowChrome(window,new WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0)});
                _=new WindowViewport(window);
                window.Show();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var hwnd=new WindowInteropHelper(window).Handle;
                var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
                if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info)||!GetWindowRect(hwnd,out var actual))throw new InvalidOperationException("Native window bounds unavailable.");
                if(!info.Work.AsRect().Contains(actual.AsRect()))throw new InvalidOperationException("WPF startup placed the native window outside its work area.");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{geometryCases=cases,nativeWindowFits=true,work=info.Work.AsRect().ToString(),window=actual.AsRect().ToString()}));
            }
            catch(Exception ex){error=ex;}
            finally{window?.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)throw new InvalidOperationException("Window viewport smoke failed.",error);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI
    {
        public int Left,Top,Right,Bottom;
        public readonly Rect AsRect()=>new(Left,Top,Right-Left,Bottom-Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo{public int Size;public RectI Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window,out RectI rect);
}
