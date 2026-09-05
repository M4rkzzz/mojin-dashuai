using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class PlayerFilesTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-player-files-"+Guid.NewGuid().ToString("N"));
    private string Dir(string name)=>Path.Combine(root,name);
    private void Put(string name,string content){var path=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content);}
    [Fact]
    public void ImportKeepsCurrentSettingsAndRetainsConflictingOldFilesWithoutCopyingAccounts()
    {
        Put("old/options.txt","old keys");Put("current/options.txt","current keys");Put("old/saves/world/level.dat","old world");
        Put("old/XaeroWaypoints/server/waypoints.txt","markers");Put("old/accounts.json","private fixture");Put("old/frpc.toml","private fixture");Put("old/mods/old.jar","incompatible mod");
        Put("old/xaero/world-map/server/region.zip","modern map");
        Assert.Equal(4,PlayerFiles.Import(Dir("old"),Dir("current")));
        Assert.Equal("current keys",File.ReadAllText(Dir("current/options.txt")));
        var conflict=Assert.Single(Directory.GetFiles(Dir("current/.hub/import-conflicts"),"options.txt",SearchOption.AllDirectories));
        Assert.Equal("old keys",File.ReadAllText(conflict));
        Assert.Equal("old world",File.ReadAllText(Dir("current/saves/world/level.dat")));
        Assert.Equal("markers",File.ReadAllText(Dir("current/XaeroWaypoints/server/waypoints.txt")));
        Assert.Equal("modern map",File.ReadAllText(Dir("current/xaero/world-map/server/region.zip")));
        Assert.False(File.Exists(Dir("current/accounts.json")));Assert.False(File.Exists(Dir("current/frpc.toml")));Assert.False(Directory.Exists(Dir("current/mods")));
        Assert.True(File.Exists(Dir("old/saves/world/level.dat")));
    }
    [Fact]
    public void MigrationCopiesInstalledContentJavaAndPlayerDataWhileKeepingOriginalDirectory()
    {
        Put("old/instances/mb/.hub/installed.json","release record");Put("old/instances/mb/.hub/run.lock","locked");
        Put("old/instances/mb/options.txt","keys");Put("old/instances/mb/journeymap/data/map.dat","map");Put("old/runtimes/java25/bin/javaw.exe","runtime");
        PlayerFiles.CopyForMigration(Dir("old"),Dir("new"));
        Assert.False(File.Exists(Dir("new/instances/mb/.hub/run.lock")));
        foreach(var name in new[]{"instances/mb/.hub/installed.json","instances/mb/options.txt","instances/mb/journeymap/data/map.dat","runtimes/java25/bin/javaw.exe"})
            Assert.Equal(File.ReadAllBytes(Dir("old/"+name)),File.ReadAllBytes(Dir("new/"+name)));
        Assert.True(File.Exists(Dir("old/instances/mb/.hub/run.lock")));
        Assert.Throws<InvalidDataException>(()=>PlayerFiles.CopyForMigration(Dir("old"),Dir("new")));
    }
    [Fact]
    public void NestedDirectoriesAreRejectedBeforeWriting()
    {
        Put("old/options.txt","keep");
        Assert.Throws<InvalidDataException>(()=>PlayerFiles.Import(Dir("old"),Dir("old/imported")));
        Assert.Throws<InvalidDataException>(()=>PlayerFiles.CopyForMigration(Dir("old"),Dir("old/migrated")));
        Assert.False(Directory.Exists(Dir("old/imported")));Assert.False(Directory.Exists(Dir("old/migrated")));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
