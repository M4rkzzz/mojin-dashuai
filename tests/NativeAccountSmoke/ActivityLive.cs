using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;
internal static class ActivityLive
{
    public static async Task Run()
    {
        var source=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
        var root=Path.Combine(Path.GetTempPath(),"mojin-activity-read-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach(var key in new[]{"account",MicrosoftAccountStorage.Key})
        {
            var path=Path.Combine(source,key+".dpapi");
            if(File.Exists(path))File.Copy(path,Path.Combine(root,key+".dpapi"));
        }
        var accounts=new Accounts(new Vault(root),NetworkPolicy.DirectApi,"");
        var profile=await accounts.Restore()??throw new InvalidOperationException("Saved account unavailable");
        foreach(var world in new[]{"m3e","dc2","mb","vw"})
        {
            var response=await accounts.Activities(JsonSerializer.SerializeToElement(new{instance=world}));
            if(response.GetProperty("instance").GetString()!=world||response.GetProperty("actions").GetArrayLength()!=3)
                throw new InvalidOperationException("Activity response contract mismatch");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{nativeActivityAuthentication=true,worlds=4,claimsSubmitted=0,drawsSubmitted=0}));
    }
}
