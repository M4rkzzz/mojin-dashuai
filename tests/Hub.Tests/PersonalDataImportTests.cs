using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class PersonalDataImportTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-import-"+Guid.NewGuid().ToString("N"));
    private string PathOf(string path)=>Path.Combine(root,path.Replace('/',Path.DirectorySeparatorChar));
    private void Put(string path,string value){var target=PathOf(path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,value);}
    [Fact]
    public void PclRootIsDetectedAndOldSettingsReplaceCurrentWithBackup()
    {
        const string old="旧客户端/.minecraft/versions/MSE/";
        Put(old+"MSE.json","{\"inheritsFrom\":\"1.7.10\"}");Put(old+"options.txt","old keys");
        Put(old+"XaeroWaypoints/Multiplayer_mc.m3e.boshan.uk/dim/waypoints.txt","old markers");
        Put(old+"XaeroWorldMap/Multiplayer_mc.m3e.boshan.uk/dim/region.zip","old explored map");
        Put(old+"accounts.json","private");Put(old+"mods/old.jar","never copy");Put(old+"config/server-gameplay.cfg","never copy");
        Put(old+"saves/world/level.dat","optional world");Put(old+"resourcepacks/pack.zip","optional resource pack");
        Put("target/options.txt","current keys");Put("target/XaeroWaypoints/Multiplayer_Any Address/dim/waypoints.txt","current markers");
        var result=PersonalDataImport.Import(PathOf("旧客户端"),PathOf("target"),"m3e",["settings","maps"]);
        Assert.Equal(PathOf(old.TrimEnd('/')),result.Source);
        Assert.Equal("old keys",File.ReadAllText(PathOf("target/options.txt")));
        Assert.Equal("current keys",File.ReadAllText(Path.Combine(result.BackupDirectory,"previous/options.txt")));
        Assert.Equal("old markers",File.ReadAllText(PathOf("target/XaeroWaypoints/Multiplayer_Any Address/dim/waypoints.txt")));
        Assert.Equal("old explored map",File.ReadAllText(PathOf("target/XaeroWorldMap/Multiplayer_Any Address/dim/region.zip")));
        Assert.Equal("old keys",File.ReadAllText(PathOf(old+"options.txt")));
        foreach(var excluded in new[]{"accounts.json","mods","config/server-gameplay.cfg","saves","resourcepacks"})Assert.False(File.Exists(PathOf("target/"+excluded))||Directory.Exists(PathOf("target/"+excluded)));
    }
    [Theory]
    [InlineData("dc2","xaero/minimap","Multiplayer_dc2.mc.boshan.uk","Multiplayer_Any Address")]
    [InlineData("mb","journeymap/data/mp","Minecraft~","Command~Line")]
    public void MapsBecomeVisibleInTheExistingUnifiedProfile(string id,string folder,string oldProfile,string active)
    {
        Put("old/"+folder+"/"+oldProfile+"/waypoints/home.json","old marker");
        Put("current/"+folder+"/"+active+"/waypoints/another.json","keep current marker");
        var result=PersonalDataImport.Import(PathOf("old"),PathOf("current"),id,["maps"]);
        Assert.True(result.Files>=2);
        Assert.Equal("old marker",File.ReadAllText(PathOf("current/"+folder+"/"+active+"/waypoints/home.json")));
        Assert.Equal("keep current marker",File.ReadAllText(PathOf("current/"+folder+"/"+active+"/waypoints/another.json")));
    }
    [Fact]
    public void VersionMismatchAndNestedTargetAreRejectedBeforeWriting()
    {
        Put("old/.minecraft/versions/Meatballcraft/Meatballcraft.json","{\"inheritsFrom\":\"1.12.2\"}");
        Put("old/.minecraft/versions/Meatballcraft/options.txt","keys");
        Assert.Throws<InvalidDataException>(()=>PersonalDataImport.Import(PathOf("old"),PathOf("target"),"m3e",["settings"]));
        Assert.False(Directory.Exists(PathOf("target")));
        Assert.Throws<InvalidDataException>(()=>PersonalDataImport.Import(PathOf("old"),PathOf("old/.minecraft/versions/Meatballcraft/content"),"mb",["settings"]));
    }
    [Fact]
    public void OptionalResourcesAndWorldsRequireSelection()
    {
        Put("old/options.txt","settings");Put("old/resourcepacks/test.zip","pack");Put("old/shaderpacks/test.zip","shader");Put("old/saves/world/level.dat","world");Put("old/screenshots/test.png","screenshot");
        PersonalDataImport.Import(PathOf("old"),PathOf("target"),"m3e",["packs","worlds"]);
        Assert.True(File.Exists(PathOf("target/resourcepacks/test.zip")));Assert.True(File.Exists(PathOf("target/saves/world/level.dat")));
        Assert.False(File.Exists(PathOf("target/options.txt")));
    }
    [Fact]
    public void FailedCopyRestoresCurrentFiles()
    {
        if(!OperatingSystem.IsWindows())return;
        Put("old/options.txt","old");Put("old/XaeroWaypoints/map.txt","locked");Put("target/options.txt","current");
        using(var gate=new FileStream(PathOf("old/XaeroWaypoints/map.txt"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.ThrowsAny<IOException>(()=>PersonalDataImport.Import(PathOf("old"),PathOf("target"),"m3e",["settings","maps"]));
        Assert.Equal("current",File.ReadAllText(PathOf("target/options.txt")));
        Assert.False(File.Exists(PathOf("target/XaeroWaypoints/map.txt")));
    }
    public void Dispose(){var full=Path.GetFullPath(root);if(!full.StartsWith(Path.Combine(Path.GetTempPath(),"mojin-import-"),StringComparison.Ordinal))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
}
