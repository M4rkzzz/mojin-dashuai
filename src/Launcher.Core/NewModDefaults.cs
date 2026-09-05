namespace Boshan.Launcher;

public static class NewModDefaults
{
    // Adding FTB Chunks must not claim the existing Xaero map/zoom shortcuts.
    // Only initialize missing bindings; explicit player bindings always win.
    public static void Prepare(string instance,PackManifest manifest)
    {
        if(manifest.Instance!="dc2"||!manifest.Files.Any(f=>f.Path.StartsWith("mods/ftb-chunks-forge-",StringComparison.OrdinalIgnoreCase)))return;
        var path=ContentSecurity.SafePath(instance,"options.txt");
        var lines=File.Exists(path)?File.ReadAllLines(path).ToList():[];
        var keys=lines.Select(line=>line.Split(':',2)[0]).ToHashSet(StringComparer.Ordinal);
        var changed=false;
        foreach(var key in new[]{"key_key.ftbchunks.map","key_key.ftbchunks.minimap.zoomIn","key_key.ftbchunks.minimap.zoomOut"})
            if(keys.Add(key)){lines.Add(key+":key.keyboard.unknown");changed=true;}
        if(!changed)return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllLines(temporary,lines);File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
