using System.Text.Json;
using System.Windows;
using Boshan.Desktop;

// Explicit, manual acceptance: opens only Microsoft's authentication window, never a game or launcher preview.
internal static class MicrosoftLive
{
    public static void Run()
    {
        var thread=new Thread(()=>
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Dispatcher.BeginInvoke(async ()=>
            {
                using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(16));
                try
                {
                    var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
                    var owner=new Window();
                    var accounts=new Accounts(new Vault(root),"https://launcher.boshan.uk","",()=>new MicrosoftWebUi(owner,Path.Combine(root,"MicrosoftAuth")));
                    await accounts.MicrosoftLogin(token:cancellation.Token);
                    var first=accounts.Current??throw new InvalidOperationException();
                    var restored=new Accounts(new Vault(root),"https://launcher.boshan.uk","");
                    if((await restored.Restore())?.Id!=first.Profile.Id)throw new InvalidOperationException();
                    await restored.MicrosoftLogin(false,token:cancellation.Token);
                    var game=await restored.GameSession();
                    if(game.Username!=first.Profile.GameName||game.UUID!=first.Profile.Id||game.UserType!="msa"||string.IsNullOrEmpty(game.AccessToken))throw new InvalidOperationException();
                    var skin=await restored.Skin();
                    Console.WriteLine(JsonSerializer.Serialize(new{liveMicrosoftLoginVerified=true,gameName=game.Username,encryptedSessionRestored=true,silentAuthenticationPassed=true,gameSessionVerified=true,skinDownloaded=skin is not null,gamesLaunched=0}));
                }
                catch(OperationCanceledException){Console.WriteLine("Microsoft authorization cancelled.");Environment.ExitCode=2;}
                catch(Exception ex)
                {
                    // Account service errors are sanitized; never print SDK exceptions or token objects.
                    Console.Error.WriteLine(ex is InvalidDataException?ex.Message:JsonSerializer.Serialize(new{error=ex.GetType().Name,status=ex is System.Net.Http.HttpRequestException http?(int?)http.StatusCode:null,stage=ex.Data["stage"],requestError=ex.Data["requestError"],transportErrors=ex.Data["transportErrors"]}));
                    Environment.ExitCode=1;
                }
                finally{app.Shutdown();}
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
    }
}
