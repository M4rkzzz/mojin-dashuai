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
    public ContentWindow(string root,Func<IDisposable> acquire):base("模组与资源管理",670,530)
    {
        var tabs=new ComboBox{ItemsSource=new[]{"mods","resourcepacks","shaderpacks"},SelectedIndex=0,Padding=new Thickness(8)};Body.Children.Add(tabs);
        var list=new ListBox{Height=260,Margin=new Thickness(0,15,0,0),Background=new SolidColorBrush(Color.FromRgb(16,22,17)),Foreground=Foreground};Body.Children.Add(list);
        void Reload(){var directory=ContentSecurity.SafePath(root,tabs.SelectedItem.ToString()!);Directory.CreateDirectory(directory);list.ItemsSource=Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();}
        tabs.SelectionChanged+=(_,_)=>Reload();Reload();
        Button("添加文件",(_,_)=>{
            try{using var gate=acquire();var dialog=new OpenFileDialog{Filter="模组或资源|*.jar;*.zip",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;foreach(var file in dialog.FileNames){var relative=tabs.SelectedItem+"/"+Path.GetFileName(file);var dest=ContentSecurity.SafePath(root,relative);if(File.Exists(dest))throw new InvalidDataException("存在同名文件，请先停用原文件。");TransactionalInstaller.AtomicCopy(file,dest);}Reload();}
            catch(Exception ex){MessageBox.Show(this,ex is InvalidDataException?ex.Message:"文件暂时无法修改，请先关闭游戏。","内容管理");}
        });
        Button("停用选中文件 / 恢复 .disabled 文件",(_,_)=>{
            try{if(list.SelectedItem is not string name)return;using var gate=acquire();var source=ContentSecurity.SafePath(root,tabs.SelectedItem+"/"+name);var dest=name.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)?source[..^9]:source+".disabled";File.Move(source,dest,false);Reload();}
            catch{MessageBox.Show(this,"无法修改：文件可能正在使用或目标文件已存在。","内容管理");}
        });
    }
}
