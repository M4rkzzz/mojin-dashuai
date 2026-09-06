using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class GameJoinLive
{
    public static async Task Run(string id,string root)
    {
        root=Path.GetFullPath(root);
        if(!root.EndsWith(Path.Combine(".local","loading-live-20260906"),StringComparison.OrdinalIgnoreCase)||!Routes.Domains.ContainsKey(id))throw new InvalidDataException("Expected the isolated release test copy");
        var manifest=Json.Read<PackManifest>(Path.Combine("artifacts/native",id+"-manifest.json"));
        var prepared=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"prepared.json"))).RootElement.GetProperty(id);
        var settings=new LauncherSettings{Root=root};settings.Java[id]=prepared.GetProperty("java").GetString()!;
        var accounts=new Accounts(new Vault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher")),NetworkPolicy.DirectApi,"");
        if(accounts.Current?.Profile.GameName!="M4rkzzz")throw new InvalidDataException("Expected the authorized saved test identity");
        var identity=accounts.Current.Profile;var session=await accounts.GameSession();
        var installer=new TransactionalInstaller(root);using var instanceLock=installer.Acquire(id);
        var output=Path.Combine(root,"reports","join-production-"+id);Directory.CreateDirectory(output);
        var secrets=new ConcurrentBag<string>();if(!string.IsNullOrEmpty(session.AccessToken))secrets.Add(session.AccessToken);
        var issued=0;
        using var join=new GameJoinSession(installer.InstancePath(id),id,async token=>{var ticket=await accounts.CreateJoinTicket(id,identity,token);secrets.Add(ticket.Ticket);Interlocked.Increment(ref issued);return ticket;});
        var port=id switch{"m3e"=>25501,"dc2"=>25502,"mb"=>25503,_=>25504};
        using var process=await new GameLauncher().Prepare(manifest,settings,session,new(Routes.Domains[id][0],"192.168.5.124",port,0),join:join.Options);
        process.StartInfo.RedirectStandardOutput=true;process.StartInfo.RedirectStandardError=true;
        process.Start();join.Attach(process);
        Json.Write(Path.Combine(output,"process.json"),new{process.Id,startedAt=process.StartTime.ToUniversalTime(),instance=id,manifest.Version,route="NAS LAN"});
        using var log=new StreamWriter(Path.Combine(output,"game.log")){AutoFlush=true};using var gate=new SemaphoreSlim(1);
        async Task Drain(StreamReader stream){while(await stream.ReadLineAsync() is {} line){foreach(var secret in secrets)line=line.Replace(secret,"[REDACTED]",StringComparison.Ordinal);await gate.WaitAsync();try{await log.WriteLineAsync(line);}finally{gate.Release();}}}
        Console.WriteLine($"START authenticated {id} PID={process.Id}");
        await Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError));await process.WaitForExitAsync();
        Json.Write(Path.Combine(output,"result.json"),new{instance=id,manifest.Version,issued,exitCode=process.ExitCode,closedAt=DateTimeOffset.UtcNow,joinedServerVerified=false,note="Ticket issuance and process exit only; compare actual server gate/online log before recording multiplayer acceptance."});
        Console.WriteLine($"EXIT {id} code={process.ExitCode} ticketsIssued={issued}");
    }
}
