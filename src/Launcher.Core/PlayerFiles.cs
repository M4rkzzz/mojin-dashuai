namespace Boshan.Launcher;

public static class PlayerFiles
{
    private static void SeparateRoots(string source,string destination)
    {
        var from=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        var to=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if(from.Equals(to,StringComparison.OrdinalIgnoreCase)||from.StartsWith(to+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||to.StartsWith(from+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择互相独立的目录。");
        ContentSecurity.SafePath(from,".path-check");ContentSecurity.SafePath(to,".path-check");
    }
    public static int Import(string source,string destination)
    {
        SeparateRoots(source,destination);
        if(!Directory.Exists(source))throw new DirectoryNotFoundException("旧客户端目录不存在。");
        var count=0;var conflict=".hub/import-conflicts/"+Guid.NewGuid().ToString("N")+"/";
        foreach(var name in new[]{"saves","screenshots","journeymap","XaeroWaypoints","XaeroWorldMap","resourcepacks","shaderpacks","options.txt","optionsof.txt"})
        {
            var from=ContentSecurity.SafePath(source,name);if(!File.Exists(from)&&!Directory.Exists(from))continue;
            var files=File.Exists(from)?new[]{from}:Directory.GetFiles(from,"*",SearchOption.AllDirectories);
            foreach(var file in files)
            {
                var relative=Path.GetRelativePath(source,file).Replace('\\','/');ContentSecurity.SafePath(source,relative);
                var target=ContentSecurity.SafePath(destination,relative);
                if(File.Exists(target))target=ContentSecurity.SafePath(destination,conflict+relative);
                TransactionalInstaller.AtomicCopy(file,target);count++;
            }
        }
        return count;
    }
    public static void CopyForMigration(string source,string destination)
    {
        SeparateRoots(source,destination);
        if(!Directory.Exists(source))throw new DirectoryNotFoundException("内容目录不存在。");
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new InvalidDataException("请选择空目录。");
        Directory.CreateDirectory(destination);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,file).Replace('\\','/');ContentSecurity.SafePath(source,relative);
            if(Path.GetFileName(file).Equals("run.lock",StringComparison.OrdinalIgnoreCase))continue;
            TransactionalInstaller.AtomicCopy(file,ContentSecurity.SafePath(destination,relative));
        }
    }
}
