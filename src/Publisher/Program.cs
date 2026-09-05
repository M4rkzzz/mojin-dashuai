using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Boshan.Launcher;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.CommandParser;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Version;
using CmlLib.Core.Tasks;

if(args.Length==0){Console.WriteLine("keygen PRIVATE PUBLIC | verify MANIFEST | sign MANIFEST PRIVATE OUTPUT | sign-catalog CATALOG PRIVATE OUTPUT | diff OLD NEW | probe INSTANCE | fetch CONTENT_FILE CACHE | engine-files INSTANCE_DIR VERSION OUTPUT | lab-launch INSTANCE_DIR VERSION JAVA [SERVER_DOMAIN]");return;}
try
{
    switch(args[0])
    {
        case "check-launcher-update":
        {
            var root=Path.GetFullPath(args[3]);
            if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update checks require an isolated .local directory.");
            var keys=JsonDocument.Parse(File.ReadAllText(args[2])).RootElement.GetProperty("publicKeys").Deserialize<Dictionary<string,string>>(Json.Options)!;
            var updates=new LauncherUpdates(root,keys);
            var envelope=Json.Read<SignedEnvelope>(args[1]);var release=updates.AcceptMetadata(envelope);
            var current=args.Length>4?Path.GetFullPath(args[4]):AppContext.BaseDirectory;
            var pending=await updates.PendingDownloadBytes(release,current);long received=0;
            using var downloader=new Downloader(Path.Combine(root,"cache"),new LauncherSettings{LimitMiB=8},origin:NetworkPolicy.DirectApi);
            var prepared=await updates.Prepare(envelope,downloader,count=>Interlocked.Add(ref received,count),currentDirectory:current);
            var report=new{prepared.Release.Version,Files=prepared.Release.Files.Length,prepared.Release.Archive.Size,DownloadedAndVerified=true,ExpectedDownloadBytes=pending,ActualDownloadBytes=received,SingleOrigin=true,GameStarted=false,Activated=false};
            Json.Write(Path.Combine(root,"report.json"),report);Console.WriteLine(JsonSerializer.Serialize(report,Json.Options));break;
        }
        case "bundle-launcher":
            if(args.Length!=6)throw new ArgumentException("bundle-launcher DIRECTORY VERSION SEQUENCE PUBLIC_BASE OUTPUT_DIRECTORY");
            await LauncherBundle.Build(args[1],args[2],long.Parse(args[3]),args[4],args[5]);break;
        case "sign-launcher":
        {
            var release=Json.Read<LauncherRelease>(args[1]);LauncherUpdates.Validate(release);
            Json.Write(args[3],ContentSecurity.Sign(release,"release-1",File.ReadAllText(args[2])));
            Console.WriteLine("Launcher update signed.");break;
        }
        case "verify-launcher":
        {
            var keys=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2])).RootElement.GetProperty("publicKeys").Deserialize<Dictionary<string,string>>(Json.Options)!;
            LauncherUpdates.Validate(ContentSecurity.Verify<LauncherRelease>(Json.Read<SignedEnvelope>(args[1]),keys));
            Console.WriteLine("Launcher signature and inventory valid.");break;
        }
        case "keygen":
            using(var key=ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                if(File.Exists(args[1])||File.Exists(args[2]))throw new IOException("Refusing to overwrite signing keys.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                File.WriteAllText(args[1],key.ExportPkcs8PrivateKeyPem());File.WriteAllText(args[2],key.ExportSubjectPublicKeyInfoPem());
            }
            Console.WriteLine("Signing key generated. Keep the private key outside public release directories.");break;
        case "verify":
            ContentSecurity.Validate(Json.Read<PackManifest>(args[1]));Console.WriteLine("Manifest structure and runtime constraints valid.");break;
        case "sign":
        case "sign-beta":
        {
            var manifest=Json.Read<PackManifest>(args[1]);ContentSecurity.Validate(manifest);
            var hash=await ContentSecurity.HashFile(args[1]);
            foreach(var evidence in manifest.ValidationEvidence)
            {
                var report=Json.Read<AcceptanceReport>(evidence);
                ReleaseAcceptance.Require(manifest,hash,report,args[0]=="sign-beta");
            }
            Json.Write(args[3],ContentSecurity.Sign(manifest,"release-1",File.ReadAllText(args[2])));Console.WriteLine("Pack manifest signed.");break;
        }
        case "sign-catalog":
        {
            var directory=Json.Read<Catalog>(args[1]);
            if(directory.Sequence<=0||directory.ExpiresAt<=DateTimeOffset.UtcNow||directory.Servers.Select(s=>s.Id).Distinct().Count()!=3)throw new InvalidDataException("Invalid catalog.");
            foreach(var server in directory.Servers)
            {
                if(!Routes.Domains.TryGetValue(server.Id,out var expected)||!server.Routes.SequenceEqual(expected))throw new InvalidDataException("Catalog routes do not match the agreed server domains.");
                foreach(var release in server.Rollbacks.Concat(server.Release is null?[]:new[]{server.Release}))if(!ContentSecurity.HashPattern().IsMatch(release.Sha256)||new Uri(release.ManifestUrl).Scheme!="https")throw new InvalidDataException("Invalid release reference.");
            }
            Json.Write(args[3],ContentSecurity.Sign(directory,"release-1",File.ReadAllText(args[2])));Console.WriteLine("Catalog signed.");break;
        }
        case "check-catalog":
        {
            var root=Path.GetFullPath(args[2]);
            if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Catalog checks require an isolated .local directory.");
            var config=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
            var keys=config.GetProperty("publicKeys").Deserialize<Dictionary<string,string>>(Json.Options)!;
            var client=new CatalogClient(config.GetProperty("api").GetString()!,keys,Path.Combine(root,"checkpoint.json"));
            var directory=await client.Fetch();
            if(directory.Servers.Length!=3)throw new InvalidDataException("Expected all three servers.");
            foreach(var server in directory.Servers)
            {
                if(!Routes.Domains.TryGetValue(server.Id,out var domains)||!server.Routes.SequenceEqual(domains)||server.Release is null)throw new InvalidDataException("Incomplete server catalog.");
                var pack=await client.GetManifest(server.Id,server.Release);
                Json.Write(Path.Combine(root,server.Id+"-manifest.json"),pack);
                Console.WriteLine(JsonSerializer.Serialize(new{pack.Instance,pack.Version,pack.Sequence,Files=pack.Files.Length,JavaMajor=pack.Runtime.Major,SignatureAndHashVerified=true},Json.Options));
            }
            Json.Write(Path.Combine(root,"report.json"),new{directory.Sequence,Servers=directory.Servers.Length,PublicCatalogVerified=true});break;
        }
        case "diff":
        {
            var old=Json.Read<PackManifest>(args[1]);var next=Json.Read<PackManifest>(args[2]);ContentSecurity.Validate(next);
            var before=old.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);var after=next.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
            Console.WriteLine(JsonSerializer.Serialize(new {Added=next.Files.Where(f=>!before.ContainsKey(f.Path)).Select(f=>f.Path),Changed=next.Files.Where(f=>before.TryGetValue(f.Path,out var b)&&b.Sha256!=f.Sha256).Select(f=>f.Path),Removed=old.Files.Where(f=>f.Policy==FilePolicy.Managed&&!after.ContainsKey(f.Path)).Select(f=>f.Path)},Json.Options));break;
        }
        case "probe":
            Console.WriteLine(JsonSerializer.Serialize(await Routes.ProbeAll(args[1]),Json.Options));break;
        case "fetch":
        {
            var file=Json.Read<ContentFile>(args[1]);ContentSecurity.ValidateFile(file);
            var cache=Path.GetFullPath(args[2]);
            if(!cache.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Download verification cache must be an isolated .local directory.");
            using var downloader=new Downloader(cache,new LauncherSettings{LimitMiB=2});
            var result=await downloader.Get(file);
            Console.WriteLine(JsonSerializer.Serialize(new{HashVerified=await ContentSecurity.Matches(result,file),Sha256=file.Sha256},Json.Options));break;
        }
        case "lab-launch":
        {
            var directory=Path.GetFullPath(args[1]);var java=Path.GetFullPath(args[3]);
            if(!directory.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Lab launch is restricted to an isolated .local directory.");
            var isCleanroom=args[2].Contains("cleanroom",StringComparison.OrdinalIgnoreCase);
            await RuntimeManager.Validate(java,isCleanroom?25:args[2].Contains("1.7.10")?8:17);
            if(isCleanroom)await CleanroomAdapter.CompletePrepared(directory,args[2]);
            var launcher=new MinecraftLauncher(new MinecraftPath(directory));
            var options=new MLaunchOption{JavaPath=java,Session=MSession.CreateOfflineSession("Mojin_QA"),MaximumRamMb=isCleanroom?8736:8192,MinimumRamMb=1024,ScreenWidth=1280,ScreenHeight=720,GameLauncherName="MojinDashuai",ExtraJvmArguments=isCleanroom?[MArgument.FromCommandLine("-XX:+UseZGC")]:[]};
            if(args.Length>4){var route=await Routes.Resolve(args[4]);options.ServerIp=route.Host;options.ServerPort=route.Port;}
            var process=await launcher.BuildProcessAsync(args[2],options);
            var cp=System.Text.RegularExpressions.Regex.Match(process.StartInfo.Arguments,"-cp\\s+\"([^\"]+)\"");
            if(cp.Success)Json.Write(Path.Combine(directory,"lab-classpath.json"),cp.Groups[1].Value.Split(Path.PathSeparator));
            process.StartInfo.UseShellExecute=false;process.StartInfo.CreateNoWindow=true;process.StartInfo.RedirectStandardError=true;process.StartInfo.RedirectStandardOutput=true;
            var log=Path.Combine(directory,"lab-launch.log");
            process.Start();Json.Write(Path.Combine(directory,"lab-process.json"),new {process.Id,JavaMajor=isCleanroom?25:17,StartedAt=DateTimeOffset.UtcNow,LaunchVersion=args[2]});
            using var writer=new StreamWriter(log,false);var logGate=new SemaphoreSlim(1);
            async Task Drain(StreamReader reader){string? line;while((line=await reader.ReadLineAsync())is not null){await logGate.WaitAsync();try{await writer.WriteLineAsync(line);await writer.FlushAsync();}finally{logGate.Release();}}}
            Console.WriteLine($"Started isolated client PID {process.Id}; logs are stored in the test directory.");
            await Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError));await process.WaitForExitAsync();Console.WriteLine("Client exit code: "+process.ExitCode);Environment.ExitCode=process.ExitCode;break;
        }
        case "engine-files":
        {
            var directory=Path.GetFullPath(args[1]);
            if(!directory.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Engine preparation requires an isolated .local directory.");
            var launcher=GameLauncher.FromInstalledFiles(directory);
            foreach(var extractor in launcher.FileExtractors.OfType<JavaFileExtractor>().ToArray())launcher.FileExtractors.Remove(extractor);
            var version=await launcher.GetVersionAsync(args[2]);
            var files=new List<object>();
            foreach(var parent in version.EnumerateToParent())foreach(var file in await launcher.ExtractFiles(parent))
            {
                if(file.Path is null)continue;
                var relative=Path.GetRelativePath(directory,file.Path).Replace('\\','/');ContentSecurity.SafePath(directory,relative);
                var copies=file.UpdateTask.OfType<FileCopyTask>().Select(t=>Path.GetRelativePath(directory,t.DestinationPath).Replace('\\','/')).ToArray();
                foreach(var copy in copies)ContentSecurity.SafePath(directory,copy);
                files.Add(new{Path=relative,Sha1=file.Hash,file.Size,file.Url,Copies=copies});
            }
            Json.Write(args[3],new{LaunchVersion=args[2],Files=files});Console.WriteLine($"Extracted {files.Count} engine file references; bundled Java remains separate.");break;
        }
        case "check-install":
        case "check-install-origin":
        {
            var manifest=Json.Read<PackManifest>(args[1]);var root=Path.GetFullPath(args[2]);
            if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installation checks require an isolated .local directory.");
            var unified=args[0]=="check-install-origin";
            var settings=new LauncherSettings{Root=root,Concurrency=4,LimitMiB=unified?8:2};
            using var downloader=new Downloader(Path.Combine(root,"cache"),settings,origin:unified?NetworkPolicy.DirectApi:null);
            var installer=new TransactionalInstaller(root);
            var watch=Stopwatch.StartNew();string phase="";
            var progress=new Progress<TransferProgress>(p=>{if(p.Phase!=phase){phase=p.Phase;Console.WriteLine(phase);}});
            await installer.Install(manifest,downloader,4,progress);
            using var process=await new GameLauncher().Prepare(manifest,settings,MSession.CreateOfflineSession("Mojin_QA"),new RouteEndpoint("offline-check","127.0.0.1",25565,0));
            var command=process.StartInfo.Arguments;
            var match=System.Text.RegularExpressions.Regex.Match(command,"(?:-cp|-classpath)\\s+\"([^\"]+)\"");
            if(match.Success&&match.Groups[1].Value.Split(Path.PathSeparator).Any(p=>!File.Exists(p)))throw new InvalidDataException("Generated classpath contains missing files.");
            var report=new{manifest.Instance,Installed=true,JavaMajor=manifest.Runtime.Major,LocalVersionPrepared=true,GameStarted=false,JoinedServer=false,ElapsedSeconds=watch.Elapsed.TotalSeconds,Files=manifest.Files.Length,SingleOrigin=unified,Origin=unified?NetworkPolicy.DirectApi:null};
            Json.Write(Path.Combine(root,manifest.Instance+"-install-check.json"),report);Console.WriteLine(JsonSerializer.Serialize(report,Json.Options));break;
        }
        case "play-check":
        {
            var manifest=Json.Read<PackManifest>(args[1]);var root=Path.GetFullPath(args[2]);
            if(!root.Contains(Path.DirectorySeparatorChar+".local"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Play checks require an isolated .local directory.");
            if(!Routes.Domains[manifest.Instance].Contains(args[3]))throw new InvalidDataException("Unknown test route.");
            var settings=new LauncherSettings{Root=root};var installer=new TransactionalInstaller(root);using var gate=installer.Acquire(manifest.Instance);
            var route=await Routes.Resolve(args[3]);
            using var process=await new GameLauncher().Prepare(manifest,settings,MSession.CreateOfflineSession("Mojin_QA"),route);
            process.StartInfo.RedirectStandardOutput=true;process.StartInfo.RedirectStandardError=true;
            var instance=installer.InstancePath(manifest.Instance);var log=Path.Combine(root,manifest.Instance+"-play-check.log");
            process.Start();
            Json.Write(Path.Combine(instance,".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime()));
            Json.Write(Path.Combine(root,manifest.Instance+"-play-process.json"),new{process.Id,StartedAt=process.StartTime.ToUniversalTime(),GameName="Mojin_QA",TestPlayer=true,Route=args[3],JavaMajor=manifest.Runtime.Major});
            Console.WriteLine($"Started {manifest.Instance} as test player Mojin_QA on {args[3]}; PID {process.Id}. This does not use the signed-in player's save.");
            using var writer=new StreamWriter(log,false);using var logGate=new SemaphoreSlim(1);
            async Task Drain(StreamReader reader){string? line;while((line=await reader.ReadLineAsync())is not null){await logGate.WaitAsync();try{await writer.WriteLineAsync(line);await writer.FlushAsync();}finally{logGate.Release();}}}
            await Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError));await process.WaitForExitAsync();
            Console.WriteLine($"Client exited: {process.ExitCode}");Environment.ExitCode=process.ExitCode;break;
        }
        default:throw new ArgumentException("Unknown command.");
    }
}
catch(Exception ex){Console.Error.WriteLine(ex is InvalidDataException or ArgumentException or IOException?ex.Message:ex.GetType().Name);Environment.ExitCode=1;}
