using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Boshan.Desktop;

/// <summary>Keep the window in the current monitor's usable area, in physical pixels.</summary>
public sealed class WindowViewport
{
    private readonly Window window;
    private readonly Size preferred;
    private readonly Size minimum;
    private HwndSource? source;
    private bool queued,closed;

    public WindowViewport(Window window)
    {
        this.window=window;
        preferred=new(window.Width,window.Height);
        minimum=new(window.MinWidth,window.MinHeight);
        window.SourceInitialized+=(_,_)=>
        {
            source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source.AddHook(WindowMessage);
            // The HWND exists, so its DPI and destination monitor are now reliable.
            Fit(initial:true);
        };
        window.DpiChanged+=(_,_)=>QueueFit();
        window.StateChanged+=(_,_)=>{if(window.WindowState==WindowState.Normal)QueueFit();};
        window.Closed+=(_,_)=>{closed=true;source?.RemoveHook(WindowMessage);};
    }

    public static Rect FitBounds(Rect current,Rect work,Size preferred,Vector scale,bool initial)
    {
        // A margin remains around ordinary windows; taskbars are already excluded.
        var margin=Math.Min(16*Math.Max(scale.X,scale.Y),Math.Min(work.Width,work.Height)/20);
        var availableWidth=Math.Max(1,work.Width-2*margin);
        var availableHeight=Math.Max(1,work.Height-2*margin);
        var width=Math.Min(initial?preferred.Width*scale.X:current.Width,availableWidth);
        var height=Math.Min(initial?preferred.Height*scale.Y:current.Height,availableHeight);
        var left=initial?work.Left+(work.Width-width)/2:Math.Clamp(current.Left,work.Left+margin,work.Right-margin-width);
        var top=initial?work.Top+(work.Height-height)/2:Math.Clamp(current.Top,work.Top+margin,work.Bottom-margin-height);
        return new(left,top,width,height);
    }

    private void QueueFit()
    {
        if(queued||closed||source is null)return;
        queued=true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>{queued=false;if(!closed)Fit(initial:false);});
    }

    private void Fit(bool initial)
    {
        if(source is null||window.WindowState!=WindowState.Normal)return;
        var monitor=MonitorFromWindow(source.Handle,2);
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(monitor,ref info)||!GetWindowRect(source.Handle,out var native))return;
        var dpi=VisualTreeHelper.GetDpi(window);
        var scale=new Vector(dpi.DpiScaleX,dpi.DpiScaleY);
        var bounds=FitBounds(native.ToRect(),info.Work.ToRect(),preferred,scale,initial);
        window.MinWidth=Math.Min(minimum.Width,bounds.Width/scale.X);
        window.MinHeight=Math.Min(minimum.Height,bounds.Height/scale.Y);
        if(initial)window.WindowStartupLocation=WindowStartupLocation.Manual;
        // WPF/WebView2 handle DPI scaling; only constrain physical size and placement.
        if(initial||Math.Abs(bounds.Left-native.Left)>1||Math.Abs(bounds.Top-native.Top)>1
            ||Math.Abs(bounds.Width-native.ToRect().Width)>1||Math.Abs(bounds.Height-native.ToRect().Height)>1)
            SetWindowPos(source.Handle,IntPtr.Zero,(int)Math.Round(bounds.Left),(int)Math.Round(bounds.Top),
                (int)Math.Floor(bounds.Width),(int)Math.Floor(bounds.Height),0x0014); // NOZORDER | NOACTIVATE
    }

    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message is 0x02E0 or 0x007E or 0x001A or 0x0232) // DPI, resolution/work-area change, end move/resize
            QueueFit();
        if(message==0x0024) // WM_GETMINMAXINFO: maximize on this monitor, excluding its taskbar.
        {
            var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info))
            {
                var limits=Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition=new(info.Work.Left-info.Monitor.Left,info.Work.Top-info.Monitor.Top);
                limits.MaxSize=new(info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top);
                // Permit moving/restoring onto a smaller monitor even if the old minimum was larger.
                limits.MinTrackSize=new(Math.Min(limits.MinTrackSize.X,limits.MaxSize.X),Math.Min(limits.MinTrackSize.Y,limits.MaxSize.Y));
                Marshal.StructureToPtr(limits,lParam,false);
                handled=true;
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI(int x,int y){public int X=x,Y=y;}
    [StructLayout(LayoutKind.Sequential)] private struct RectI
    {
        public int Left,Top,Right,Bottom;
        public readonly Rect ToRect()=>new(Left,Top,Right-Left,Bottom-Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public RectI Monitor,Work;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo {public PointI Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd,out RectI rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
