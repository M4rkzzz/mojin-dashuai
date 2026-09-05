using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boshan.Desktop;

internal static class ContentDialogLayoutSmoke
{
    private sealed class EmptyLease:IDisposable {public void Dispose(){} }
    public static void Run(string outputRoot)
    {
        outputRoot=Path.GetFullPath(outputRoot);if(!outputRoot.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar))throw new InvalidDataException("Use an isolated .local output directory.");Directory.CreateDirectory(outputRoot);
        Exception? error=null;var checks=new List<object>();
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"mojin-content-layout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"mods"));
            try
            {
                for(var i=0;i<160;i++)File.WriteAllText(Path.Combine(root,"mods",$"fixture-mod-{i:D3}.jar"),"layout fixture");
                var window=new ContentWindow(root,()=>new EmptyLease());var grid=(Grid)window.Content;window.Content=null;
                var host=new Border{Background=new SolidColorBrush(Color.FromRgb(17,17,17)),Child=grid};TextElement.SetForeground(host,Brushes.White);
                var list=Find<ListBox>(grid,"ContentFiles");var actions=Find<Grid>(grid,"ContentActions");var empty=Find<TextBlock>(grid,"ContentEmptyState");
                if(actions.Children.Cast<Button>().ElementAt(1).IsEnabled)throw new InvalidOperationException("Disable action must start disabled with no selection.");
                list.SelectedIndex=0;
                if(!actions.Children.Cast<Button>().ElementAt(1).IsEnabled)throw new InvalidOperationException("Disable action must become available when a file is selected.");
                foreach(var scale in new[]{1d,1.25,1.5,2})foreach(var screen in new[]{(1280,720),(1920,1080)})
                {
                    var width=Math.Min(646,screen.Item1/scale-24);var height=Math.Min(506,screen.Item2/scale-64);
                    host.Width=width;host.Height=height;host.Measure(new Size(width,height));host.Arrange(new Rect(0,0,width,height));host.UpdateLayout();
                    foreach(var button in actions.Children.Cast<Button>())
                    {
                        var top=button.TranslatePoint(new Point(0,0),host);
                        if(top.X<0||top.Y<0||top.X+button.ActualWidth>width+1||top.Y+button.ActualHeight>height+1||button.ActualHeight<30)throw new InvalidOperationException("Content management action is clipped.");
                    }
                    if(list.ActualHeight<40)throw new InvalidOperationException("Scrollable content list became unusably small.");
                    var path=Path.Combine(outputRoot,$"content-{scale*100:0}-{screen.Item1}x{screen.Item2}.png");Save(host,path,width,height,scale);
                    checks.Add(new{scale,screen=$"{screen.Item1}x{screen.Item2}",buttonsVisible=true,listHeight=list.ActualHeight,screenshot=Path.GetFileName(path)});
                }
                var category=grid.Children.OfType<Grid>().First();category.Children.Cast<Button>().ElementAt(1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(empty.Visibility!=Visibility.Visible||empty.Text!="暂无资源包")throw new InvalidOperationException("Empty category status is missing.");
                if(actions.Children.Cast<Button>().ElementAt(1).IsEnabled)throw new InvalidOperationException("Empty category must disable the file action.");
                host.UpdateLayout();Save(host,Path.Combine(outputRoot,"content-empty.png"),host.Width,host.Height,2);
                if(PresentationSource.FromVisual(window) is not null)throw new InvalidOperationException("Layout fixture unexpectedly created a native window.");
                window.Close();
            }
            catch(Exception ex){error=ex;}
            finally
            {
                var resolved=Path.GetFullPath(root);if(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("mojin-content-layout-",StringComparison.Ordinal))Directory.Delete(resolved,true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error is not null)throw new InvalidOperationException("Content layout smoke failed",error);
        var report=JsonSerializer.Serialize(new{passed=true,checks,emptyState=true,noNativeWindow=true},new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(outputRoot,"report.json"),report);Console.WriteLine(report);
    }
    private static T Find<T>(Grid grid,string name)where T:FrameworkElement
    {
        foreach(var child in grid.Children.OfType<FrameworkElement>()){if(child is T match&&match.Name==name)return match;if(child is Grid nested){try{return Find<T>(nested,name);}catch(InvalidOperationException){}}}throw new InvalidOperationException(name);
    }
    private static void Save(Visual visual,string path,double width,double height,double scale)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(width*scale),(int)Math.Ceiling(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
