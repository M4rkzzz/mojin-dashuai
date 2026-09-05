using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boshan.Launcher;
using Microsoft.Win32;

namespace Boshan.Desktop;

public class HubDialog : Window
{
    protected readonly StackPanel Body=new(){Margin=new Thickness(28)};
    protected HubDialog(string title,int width=490,int height=390)
    {
        Title=title;Width=width;Height=height;Background=new SolidColorBrush(Color.FromRgb(22,30,23));Foreground=new SolidColorBrush(Color.FromRgb(218,231,203));WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;Content=Body;
        Body.Children.Add(new TextBlock{Text=title,FontSize=23,Margin=new Thickness(0,0,0,22)});
    }
    protected void Label(string text)=>Body.Children.Add(new TextBlock{Text=text,Margin=new Thickness(0,10,0,8),TextWrapping=TextWrapping.Wrap});
    protected Button Button(string text,RoutedEventHandler click){var button=new Button{Content=text,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,18,0,0),Background=new SolidColorBrush(Color.FromRgb(215,235,189)),Foreground=Brushes.Black,BorderThickness=new Thickness(0)};button.Click+=click;Body.Children.Add(button);return button;}
}
public sealed class PasswordWindow : HubDialog
{
    private readonly PasswordBox current=new(){Padding=new Thickness(9)}, next=new(){Padding=new Thickness(9)};
    public string CurrentPassword=>current.Password;
    public string NewPassword=>next.Password;
    public PasswordWindow(bool recovery):base(recovery?"重新生成恢复码":"修改账号密码")
    {
        Label("当前密码");Body.Children.Add(current);
        if(!recovery){Label("新密码（至少 10 个字符）");Body.Children.Add(next);}
        Button("确认",(_,_)=>{if(current.Password.Length==0||(!recovery&&next.Password.Length<10))return;DialogResult=true;});
    }
}
public sealed class RecoveryWindow : HubDialog
{
    public RecoveryWindow(string code):base("保存新的恢复码",490,300)
    {
        Label("此码只展示一次，请保存至安全位置。旧码已失效。");Body.Children.Add(new TextBox{Text=code,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Padding=new Thickness(12),Margin=new Thickness(0,10,0,0)});Button("我已保存",(_,_)=>Close());
    }
}
public sealed class ContentWindow : HubDialog
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    private static readonly Brush Surface=new SolidColorBrush(Color.FromRgb(17,17,17));
    private static readonly Brush BorderColor=new SolidColorBrush(Color.FromRgb(65,65,65));
    private static Button ActionButton(string text)
    {
        var button=new Button{Content=text,Padding=new Thickness(12,9,12,9),MinHeight=38,Background=new SolidColorBrush(Color.FromRgb(34,34,34)),Foreground=Brushes.White,BorderBrush=BorderColor,BorderThickness=new Thickness(1),FontSize=13};
        button.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border x:Name="frame" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="frame" Property="Background" Value="#333333"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        return button;
    }
    public ContentWindow(string root,Func<IDisposable> acquire):base("模组与资源管理",670,560)
    {
        Background=Surface;Foreground=Brushes.White;ResizeMode=ResizeMode.CanResize;
        var work=SystemParameters.WorkArea;Width=Math.Min(670,Math.Max(300,work.Width-24));Height=Math.Min(560,Math.Max(260,work.Height-24));
        MinWidth=Math.Min(400,Width);MinHeight=Math.Min(300,Height);MaxWidth=Math.Max(Width,work.Width);MaxHeight=Math.Max(Height,work.Height);
        SourceInitialized+=(_,_)=>{try{var dark=1;var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;if(DwmSetWindowAttribute(handle,20,ref dark,sizeof(int))!=0)DwmSetWindowAttribute(handle,19,ref dark,sizeof(int));}catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException){ }};
        var layout=new Grid{Margin=new Thickness(20),Background=Surface};Content=layout;
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})layout.RowDefinitions.Add(new RowDefinition{Height=height});
        void Place(UIElement element,int row){Grid.SetRow(element,row);layout.Children.Add(element);}
        Place(new TextBlock{Text="模组与资源管理",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)},0);
        var tabs=new Grid();for(var i=0;i<3;i++)tabs.ColumnDefinitions.Add(new ColumnDefinition());Place(tabs,1);
        var listArea=new Grid{Margin=new Thickness(0,14,0,0)};Place(listArea,2);
        var list=new ListBox{Name="ContentFiles",Background=Surface,Foreground=Brushes.White,BorderBrush=BorderColor,BorderThickness=new Thickness(1),Padding=new Thickness(5),FontSize=13};
        ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Auto);listArea.Children.Add(list);
        var empty=new TextBlock{Name="ContentEmptyState",Text="暂无模组",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(190,190,190)),FontSize=14,IsHitTestVisible=false};listArea.Children.Add(empty);
        var status=new TextBlock{Name="ContentStatus",Margin=new Thickness(0,10,0,0),TextTrimming=TextTrimming.CharacterEllipsis,FontSize=13,Visibility=Visibility.Collapsed};Place(status,3);
        var actions=new Grid{Name="ContentActions",Margin=new Thickness(0,14,0,0)};actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition());Place(actions,4);
        var add=ActionButton("添加文件");add.Margin=new Thickness(0,0,6,0);actions.Children.Add(add);
        var toggle=ActionButton("选择文件后停用");toggle.IsEnabled=false;toggle.Margin=new Thickness(6,0,0,0);Grid.SetColumn(toggle,1);actions.Children.Add(toggle);
        var names=new[]{"mods","resourcepacks","shaderpacks"};var labels=new[]{"模组","资源包","光影"};var tabButtons=new List<Button>();var selected=0;var busy=false;
        void Message(string text,bool error=false){status.Text=text;status.Foreground=error?new SolidColorBrush(Color.FromRgb(241,164,148)):new SolidColorBrush(Color.FromRgb(207,231,190));status.Visibility=Visibility.Visible;}
        void Selection(){toggle.IsEnabled=!busy&&list.SelectedItem is string;toggle.Content=list.SelectedItem is string name?(name.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)?"恢复选中文件":"停用选中文件"):"选择文件后停用";}
        void Reload()
        {
            var directory=ContentSecurity.SafePath(root,names[selected]);Directory.CreateDirectory(directory);var files=Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();list.ItemsSource=files;
            empty.Text="暂无"+labels[selected];empty.Visibility=files.Length==0?Visibility.Visible:Visibility.Collapsed;Selection();
        }
        void SetBusy(bool value){busy=value;add.IsEnabled=!value;list.IsEnabled=!value;foreach(var button in tabButtons)button.IsEnabled=!value;Selection();}
        for(var index=0;index<3;index++)
        {
            var tab=index;var button=ActionButton(labels[index]);button.Margin=new Thickness(index==0?0:4,0,index==2?0:4,0);Grid.SetColumn(button,index);tabs.Children.Add(button);tabButtons.Add(button);
            button.Click+=(_,_)=>{selected=tab;foreach(var item in tabButtons)item.Background=new SolidColorBrush(Color.FromRgb(34,34,34));button.Background=new SolidColorBrush(Color.FromRgb(53,53,53));status.Visibility=Visibility.Collapsed;Reload();};
        }
        tabButtons[0].Background=new SolidColorBrush(Color.FromRgb(53,53,53));list.SelectionChanged+=(_,_)=>Selection();Reload();
        add.Click+=async(_,_)=>
        {
            SetBusy(true);
            try
            {
                using var gate=acquire();var dialog=new OpenFileDialog{Title="添加"+labels[selected],Filter=selected==0?"模组文件|*.jar;*.zip":"资源文件|*.zip",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;
                var files=dialog.FileNames;var folder=names[selected];Message("正在添加文件");
                await Task.Run(()=>{foreach(var file in files){var dest=ContentSecurity.SafePath(root,folder+"/"+Path.GetFileName(file));if(File.Exists(dest))throw new InvalidDataException("存在同名文件，请先停用原文件。");TransactionalInstaller.AtomicCopy(file,dest);}});
                Reload();Message($"已添加 {files.Length} 个文件");
            }
            catch(Exception ex){Message(ex is InvalidDataException?ex.Message:"无法添加文件，请检查游戏是否关闭及目录权限。",true);}
            finally{SetBusy(false);}
        };
        toggle.Click+=async(_,_)=>
        {
            if(list.SelectedItem is not string name)return;SetBusy(true);
            try
            {
                using var gate=acquire();var source=ContentSecurity.SafePath(root,names[selected]+"/"+name);var restoring=name.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);var dest=restoring?source[..^9]:source+".disabled";
                await Task.Run(()=>File.Move(source,dest,false));Reload();Message(restoring?"已恢复选中文件":"已停用选中文件");
            }
            catch{Message("无法修改文件：文件正在使用或目标文件已存在。",true);}
            finally{SetBusy(false);}
        };
    }
}
