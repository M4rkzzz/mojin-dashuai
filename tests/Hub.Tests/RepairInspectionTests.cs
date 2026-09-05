using System.Security.Cryptography;
using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class RepairInspectionTests
{
    private sealed class Capture : IProgress<TransferProgress>
    {
        public List<TransferProgress> Values {get;}=[];
        public void Report(TransferProgress value)=>Values.Add(value);
    }
    [Fact]
    public async Task InspectionCountsMissingDamagedAndPreservedFilesWithoutChangingPlayerData()
    {
        var root=Path.Combine(Path.GetTempPath(),"mojin-repair-inspection-"+Guid.NewGuid().ToString("N"));var installer=new TransactionalInstaller(root);var instance=installer.InstancePath("dc2");
        var expected=Encoding.UTF8.GetBytes("expected");var hash=Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        ContentFile File(string path,FilePolicy policy=FilePolicy.Managed)=>new(path,expected.Length,hash,["https://fixture.invalid/file"],policy,"fixture");
        var files=new[]{File("mods/good.jar"),File("mods/damaged.jar"),File("mods/missing.jar"),File("config/player.cfg",FilePolicy.Seed),File("options.txt",FilePolicy.Preserve)};
        var pack=new PackManifest("dc2","fixture",1,"1.20.1","forge","47","fixture",new("java17",17,"17","windows-x64",File("runtime.zip"),"bin/java.exe",1),8192,"fixture",files,["fixture"]);
        try
        {
            Directory.CreateDirectory(Path.Combine(instance,"mods"));Directory.CreateDirectory(Path.Combine(instance,"config"));
            await System.IO.File.WriteAllBytesAsync(Path.Combine(instance,"mods","good.jar"),expected);
            await System.IO.File.WriteAllTextAsync(Path.Combine(instance,"mods","damaged.jar"),"modified");
            await System.IO.File.WriteAllTextAsync(Path.Combine(instance,"config","player.cfg"),"player settings");
            await System.IO.File.WriteAllTextAsync(Path.Combine(instance,"options.txt"),"player keys");
            var progress=new Capture();var summary=await installer.InspectFiles(pack,progress);
            Assert.Equal(5,summary.CheckedFiles);Assert.Equal(1,summary.RestoredFiles);Assert.Equal(1,summary.RepairedFiles);
            Assert.Equal(new[]{"mods/damaged.jar","mods/missing.jar"},summary.Changes.Select(file=>file.Path));
            Assert.Equal("player settings",await System.IO.File.ReadAllTextAsync(Path.Combine(instance,"config","player.cfg")));
            Assert.Equal("player keys",await System.IO.File.ReadAllTextAsync(Path.Combine(instance,"options.txt")));
            Assert.Equal("检查本地文件",progress.Values.Last().Phase);Assert.Equal(5,progress.Values.Last().Completed);Assert.Equal(5,progress.Values.Last().Total);
            foreach(var file in summary.Changes)await System.IO.File.WriteAllBytesAsync(Path.Combine(instance,file.Path.Replace('/',Path.DirectorySeparatorChar)),expected);
            var healthy=await installer.InspectFiles(pack);Assert.Empty(healthy.Changes);Assert.Equal(0,healthy.RestoredFiles+healthy.RepairedFiles);Assert.Equal(5,healthy.CheckedFiles);
        }
        finally
        {
            var full=Path.GetFullPath(root);if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("mojin-repair-inspection-",StringComparison.Ordinal))Directory.Delete(full,true);
        }
    }
}
