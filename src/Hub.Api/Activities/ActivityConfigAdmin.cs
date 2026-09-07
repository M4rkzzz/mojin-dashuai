namespace Boshan.Hub.Activities;

public static class ActivityConfigAdmin
{
    public static void Run(ActivityCatalog catalog,string[] args)
    {
        if(args.Length is 1 or 2 && args[0]=="show")
        {
            Console.WriteLine(args.Length==2?ActivityJson.Write(catalog.World(args[1])):ActivityJson.Write(new{revision=catalog.Value.Version,worlds=catalog.Value.Worlds.Select(w=>new{w.Id,w.Name,rewards=w.Rewards.Count(r=>!r.Retired)})}));return;
        }
        if(args.Length!=4 || args[0] is not ("validate" or "apply") || !int.TryParse(args[3],out var expected))throw new ArgumentException("activities-config show [world] | validate|apply world|all JSON_PATH EXPECTED_REVISION");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(catalog.Path)!);
        using var writer=new FileStream(catalog.Path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        catalog.Reload();var before=catalog.Value;
        if(before.Version!=expected)throw new ArgumentException("活动版本已变化，请重新读取后发布。");
        var json=File.ReadAllText(args[2]);ActivityCatalogue candidate;
        if(args[1]=="all")candidate=ActivityJson.Read<ActivityCatalogue>(json) with{Version=expected+1};
        else
        {
            var world=ActivityJson.Read<ActivityWorld>(json);
            if(world.Id!=args[1]||!before.Worlds.Any(w=>w.Id==world.Id))throw new ArgumentException("服务器 ID 不符。");
            candidate=before with{Version=expected+1,Worlds=before.Worlds.Select(w=>w.Id==world.Id?world:w).ToArray()};
        }
        candidate=ActivityCatalog.Merge(before,candidate);
        if(args[0]=="apply")
        {
            var history=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(catalog.Path)!,"history");Directory.CreateDirectory(history);
            var previous=System.IO.Path.Combine(history,$"{before.Version}.json");
            if(!File.Exists(previous))File.WriteAllText(previous,ActivityJson.Write(before));
            var next=System.IO.Path.Combine(history,$"{candidate.Version}.json");
            using(var f=new FileStream(next,FileMode.CreateNew,FileAccess.Write))using(var write=new StreamWriter(f))write.Write(ActivityJson.Write(candidate));
            var temp=catalog.Path+".new";File.WriteAllText(temp,ActivityJson.Write(candidate));File.Move(temp,catalog.Path,true);
        }
        Console.WriteLine(ActivityJson.Write(new{applied=args[0]=="apply",revision=candidate.Version,world=args[1],apiRefreshSeconds=5,gameRefreshSeconds=60,restartRequired=false}));
    }
}
