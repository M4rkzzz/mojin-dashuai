using System.Windows;
using System.IO;
using Boshan.Launcher;
namespace Boshan.Desktop;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==2&&e.Args[0]=="--update-ready"&&Guid.TryParseExact(e.Args[1],"N",out _))UpdateStartup.Nonce=e.Args[1];
        try
        {
            var config=Json.Read<AppConfig>(Path.Combine(AppContext.BaseDirectory,"launcher.json"));
            var updates=new LauncherUpdates(UpdateStartup.DataRoot,config.PublicKeys);
            for(var attempt=0;attempt<2;attempt++)
            {
                var ready=await updates.Ready(AppContext.BaseDirectory,typeof(App).Assembly.GetName().Version!);
                if(ready is null)break;
                if(await UpdateStartup.Start(updates,ready)){Shutdown();return;}
            }
        }
        catch(Exception){ /* A damaged/unavailable update must not prevent the installed launcher opening. */ }
        MainWindow=new MainWindow();MainWindow.Show();
    }
}
