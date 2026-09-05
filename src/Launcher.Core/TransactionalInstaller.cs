using System.Diagnostics;

namespace Boshan.Launcher;

public sealed record FileChange(string Path, bool Existed, bool Delete, string? CachePath);
public sealed record UpdateJournal(string Id, string State, PackManifest Target, InstalledPack? Previous, FileChange[] Changes);
public sealed record ActiveGame(int Pid,DateTime StartedAt);
public sealed record FileInspection(int CheckedFiles,int RestoredFiles,int RepairedFiles,ContentFile[] Changes);
public sealed record InstallationSummary(int CheckedFiles,int RestoredFiles,int RepairedFiles,int RemovedFiles,bool RuntimePrepared);

public sealed class TransactionalInstaller(string root)
{
    public bool IsRunning(string id)
    {
        var marker=Path.Combine(InstancePath(id),".hub","active-game.json");
        if(!File.Exists(marker))return false;
        try{var game=Json.Read<ActiveGame>(marker);using var process=Process.GetProcessById(game.Pid);return !process.HasExited&&Math.Abs((process.StartTime.ToUniversalTime()-game.StartedAt).TotalSeconds)<1;}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException){return false;}
    }
    public string InstancePath(string id)
    {
        if (!Routes.Domains.ContainsKey(id)) throw new InvalidDataException("未知服务器。");
        return ContentSecurity.SafePath(root, "instances/" + id);
    }
    public FileStream Acquire(string id)
    {
        var marker=Path.Combine(InstancePath(id),".hub","active-game.json");
        if(File.Exists(marker))
        {
            var game=Json.Read<ActiveGame>(marker);
            try {using var process=Process.GetProcessById(game.Pid);if(!process.HasExited&&Math.Abs((process.StartTime.ToUniversalTime()-game.StartedAt).TotalSeconds)<1)throw new IOException("此世界仍在运行，请先关闭游戏。");}
            catch(ArgumentException) { }
        }
        var path = ContentSecurity.SafePath(InstancePath(id), ".hub/run.lock"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("此世界正在运行或更新。请先关闭游戏。"); }
    }
    public InstalledPack? ReadInstalled(string id)
    {
        var path = Path.Combine(InstancePath(id), ".hub", "installed.json");
        return File.Exists(path) ? Json.Read<InstalledPack>(path) : null;
    }
    public void Recover(string id)
    {
        var instance = InstancePath(id); var path = Path.Combine(instance, ".hub", "journal.json");
        if (!File.Exists(path)) return;
        var journal = Json.Read<UpdateJournal>(path);
        if (journal.State == "committing")
        {
            foreach (var change in journal.Changes)
            {
                var target = ContentSecurity.SafePath(instance, change.Path);
                if (change.Existed)
                {
                    var backup = ContentSecurity.SafePath(instance, $".hub/transactions/{journal.Id}/backup/{change.Path}");
                    if (!File.Exists(backup)) throw new IOException("更新恢复文件缺失，请保留现有目录并联系管理员。");
                    AtomicCopy(backup, target);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
            var ledger = Path.Combine(instance, ".hub", "installed.json");
            if (journal.Previous is null) { if(File.Exists(ledger))File.Delete(ledger); }
            else Json.Write(ledger, journal.Previous);
        }
        File.Delete(path);
    }
    public async Task<FileInspection> InspectFiles(PackManifest manifest,IProgress<TransferProgress>? progress=null,CancellationToken token=default)
    {
        ContentSecurity.Validate(manifest);
        var instance=InstancePath(manifest.Instance);var changed=new List<ContentFile>();var checkedFiles=0;var restored=0;var repaired=0;
        progress?.Report(new(manifest.Instance,"检查本地文件",0,manifest.Files.Length,0));
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            // Manifest paths were validated above. Missing files require no ancestor scan until they are written.
            var candidate=Path.Combine(instance,file.Path.Replace('/',Path.DirectorySeparatorChar));
            if(!File.Exists(candidate)){changed.Add(file);restored++;}
            else
            {
                var path = ContentSecurity.SafePath(instance, file.Path);
                if(file.Policy==FilePolicy.Managed&&!await ContentSecurity.Matches(path,file,token)){changed.Add(file);repaired++;}
            }
            checkedFiles++;progress?.Report(new(manifest.Instance,"检查本地文件",checkedFiles,manifest.Files.Length,0));
        }
        return new(checkedFiles,restored,repaired,changed.ToArray());
    }
    public async Task<InstallationSummary> Install(PackManifest manifest, Downloader downloader, int concurrency, IProgress<TransferProgress>? progress = null, CancellationToken token = default)
    {
        ContentSecurity.Validate(manifest);
        using var instanceLock = Acquire(manifest.Instance);
        Recover(manifest.Instance);
        var instance = InstancePath(manifest.Instance); var previous = ReadInstalled(manifest.Instance);
        var previousFiles = previous?.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ContentFile>(StringComparer.OrdinalIgnoreCase);
        var inspection=await InspectFiles(manifest,progress,token);var changed=inspection.Changes;
        var completeBundle=previous is null?(manifest.Bundles??[]).SingleOrDefault(bundle=>bundle.Complete):null;
        var incoming = manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var retired=previousFiles.Values.Where(f=>f.Policy==FilePolicy.Managed&&!incoming.ContainsKey(f.Path)).ToArray();
        var runtimePrepared=!File.Exists(Path.Combine(RuntimeManager.RuntimeRoot(root,manifest.Runtime),".verified"));
        long transferred = 0; var clock = Stopwatch.StartNew();
        var downloaded = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining=completeBundle is null?changed:changed.Where(file=>file.OfficialOnly).ToArray();
        // Only reserve space that will be newly allocated. A resumed archive and verified
        // cache objects already occupy disk space; ZIP imports cannot reuse a partial object.
        var objects=new Dictionary<string,(ContentFile File,bool Resumable)>(StringComparer.OrdinalIgnoreCase);
        void Reserve(ContentFile file,bool resumable)
        {
            if(objects.TryGetValue(file.Sha256,out var old))resumable&=old.Resumable;
            objects[file.Sha256]=(file,resumable);
        }
        foreach(var file in remaining)Reserve(file,true);
        if(completeBundle is not null)
        {
            Reserve(completeBundle.Archive,true);
            foreach(var file in manifest.Files.Where(file=>!file.OfficialOnly))Reserve(file,false);
            Reserve(manifest.Runtime.Archive,false);
        }
        else if(runtimePrepared)Reserve(manifest.Runtime.Archive,true);
        var available=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        var required=checked(changed.Sum(file=>file.Size)+(runtimePrepared?manifest.Runtime.ExpandedSize:0)+256L*1024*1024);
        foreach(var (hash,reservation) in objects)
        {
            var present=await downloader.Available(reservation.File,token,includePartial:reservation.Resumable);
            available[hash]=present;required=checked(required+reservation.File.Size-present);
        }
        // Reserve both transaction backups and a recoverable copy of any modified old file.
        // Their existing on-disk sizes can be larger than the incoming manifest versions.
        foreach(var file in changed.Concat(retired))
        {
            token.ThrowIfCancellationRequested();
            var target=ContentSecurity.SafePath(instance,file.Path);
            if(File.Exists(target))required=checked(required+new FileInfo(target).Length*2);
        }
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.AvailableFreeSpace < required) throw new IOException($"磁盘空间不足，需要至少 {required / (1024.0*1024*1024):F1} GiB 可用空间。");
        var positions=new System.Collections.Concurrent.ConcurrentDictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in remaining)positions[file.Path]=available[file.Sha256];
        long completed=positions.Values.Sum();
        var downloadTotal=remaining.Sum(file=>file.Size)+(completeBundle?.Archive.Size??0);
        var phase=previous is null?"下载客户端":"正在下载世界内容";
        clock.Restart();
        async Task DownloadRemaining(CancellationToken cancellation)
        {
            if(remaining.Length==0)return;
            progress?.Report(new(manifest.Instance,phase,completed,downloadTotal,0));
            await Parallel.ForEachAsync(remaining, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellation }, async (file, ct) => {
                var path = await downloader.Get(file,count=>Interlocked.Add(ref transferred,count),ct,position=>{
                    var old=positions[file.Path];positions[file.Path]=position;var done=Interlocked.Add(ref completed,position-old);
                    progress?.Report(new(manifest.Instance,phase,Math.Clamp(done,0,downloadTotal),downloadTotal,Interlocked.Read(ref transferred)/Math.Max(1,clock.Elapsed.TotalSeconds)));
                });
                downloaded[file.Path] = path;
            });
        }
        // New first installs use one complete archive. Legacy override bundles are ignored;
        // old manifests, upgrades and repairs continue to use verified per-file differences.
        if(completeBundle is not null)
        {
            progress?.Report(new(manifest.Instance,phase,completed+available[completeBundle.Archive.Sha256],downloadTotal,0));
            var imported=await downloader.PrimeBundle(completeBundle,manifest.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase),
                count=>Interlocked.Add(ref transferred,count),token,
                position=>progress?.Report(new(manifest.Instance,phase,completed+position,downloadTotal,Interlocked.Read(ref transferred)/Math.Max(1,clock.Elapsed.TotalSeconds))),
                runtimeArchive:manifest.Runtime.Archive,
                onExtractionProgress:(position,expanded)=>progress?.Report(new(manifest.Instance,"解压客户端",position,expanded,0)),
                beforeExtract:async cancellation=>{completed+=completeBundle.Archive.Size;await DownloadRemaining(cancellation);});
            foreach(var file in changed.Where(file=>!file.OfficialOnly))downloaded[file.Path]=imported[file.Path];
        }
        else await DownloadRemaining(token);
        await RuntimeManager.Install(root, manifest.Runtime, downloader, token,progress,manifest.Instance);
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N"); var actions = new List<FileChange>();
        var prepared=0;var preparationTotal=changed.Length+retired.Length;
        progress?.Report(new(manifest.Instance,"校验并准备更新",0,preparationTotal,0));
        foreach(var file in changed)
        {
            token.ThrowIfCancellationRequested();
            var target = ContentSecurity.SafePath(instance, file.Path); var exists = File.Exists(target);
            if (exists)
            {
                AtomicCopy(target, ContentSecurity.SafePath(instance, $".hub/transactions/{id}/backup/{file.Path}"));
                if (!previousFiles.TryGetValue(file.Path, out var old) || !await ContentSecurity.Matches(target, old, token))
                    AtomicCopy(target, ContentSecurity.SafePath(instance, $".hub/disabled/{id}/{file.Path}"));
            }
            actions.Add(new(file.Path, exists, false, downloaded[file.Path]));
            prepared++;progress?.Report(new(manifest.Instance,"校验并准备更新",prepared,preparationTotal,0));
        }
        foreach(var old in retired)
        {
            token.ThrowIfCancellationRequested();
            var path = ContentSecurity.SafePath(instance, old.Path); if (!File.Exists(path)){prepared++;progress?.Report(new(manifest.Instance,"校验并准备更新",prepared,preparationTotal,0));continue;}
            // Modified formerly managed files are preserved in the recoverable disabled area.
            AtomicCopy(path, ContentSecurity.SafePath(instance, $".hub/transactions/{id}/backup/{old.Path}"));
            if (!await ContentSecurity.Matches(path, old, token)) AtomicCopy(path, ContentSecurity.SafePath(instance, $".hub/disabled/{id}/{old.Path}"));
            actions.Add(new(old.Path, true, true, null));
            prepared++;progress?.Report(new(manifest.Instance,"校验并准备更新",prepared,preparationTotal,0));
        }
        var journalPath = Path.Combine(instance, ".hub", "journal.json");
        token.ThrowIfCancellationRequested();
        Json.Write(journalPath, new UpdateJournal(id, "committing", manifest, previous, actions.ToArray()));
        // Commit is deliberately not cancelable. Any exception or process interruption is recovered from the journal.
        try
        {
            var applied=0;progress?.Report(new(manifest.Instance, "应用更新", 0, actions.Count, 0));
            foreach(var change in actions)
            {
                var target = ContentSecurity.SafePath(instance, change.Path);
                if (change.Delete) File.Delete(target); else AtomicCopy(change.CachePath!, target);
                applied++;progress?.Report(new(manifest.Instance,"应用更新",applied,actions.Count,0));
            }
            if (previous is not null) Json.Write(Path.Combine(instance, ".hub", "previous.json"), previous);
            Json.Write(Path.Combine(instance, ".hub", "installed.json"), new InstalledPack(manifest, DateTimeOffset.UtcNow));
            Json.Write(journalPath, new UpdateJournal(id, "committed", manifest, previous, actions.ToArray()));
            File.Delete(journalPath);
        }
        catch { Recover(manifest.Instance); throw; }
        return new(inspection.CheckedFiles,inspection.RestoredFiles,inspection.RepairedFiles,actions.Count(action=>action.Delete),runtimePrepared);
    }
    public static void AtomicCopy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".hub-" + Guid.NewGuid().ToString("N");
        using (var input = File.OpenRead(source)) using(var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
        File.Move(temp, target, true);
    }
}
