using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boshan.Launcher;

public sealed record LauncherRelease(long Sequence,string Version,string Platform,ContentFile Archive,ContentFile[] Files);
public sealed record PreparedLauncher(LauncherRelease Release,string Directory,string Executable);

// Each update lives in a separate directory. Running executables, account data,
// installed games and their Java runtimes are never replaced by the updater.
public sealed class LauncherUpdates(string root,IReadOnlyDictionary<string,string> publicKeys)
{
    public const string EntryPoint="MojinDashuai.Launcher.exe";
    public string Root=>Path.GetFullPath(root);
    private string ReadyPath=>ContentSecurity.SafePath(Root,"ready.signed.json");
    private string ActivePath=>ContentSecurity.SafePath(Root,"active.signed.json");
    private string PreviousPath=>ContentSecurity.SafePath(Root,"previous.signed.json");
    private string CheckpointPath=>ContentSecurity.SafePath(Root,"checkpoint.json");

    public static void Validate(LauncherRelease release)
    {
        LauncherVersion.Validate(release.Version);
        if(release.Sequence<=0||release.Platform!="windows-x64")
            throw new InvalidDataException("启动器更新信息无效。");
        ContentSecurity.ValidateFile(release.Archive);
        if(release.Archive.Size is <=0 or >536870912||release.Files.Length is <4 or >10000)
            throw new InvalidDataException("启动器更新大小无效。");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total=0;
        foreach(var file in release.Files)
        {
            ContentSecurity.ValidateRelativePath(file.Path);ContentSecurity.ValidateFile(file);
            if(!names.Add(file.Path)||file.Size>1073741824||(total+=file.Size)>1073741824)
                throw new InvalidDataException("启动器更新文件清单无效。");
        }
        if(!new[]{EntryPoint,"MojinDashuai.Launcher.dll","launcher.json","web/index.html"}.All(names.Contains))
            throw new InvalidDataException("启动器更新缺少必要文件。");
    }

