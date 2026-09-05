using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class NewModDefaultsTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-new-mod-"+Guid.NewGuid().ToString("N"));
    private static PackManifest Manifest(string instance="dc2",bool chunks=true)
    {
        var file=new ContentFile("mods/ftb-chunks-forge-2001.3.7.jar",1,new string('a',64),["https://example.invalid/mod.jar"],FilePolicy.Managed,"test");
        var runtime=new RuntimeSpec("test",17,"test","windows-x64",file,"bin/java.exe",1);
        return new(instance,"test",2,"1.20.1","forge","47.4.0","test",runtime,8192,"test",chunks?[file]:[],[]);
    }
    [Fact]
    public void NewModDoesNotClaimExistingMapKeysAndExplicitPlayerBindingsWin()
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"options.txt");
        File.WriteAllText(path,"key_key.gui.xaero_open_map:key.keyboard.m\nkey_key.ftbchunks.map:key.keyboard.o\nfov:0.7\n");
        NewModDefaults.Prepare(root,Manifest());
        var result=File.ReadAllText(path);
        Assert.Contains("key_key.gui.xaero_open_map:key.keyboard.m",result);
        Assert.Contains("key_key.ftbchunks.map:key.keyboard.o",result);
        Assert.Contains("key_key.ftbchunks.minimap.zoomIn:key.keyboard.unknown",result);
        Assert.Contains("key_key.ftbchunks.minimap.zoomOut:key.keyboard.unknown",result);
        Assert.Contains("fov:0.7",result);
        NewModDefaults.Prepare(root,Manifest());Assert.Equal(result,File.ReadAllText(path));
    }
    [Fact]
    public void UnrelatedInstancesAndOldPacksAreUntouched()
    {
        NewModDefaults.Prepare(root,Manifest("mb"));
        NewModDefaults.Prepare(root,Manifest(chunks:false));
        Assert.False(Directory.Exists(root));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
