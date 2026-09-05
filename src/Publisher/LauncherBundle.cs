using System.IO.Compression;
using Boshan.Launcher;

internal static class LauncherBundle
{
    internal static async Task Build(string source,string version,long sequence,string publicBase,string output)
    {
        source=Path.GetFullPath(source);output=Path.GetFullPath(output);
        if(output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||source==output)
            throw new InvalidDataException("Bundle output must be outside the published application directory.");
        LauncherVersion.Validate(version);
        if(sequence<=0)
            throw new InvalidDataException("Invalid launcher release version or sequence.");
        var uri=new Uri(publicBase.TrimEnd('/')+"/");
        if(uri.Scheme is not ("http" or "https")||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)
            throw new InvalidDataException("Invalid public download base.");
        Directory.CreateDirectory(output);
        var archivePath=Path.Combine(output,"MojinDashuai-windows-x64.zip");
        if(File.Exists(archivePath)||File.Exists(Path.Combine(output,"launcher-release.json")))throw new IOException("Release output already exists.");
        var inventory=new List<(string Relative,string Full)>();
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative=Path.GetRelativePath(source,file).Replace('\\','/');ContentSecurity.SafePath(source,relative);
            inventory.Add((relative,file));
        }
        // Fixed timestamps and a stable entry order make an unchanged build reproducible.
        using(var zip=ZipFile.Open(archivePath,ZipArchiveMode.Create))
            foreach(var (relative,file) in inventory)
            {
                var entry=zip.CreateEntry(relative,CompressionLevel.Optimal);entry.LastWriteTime=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);
                await using var input=File.OpenRead(file);await using var target=entry.Open();await input.CopyToAsync(target);
            }
        var hash=await ContentSecurity.HashFile(archivePath);
        var archiveRelative="objects/sha256/"+hash;
        var url=new Uri(uri,archiveRelative).AbsoluteUri;
        var archive=new ContentFile(archiveRelative,new FileInfo(archivePath).Length,hash,[url],FilePolicy.Managed,"魔金大帅启动器构建产物");
        var files=new List<ContentFile>();
        foreach(var (relative,file) in inventory)
            files.Add(new(relative,new FileInfo(file).Length,await ContentSecurity.HashFile(file),[url],FilePolicy.Managed,"启动器压缩包内文件，仅从完整签名压缩包提取"));
        var release=new LauncherRelease(sequence,version,"windows-x64",archive,files.ToArray());LauncherUpdates.Validate(release);
        Json.Write(Path.Combine(output,"launcher-release.json"),release);
        Console.WriteLine($"Launcher bundle prepared: {files.Count} files, {archive.Size} bytes, SHA256 {hash}.");
    }
}