    public LauncherRelease AcceptMetadata(SignedEnvelope envelope)
    {
        var release=ContentSecurity.Verify<LauncherRelease>(envelope,publicKeys);Validate(release);
        var hash=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(envelope.Payload)));
        if(File.Exists(CheckpointPath))
        {
            var previous=Json.Read<CatalogCheckpoint>(CheckpointPath);
            if(release.Sequence<previous.Sequence||release.Sequence==previous.Sequence&&hash!=previous.Hash)
                throw new InvalidDataException("拒绝旧版或被替换的启动器更新。");
        }
        Json.Write(CheckpointPath,new CatalogCheckpoint(release.Sequence,hash));return release;
    }

    public async Task<SignedEnvelope?> Fetch(string api,CancellationToken token=default)
    {
        var uri=new Uri(new Uri(api),"/v1/launcher");
        if(uri.Scheme!="https"||uri.UserInfo.Length!=0)throw new InvalidDataException("更新地址必须使用 HTTPS。");
        using var client=new HttpClient(new HttpClientHandler{UseCookies=false}){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=4*1024*1024};
        using var response=await client.GetAsync(uri,token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SignedEnvelope>(await response.Content.ReadAsByteArrayAsync(token),Json.Options)
            ??throw new InvalidDataException("启动器更新信息为空。");
    }

    private string ReleaseDirectory(LauncherRelease release)=>ContentSecurity.SafePath(Root,"releases/"+release.Sequence+"-"+release.Archive.Sha256.ToLowerInvariant());
    private string FailurePath(LauncherRelease release)=>ContentSecurity.SafePath(Root,"failed/"+release.Sequence+"-"+release.Archive.Sha256.ToLowerInvariant()+".signed.json");
    public bool HasFailed(LauncherRelease release)=>File.Exists(FailurePath(release));
    public void Retry(LauncherRelease release){var path=FailurePath(release);if(File.Exists(path))File.Delete(path);}
    public async Task<PreparedLauncher> Prepare(SignedEnvelope envelope,Downloader downloader,Action<long>? progress=null,CancellationToken token=default)
    {
        var release=AcceptMetadata(envelope);
        var archive=await downloader.Get(release.Archive,progress,token);
        return await PrepareArchive(envelope,archive,token);
    }
    public async Task<PreparedLauncher> PrepareArchive(SignedEnvelope envelope,string archive,CancellationToken token=default)
    {
        var release=AcceptMetadata(envelope);
        if(!await ContentSecurity.Matches(archive,release.Archive,token))throw new InvalidDataException("启动器压缩包校验失败。");
        var destination=ReleaseDirectory(release);
        if(!await Complete(destination,release,token))
        {
            var stage=ContentSecurity.SafePath(Root,"stage-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using var zip=ZipFile.OpenRead(archive);
                var expected=release.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if(entry.FullName.EndsWith('/')){ContentSecurity.ValidateRelativePath(entry.FullName.TrimEnd('/'));continue;}
                    ContentSecurity.ValidateRelativePath(entry.FullName);
                    if(!seen.Add(entry.FullName)||((entry.ExternalAttributes>>16)&0xF000)==0xA000||!expected.TryGetValue(entry.FullName,out var file)||entry.Length!=file.Size)
                        throw new InvalidDataException("启动器压缩包与签名清单不符。");
                    var target=ContentSecurity.SafePath(stage,file.Path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using(var input=entry.Open())await using(var output=File.Create(target))
                    {
                        var buffer=new byte[65536];long written=0;int count;
                        while((count=await input.ReadAsync(buffer,token))>0)
                        {
                            if((written+=count)>file.Size)throw new InvalidDataException("启动器文件解压大小超出清单。");
                            await output.WriteAsync(buffer.AsMemory(0,count),token);
                        }
                    }
                    if(!await ContentSecurity.Matches(target,file,token))throw new InvalidDataException("启动器文件校验失败。");
                }
                if(seen.Count!=expected.Count)throw new InvalidDataException("启动器压缩包缺少文件。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(Directory.Exists(destination))Directory.Move(destination,ContentSecurity.SafePath(Root,"damaged-"+Guid.NewGuid().ToString("N")));
                Directory.Move(stage,destination);
            }
            finally{if(Directory.Exists(stage))Directory.Delete(ContentSecurity.SafePath(Root,Path.GetFileName(stage)),true);}
        }
        Json.Write(ReadyPath,envelope);
        return new(release,destination,ContentSecurity.SafePath(destination,EntryPoint));
    }

    private static async Task<bool> Complete(string directory,LauncherRelease release,CancellationToken token)
    {
        if(!Directory.Exists(directory))return false;
        foreach(var file in release.Files)
            if(!await ContentSecurity.Matches(ContentSecurity.SafePath(directory,file.Path),file,token))return false;
        return true;
    }
    public Task<PreparedLauncher?> Ready(string currentDirectory,Version currentVersion,CancellationToken token=default)
        =>Ready(currentDirectory,$"{currentVersion.Major}.{currentVersion.Minor}.{Math.Max(0,currentVersion.Build)}",token);
    public async Task<PreparedLauncher?> Ready(string currentDirectory,string currentVersion,CancellationToken token=default)
    {
        LauncherVersion.Validate(currentVersion);
        foreach(var path in new[]{ReadyPath,ActivePath,PreviousPath})
        {
            if(!File.Exists(path))continue;
            var release=ContentSecurity.Verify<LauncherRelease>(Json.Read<SignedEnvelope>(path),publicKeys);Validate(release);
            if(HasFailed(release))continue;
            if(LauncherVersion.Compare(release.Version,currentVersion)<=0)continue;
            var directory=ReleaseDirectory(release);
            if(Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(directory,StringComparison.OrdinalIgnoreCase))return null;
            if(await Complete(directory,release,token))return new(release,directory,ContentSecurity.SafePath(directory,EntryPoint));
        }
        return null;
    }
    public void Activate(PreparedLauncher prepared)
    {
        foreach(var path in new[]{ReadyPath,ActivePath,PreviousPath})
        {
            if(!File.Exists(path))continue;
            var envelope=Json.Read<SignedEnvelope>(path);
            var release=ContentSecurity.Verify<LauncherRelease>(envelope,publicKeys);
            if(release.Sequence!=prepared.Release.Sequence||release.Archive.Sha256!=prepared.Release.Archive.Sha256)continue;
            if(path!=ActivePath)
            {
                if(File.Exists(ActivePath))Json.Write(PreviousPath,Json.Read<SignedEnvelope>(ActivePath));
                Json.Write(ActivePath,envelope);
            }
            return;
        }
        throw new InvalidDataException("更新版本已改变，请重试。");
    }
    public void Reject(PreparedLauncher prepared)
    {
        foreach(var path in new[]{ReadyPath,ActivePath,PreviousPath})
        {
            if(!File.Exists(path))continue;
            var envelope=Json.Read<SignedEnvelope>(path);
            var release=ContentSecurity.Verify<LauncherRelease>(envelope,publicKeys);
            if(release.Sequence!=prepared.Release.Sequence||release.Archive.Sha256!=prepared.Release.Archive.Sha256)continue;
            Json.Write(FailurePath(release),envelope);File.Delete(path);
        }
    }
}
