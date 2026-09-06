using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Boshan.Desktop;

public sealed class GameLoadingWindow : Window
{
    private readonly TextBlock phase,percentage;
    private readonly ProgressBar progress;
    private readonly bool reducedMotion;
    private int overallPercent;
    public int DisplayedPercent=>overallPercent;
    public event Action? RevealRequested;
    public GameLoadingWindow(string instance,bool reducedMotion=false)
    {
        this.reducedMotion=reducedMotion;
        var (name,color)=instance switch {
            "m3e"=>("魔法金属","#ACDBB1"),"dc2"=>("亡者世界","#E7B48C"),
            "mb"=>("肉丸工艺","#BAB3F0"),"vw"=>("虚空行者","#91B8CA"),_=>throw new ArgumentOutOfRangeException(nameof(instance))};
        var accent=(Brush)new BrushConverter().ConvertFromString(color)!;
        Title="魔金大帅 · "+name+" · 正在加载";
        using(var icon=typeof(GameLoadingWindow).Assembly.GetManifestResourceStream("Mojin.LauncherIcon")!)
            Icon=BitmapFrame.Create(icon,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
        Width=960;Height=540;MinWidth=480;MinHeight=270;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.Black;Foreground=Brushes.White;
        FontFamily=new FontFamily("Microsoft YaHei UI");
        _=new WindowViewport(this);
        var frame=new Grid{Width=960,Height=540,ClipToBounds=true};
        frame.Children.Add(new Image{Source=new BitmapImage(new Uri($"pack://application:,,,/MojinDashuai.Launcher;component/Assets/GameLoading/{instance}.jpg")),Stretch=Stretch.UniformToFill});
        frame.Children.Add(new Border{Background=new LinearGradientBrush(new GradientStopCollection{
            new(Color.FromArgb(80,0,0,0),0),new(Color.FromArgb(0,0,0,0),0.35),new(Color.FromArgb(110,0,0,0),0.65),new(Color.FromArgb(245,0,0,0),1)},new Point(0,0),new Point(0,1))});
        var heading=new StackPanel{Margin=new Thickness(32,28,0,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        heading.Children.Add(Text("魔金大帅",15,Brushes.White));
        var title=Text(name,40,Brushes.White);title.FontWeight=FontWeights.SemiBold;title.Margin=new Thickness(0,8,0,0);heading.Children.Add(title);
        frame.Children.Add(heading);
        var close=Link("×",22);close.Width=44;close.Height=44;close.Margin=new Thickness(0,10,10,0);close.HorizontalAlignment=HorizontalAlignment.Right;close.VerticalAlignment=VerticalAlignment.Top;
        close.ToolTip="关闭加载窗并显示游戏";close.Click+=(_,_)=>RevealRequested?.Invoke();frame.Children.Add(close);
        var footer=new Grid{Margin=new Thickness(32,0,32,30),VerticalAlignment=VerticalAlignment.Bottom};
        footer.ColumnDefinitions.Add(new ColumnDefinition());footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var status=new StackPanel{Margin=new Thickness(0,0,24,0),VerticalAlignment=VerticalAlignment.Bottom};
        phase=Text("正在加载",20,Brushes.White);phase.FontWeight=FontWeights.SemiBold;
        status.Children.Add(phase);footer.Children.Add(status);
        var right=new StackPanel{HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(right,1);
        var show=Link("显示游戏 ↗",13);show.HorizontalAlignment=HorizontalAlignment.Right;show.Margin=new Thickness(0,0,0,12);show.ToolTip="需要查看游戏原始加载画面时，可手动显示";show.Click+=(_,_)=>RevealRequested?.Invoke();right.Children.Add(show);
        percentage=Text("0%",28,Brushes.White);percentage.TextAlignment=TextAlignment.Right;right.Children.Add(percentage);footer.Children.Add(right);frame.Children.Add(footer);
        progress=new ProgressBar{Height=5,Minimum=0,Maximum=100,Foreground=accent,Background=new SolidColorBrush(Color.FromArgb(65,255,255,255)),BorderThickness=new Thickness(0),VerticalAlignment=VerticalAlignment.Bottom};
        frame.Children.Add(progress);
        Content=new Viewbox{Stretch=Stretch.Uniform,Child=frame};
        MouseLeftButtonDown+=(_,e)=>{if(e.ButtonState==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){}};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)RevealRequested?.Invoke();};
    }
    public void Update(GameLoadingFrame frame)
    {
        // Overall loader progress only. Hold through gaps and after the loader removes its bar.
        // Reserve completion until the actual connection signal; never reset or simulate time.
        if(frame.Percent is int percent)overallPercent=Math.Max(overallPercent,Math.Min(99,percent));
        if(frame.Phase=="connecting")overallPercent=100;
        phase.Text=frame.Phase=="connecting"?"正在连接服务器":overallPercent>=99?"即将进入游戏":"正在加载";
        progress.Value=overallPercent;percentage.Text=overallPercent+"%";
    }
    private static TextBlock Text(string text,double size,Brush brush)=>new(){Text=text,FontSize=size,Foreground=brush,TextTrimming=TextTrimming.CharacterEllipsis};
    private static Button Link(string text,double size)=>new(){Content=text,FontSize=size,Foreground=Brushes.White,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(7,5,7,5),Cursor=Cursors.Hand};
}

public sealed record GameLoadingFrame(string Session,string Phase,string Detail,int Completed,int Total)
{
    public bool HasCount=>Total>0&&Completed>=0&&Completed<=Total;
    // Counter comes from the loader's overall startup bar, never a timer.
    public int? Percent=>HasCount?(int)((long)Completed*100/Total):null;
}
