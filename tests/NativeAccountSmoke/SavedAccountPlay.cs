using System.Diagnostics;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class SavedAccountPlay
{
    public static async Task Run(string[] args)
    {
        try
        {
            var accountRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Boshan","Launcher");
            var accounts=new Accounts(new Vault(accountRoot),"https://launcher.boshan.uk","");
            if(args is ["--saved-profile"])
            {
                var profile=accounts.Current?.Profile;
                Console.WriteLine(JsonSerializer.Serialize(new{gameName=profile?.GameName,kind=profile?.Kind}));return;
            }
            if(args.Length!=5)throw new InvalidDataException("Expected manifest, isolated root, route and expected game name.");
            var manifest=Json.Read<PackManifest>(args[1]);var root=Path.GetFullPath(args[2]);
            if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Saved-account checks require an isolated game directory.");
            if(!Routes.Domains[manifest.Instance].Contains(args[3]))throw new InvalidDataException("Unknown test route.");
            if(accounts.Current?.Profile.GameName!=args[4])throw new InvalidDataException("Saved game name does not match the requested player.");
            var session=await accounts.GameSession();
            if(session.Username!=args[4])throw new InvalidDataException("Authenticated game name does not match the requested player.");
            var installer=new TransactionalInstaller(root);using var gate=installer.Acquire(manifest.Instance);
            var route=await Routes.Resolve(args[3]);
            using var process=await new GameLauncher().Prepare(manifest,new LauncherSettings{Root=root},session,route);
            process.StartInfo.RedirectStandardOutput=true;process.StartInfo.RedirectStandardError=true;
            process.Start();
            Json.Write(Path.Combine(installer.InstancePath(manifest.Instance),".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime()));
            Json.Write(Path.Combine(root,manifest.Instance+"-saved-play-process.json"),new{process.Id,StartedAt=process.StartTime.ToUniversalTime(),GameName=session.Username,Route=args[3],JavaMajor=manifest.Runtime.Major});
            Console.WriteLine($"Started {manifest.Instance} as {session.Username}; PID {process.Id}.");
            using var writer=new StreamWriter(Path.Combine(root,manifest.Instance+"-saved-play.log"),false);
            using var logGate=new SemaphoreSlim(1);
            async Task Drain(StreamReader reader)
            {
                string? line;
                while((line=await reader.ReadLineAsync()) is not null)
                {
                    if(!string.IsNullOrEmpty(session.AccessToken))line=line.Replace(session.AccessToken,"[REDACTED]",StringComparison.Ordinal);
                    await logGate.WaitAsync();
                    try{await writer.WriteLineAsync(line);await writer.FlushAsync();}finally{logGate.Release();}
                }
            }
            await Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError));await process.WaitForExitAsync();
            Console.WriteLine("Game exited: "+process.ExitCode);Environment.ExitCode=process.ExitCode;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex is InvalidDataException?ex.Message:ex.GetType().Name);Environment.ExitCode=1;
        }
    }
}
