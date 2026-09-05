using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class MapIdentityTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-map-"+Guid.NewGuid().ToString("N"));
    private void Put(string path,string content){var full=ContentSecurity.SafePath(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllText(full,content);}
    [Theory]
    [InlineData("m3e","XaeroWorldMap","XaeroWaypoints")]
    [InlineData("vw","XaeroWorldMap","XaeroWaypoints")]
    [InlineData("dc2","xaero/world-map","xaero/minimap")]
    public void RoutesShareAnInstanceMapWithoutDeletingOldProfilesOrOverwritingPlayerPreferences(string id,string worldMap,string minimap)
    {
        var routes=Routes.Domains[id];
        Put(worldMap+"/Multiplayer_"+routes[0]+"/null/region.zip","larger explored region");
        Put(worldMap+"/Multiplayer_"+routes[1]+"/null/region.zip","other");
        Put(worldMap+"/Multiplayer_"+routes[1]+"/DIM1/new.zip","new");
        Put(minimap+"/Multiplayer_"+routes[0]+"/waypoints.txt","my waypoint");
        Put("config/xaeroworldmap.txt","lighting:false\ndifferentiateByServerAddress:true\n");
        Put("local/ftbutilities/map/region.dat","existing FTB map data");
        MapIdentity.Prepare(root,id);
        Assert.Equal("existing FTB map data",File.ReadAllText(Path.Combine(root,"local/ftbutilities/map/region.dat")));
        Assert.Equal("larger explored region",File.ReadAllText(Path.Combine(root,worldMap,MapIdentity.SharedFolder,"null/region.zip")));
        Assert.Equal("new",File.ReadAllText(Path.Combine(root,worldMap,MapIdentity.SharedFolder,"DIM1/new.zip")));
        Assert.Equal("other",File.ReadAllText(Path.Combine(root,worldMap,"Multiplayer_"+routes[1],"null/region.zip")));
        Assert.Equal("my waypoint",File.ReadAllText(Path.Combine(root,minimap,MapIdentity.SharedFolder,"waypoints.txt")));
        Assert.Equal("lighting:false\ndifferentiateByServerAddress:false\n",File.ReadAllText(Path.Combine(root,"config/xaeroworldmap.txt")));
        Assert.Contains("differentiateByServerAddress:true",File.ReadAllText(Path.Combine(root,".hub/map-config-backup/xaeroworldmap.txt")));
        Put(worldMap+"/"+MapIdentity.SharedFolder+"/null/region.zip","new progress");MapIdentity.Prepare(root,id);
        Assert.Equal("new progress",File.ReadAllText(Path.Combine(root,worldMap,MapIdentity.SharedFolder,"null/region.zip")));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
