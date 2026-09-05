using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class MicrosoftResume
{
    public static async Task Run()
    {
        var source=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
        var root=Path.Combine(Path.GetTempPath(),"mojin-session-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach(var key in new[]{"account",MicrosoftAccountStorage.Key})
            {
                var path=Path.Combine(source,key+".dpapi");
                if(!File.Exists(path))throw new InvalidOperationException("Saved Microsoft session is missing.");
                File.Copy(path,Path.Combine(root,key+".dpapi"));
            }
            var accounts=new Accounts(new Vault(root),NetworkPolicy.DirectApi,"");
            var expected=accounts.Current?.Profile;
            if(expected?.Kind!="microsoft")throw new InvalidOperationException("Saved account is not Microsoft.");
            await accounts.MicrosoftLogin(interactive:false);
            var fresh=new Accounts(new Vault(root),NetworkPolicy.DirectApi,"");
            var profile=await fresh.Restore();var session=await fresh.GameSession();
            if(profile?.Id!=expected.Id||profile.GameName!=expected.GameName||session.Username!=expected.GameName)throw new InvalidOperationException("Restored identity differs.");
            var skin=await fresh.Skin();
            Console.WriteLine(JsonSerializer.Serialize(new{liveMicrosoftLoginVerified=true,encryptedSessionRestored=true,silentAuthenticationPassed=true,gameSessionVerified=true,skinDownloaded=skin is not null,interactiveWindowOpened=false,gameWindowsOpened=0,authorization="existing encrypted Microsoft authorization"}));
        }
        catch(Exception error){Console.Error.WriteLine("Saved Microsoft session check failed: "+(NetworkPolicy.Find(error) is {} d?NetworkPolicy.Message(d):error.GetType().Name));Environment.ExitCode=1;}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
