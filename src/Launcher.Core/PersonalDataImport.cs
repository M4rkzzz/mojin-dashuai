using System.Text.Json;

namespace Boshan.Launcher;

public sealed record PersonalImportResult(string Source,int Files,string BackupDirectory)
{
    public string Message=>$"已导入 {Files} 个文件。";
}
public static class PersonalDataImport
{
    private static readonly string[] PersonalRoots=["options.txt","optionsof.txt","optionsshaders.txt","XaeroWorldMap","XaeroWaypoints","xaero","journeymap","resourcepacks","shaderpacks","saves","screenshots"];
    public static string FindInstance(string selected,string id)
    {
        if(!Routes.Domains.ContainsKey(id))throw new InvalidDataException("未知服务器。");
        var root=Path.GetFullPath(selected);ContentSecurity.SafePath(root,".path-check");
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("旧客户端目录不存在。");
        var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path){if(Directory.Exists(path)&&PersonalRoots.Any(name=>File.Exists(Path.Combine(path,name))||Directory.Exists(Path.Combine(path,name))))candidates.Add(path);}
        void Minecraft(string path)
        {
            Add(path);
            var versions=Path.Combine(path,"versions");
            if(Directory.Exists(versions))foreach(var version in Directory.EnumerateDirectories(versions)){ContentSecurity.SafePath(path,"versions/"+Path.GetFileName(version));Add(version);}
        }
        Minecraft(root);Minecraft(Path.Combine(root,".minecraft"));Minecraft(Path.Combine(root,"minecraft"));
        foreach(var child in Directory.EnumerateDirectories(root))
        {
            ContentSecurity.SafePath(root,Path.GetFileName(child));
            Minecraft(Path.Combine(child,".minecraft"));Minecraft(Path.Combine(child,"minecraft"));
        }
        var expected=id switch{"m3e"=>"1.7.10","dc2"=>"1.20.1",_=>"1.12.2"};
        int Score(string path)
        {
            var name=Path.GetFileName(path).ToLowerInvariant();
            var score=id switch{"m3e" when name.Contains("mse")||name.Contains("魔金")=>20,"dc2" when name.Contains("deceased")||name.Contains("亡者")=>20,"mb" when name.Contains("meatball")||name.Contains("肉丸")=>20,_=>0};
            var metadata=Path.Combine(path,Path.GetFileName(path)+".json");
            if(File.Exists(metadata))
            {
                try
                {
                    using var document=JsonDocument.Parse(File.ReadAllText(metadata));
                    var version=document.RootElement.TryGetProperty("inheritsFrom",out var parent)?parent.GetString():null;
                    if(version is "1.7.10" or "1.20.1" or "1.12.2")return version==expected?100:-1;
                    if(document.RootElement.TryGetProperty("id",out var own)&&own.GetString()?.Contains(expected)==true)score+=50;
                }
                catch(JsonException){ }
            }
            return score;
        }
        var matches=candidates.Select(path=>(Path:path,Score:Score(path))).Where(x=>x.Score>=0).OrderByDescending(x=>x.Score).ToArray();
        if(matches.Length==0)throw new InvalidDataException("没有找到对应客户端的个人数据，请选择旧客户端的游戏目录。");
        if(matches.Length>1&&matches[0].Score==matches[1].Score)
        {
            if(matches.Any(x=>x.Path.Equals(root,StringComparison.OrdinalIgnoreCase)))return root;
            throw new InvalidDataException("找到多个游戏实例，请选择需要导入的那个实例目录。");
        }
        return matches[0].Path;
    }
    public static PersonalImportResult Import(string selected,string destination,string id,string[] categories)
    {
        var source=FindInstance(selected,id);destination=Path.GetFullPath(destination);
        if(source.Equals(destination,StringComparison.OrdinalIgnoreCase)||source.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||destination.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("旧客户端与目标目录必须互相独立。");
        if(categories.Length==0||categories.Any(c=>c is not ("settings" or "maps" or "packs" or "worlds")))throw new InvalidDataException("请选择要导入的内容。");
        var plan=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative,string? target=null)
        {
            var path=ContentSecurity.SafePath(source,relative);
            if(File.Exists(path)){var key=target??relative;ContentSecurity.SafePath(destination,key);plan.TryAdd(key,path);return;}
            if(!Directory.Exists(path))return;
            // Check each directory before descending, including junctions in old clients.
            foreach(var file in Directory.EnumerateFiles(path))Add(relative+"/"+Path.GetFileName(file),(target??relative)+"/"+Path.GetFileName(file));
            foreach(var folder in Directory.EnumerateDirectories(path))Add(relative+"/"+Path.GetFileName(folder),(target??relative)+"/"+Path.GetFileName(folder));
        }
        if(categories.Contains("settings"))foreach(var name in new[]{"options.txt","optionsof.txt","optionsshaders.txt","config/xaerominimap.txt","config/xaeroworldmap.txt","config/jei/bookmarks.ini","config/jei/bookmarks.json","config/jei/jei-client.toml","config/jei/worldSettings.ini"})Add(name);
        if(categories.Contains("packs")){Add("resourcepacks");Add("shaderpacks");}
        if(categories.Contains("worlds")){Add("saves");Add("screenshots");Add("schematics");}
        if(categories.Contains("maps"))
        {
            foreach(var folder in new[]{"XaeroWorldMap","XaeroWaypoints","xaero","journeymap"})Add(folder);
            void Profiles(string folder,string active)
            {
                var basePath=ContentSecurity.SafePath(source,folder);if(!Directory.Exists(basePath))return;
                var profiles=Directory.EnumerateDirectories(basePath).Where(path=>!Path.GetFileName(path).Equals("backup",StringComparison.OrdinalIgnoreCase)).ToArray();
                var matching=profiles.Where(path=>Routes.Domains[id].Any(domain=>Path.GetFileName(path).Contains(domain,StringComparison.OrdinalIgnoreCase))||Path.GetFileName(path)==active).ToArray();
                if(matching.Length==0&&profiles.Length==1)matching=profiles;
                foreach(var profile in matching.OrderByDescending(Directory.GetLastWriteTimeUtc))Add(folder+"/"+Path.GetFileName(profile),folder+"/"+active);
            }
            if(id=="m3e"){Profiles("XaeroWorldMap",MapIdentity.SharedFolder);Profiles("XaeroWaypoints",MapIdentity.SharedFolder);}
            if(id=="dc2"){Profiles("xaero/world-map",MapIdentity.SharedFolder);Profiles("xaero/minimap",MapIdentity.SharedFolder);}
            if(id=="mb")Profiles("journeymap/data/mp","Command~Line");
        }
        if(plan.Count==0)throw new InvalidDataException("旧客户端中没有所选内容。");
        var backup=ContentSecurity.SafePath(destination,".hub/personal-import/"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N"));
        var entries=plan.Select(pair=>new{Path=pair.Key,Source=pair.Value,Existed=File.Exists(ContentSecurity.SafePath(destination,pair.Key))}).ToArray();
        foreach(var entry in entries)
        {
            if(Directory.Exists(ContentSecurity.SafePath(destination,entry.Path)))throw new IOException("目标文件位置被文件夹占用："+entry.Path);
            if(entry.Existed)TransactionalInstaller.AtomicCopy(ContentSecurity.SafePath(destination,entry.Path),ContentSecurity.SafePath(backup,"previous/"+entry.Path));
        }
        var attempted=0;
        try
        {
            Json.Write(Path.Combine(backup,"import.json"),new{Source=source,Instance=id,State="importing",Entries=entries});
            foreach(var entry in entries)
            {
                attempted++;TransactionalInstaller.AtomicCopy(entry.Source,ContentSecurity.SafePath(destination,entry.Path));
            }
            Json.Write(Path.Combine(backup,"import.json"),new{Source=source,Instance=id,State="complete",Entries=entries});
        }
        catch
        {
            foreach(var entry in entries.Take(attempted).Reverse())
            {
                var target=ContentSecurity.SafePath(destination,entry.Path);
                if(entry.Existed)TransactionalInstaller.AtomicCopy(ContentSecurity.SafePath(backup,"previous/"+entry.Path),target);
                else if(File.Exists(target))File.Delete(target);
            }
            throw;
        }
        return new(source,entries.Length,backup);
    }
}
