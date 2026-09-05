using System.Text;

namespace Boshan.Launcher;

// Each launcher instance represents one server world. Xaero's built-in
// address-independent mode keeps its two routes on the same map profile.
public static class MapIdentity
{
    public const string SharedFolder="Multiplayer_Any Address";
    public static void Prepare(string instance,string id)
    {
        if(id=="mb")return;
        if(id is not ("m3e" or "dc2"))throw new InvalidDataException("未知地图实例。");
        var folders=id=="m3e"?new[]{"XaeroWorldMap","XaeroWaypoints"}:new[]{"xaero/world-map","xaero/minimap"};
        foreach(var folder in folders)SeedMaps(instance,id,folder);
        foreach(var name in new[]{"xaeroworldmap.txt","xaerominimap.txt"})
        {
            var path=ContentSecurity.SafePath(instance,"config/"+name);
            var original=File.Exists(path)?File.ReadAllText(path):"";
            var lines=original.Split('\n').Select(x=>x.TrimEnd('\r')).ToList();
            var matches=lines.Select((value,index)=>(value,index)).Where(x=>x.value.StartsWith("differentiateByServerAddress:",StringComparison.Ordinal)).ToArray();
            if(matches.Length==1&&matches[0].value=="differentiateByServerAddress:false")continue;
            var backup=ContentSecurity.SafePath(instance,".hub/map-config-backup/"+name);
            if(File.Exists(path)&&!File.Exists(backup))TransactionalInstaller.AtomicCopy(path,backup);
            lines.RemoveAll(x=>x.StartsWith("differentiateByServerAddress:",StringComparison.Ordinal));
            while(lines.Count>0&&lines[^1].Length==0)lines.RemoveAt(lines.Count-1);
            lines.Add("differentiateByServerAddress:false");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temp,string.Join('\n',lines)+"\n",new UTF8Encoding(false));File.Move(temp,path,true);
        }
        Json.Write(ContentSecurity.SafePath(instance,".hub/map-profile.json"),new{Instance=id,AddressIndependent=true,OriginalMapsRetained=true});
    }
    private static void SeedMaps(string instance,string id,string folder)
    {
        var root=ContentSecurity.SafePath(instance,folder);var shared=ContentSecurity.SafePath(root,SharedFolder);
        if(Directory.Exists(shared))return;
        var candidates=Routes.Domains[id].Select(domain=>ContentSecurity.SafePath(root,"Multiplayer_"+domain)).Where(Directory.Exists)
            .Select(path=>(Path:path,Files:Directory.GetFiles(path,"*",SearchOption.AllDirectories)))
            .OrderByDescending(x=>x.Files.Sum(file=>new FileInfo(file).Length)).ThenBy(x=>x.Path,StringComparer.Ordinal).ToArray();
        if(candidates.Length==0)return;
        var stage=ContentSecurity.SafePath(root,".mojin-map-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            foreach(var candidate in candidates)foreach(var file in candidate.Files)
            {
                var relative=Path.GetRelativePath(candidate.Path,file).Replace('\\','/');ContentSecurity.SafePath(candidate.Path,relative);
                var target=ContentSecurity.SafePath(stage,relative);
                // Region formats are opaque: never merge or overwrite competing
                // region files. Every original profile remains available.
                if(!File.Exists(target))TransactionalInstaller.AtomicCopy(file,target);
            }
            Directory.Move(stage,shared);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(ContentSecurity.SafePath(root,Path.GetFileName(stage)),true);}
    }
}
