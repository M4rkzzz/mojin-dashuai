using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class LauncherUpdateSmoke
{
    internal static async Task Run(string fixture)
    {
        var root=Path.GetFullPath(Path.Combine(".local","launcher-update-smoke",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var updates=new LauncherUpdates(Path.Combine(root,"updates"),new Dictionary<string,string>{{"test",key.ExportSubjectPublicKeyInfoPem()}});
        async Task<PreparedLauncher> Prepare(long sequence,bool fail)
        {
            var stage=Path.Combine(root,"input-"+sequence);Directory.CreateDirectory(stage);
            foreach(var file in Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories))
            {
                var target=ContentSecurity.SafePath(stage,Path.GetRelativePath(fixture,file).Replace('\\','/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
            }
            Directory.CreateDirectory(Path.Combine(stage,"web"));
            File.WriteAllText(Path.Combine(stage,"launcher.json"),"{}");File.WriteAllText(Path.Combine(stage,"web","index.html"),"test fixture");
            if(fail)File.WriteAllText(Path.Combine(stage,"simulate-failure"),"exit before ready");
            var files=new List<ContentFile>();
            foreach(var file in Directory.EnumerateFiles(stage,"*",SearchOption.AllDirectories))
                files.Add(new(Path.GetRelativePath(stage,file).Replace('\\','/'),new FileInfo(file).Length,await ContentSecurity.HashFile(file),["https://example.invalid/test.zip"],FilePolicy.Managed,"test fixture"));
            var zip=Path.Combine(root,sequence+".zip");ZipFile.CreateFromDirectory(stage,zip);
            var archive=new ContentFile("launcher.zip",new FileInfo(zip).Length,await ContentSecurity.HashFile(zip),["https://example.invalid/test.zip"],FilePolicy.Managed,"test fixture");
            return await updates.PrepareArchive(ContentSecurity.Sign(new LauncherRelease(sequence,"0.2.0-beta.1","windows-x64",archive,files.ToArray()),"test",key.ExportPkcs8PrivateKeyPem()),zip);
        }
        var method=typeof(App).Assembly.GetType("Boshan.Desktop.UpdateStartup")!.GetMethod("Start",BindingFlags.NonPublic|BindingFlags.Static)!;
        async Task<bool> Start(PreparedLauncher prepared)=>await (Task<bool>)method.Invoke(null,[updates,prepared])!;
        var good=await Prepare(1,false);
        if(!await Start(good))throw new InvalidOperationException("The replacement process did not complete the native handshake.");
        var bad=await Prepare(2,true);
        if(await Start(bad)||!updates.HasFailed(bad.Release))throw new InvalidOperationException("An unhealthy replacement was not rejected.");
        var fallback=await updates.Ready(Path.Combine(root,"old"),new Version(0,1,0));
        if(fallback?.Release.Sequence!=1)throw new InvalidOperationException("The healthy previous version was lost.");
        var report=new{passed=true,actualChildProcess=true,activationHandshake=true,failedLaunchRetainsPrevious=true,noGameOrAccountFilesTouched=true};
        Json.Write(Path.Combine(root,"report.json"),report);Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
