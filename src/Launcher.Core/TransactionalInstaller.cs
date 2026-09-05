using System.Diagnostics;

namespace Boshan.Launcher;

public sealed record FileChange(string Path, bool Existed, bool Delete, string? CachePath);
public sealed record UpdateJournal(string Id, string State, PackManifest Target, InstalledPack? Previous, FileChange[] Changes);
public sealed record ActiveGame(int Pid,DateTime StartedAt);

public sealed class TransactionalInstaller(string root)
{
    public string InstancePath(string id)
    {
        if (id is not ("m3e" or "dc2" or "mb")) throw new InvalidDataException("未知服务器。");
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
    public async Task Install(PackManifest manifest, Downloader downloader, int concurrency, IProgress<TransferProgress>? progress = null, CancellationToken token = default)
    {
        ContentSecurity.Validate(manifest);
        using var instanceLock = Acquire(manifest.Instance);
        Recover(manifest.Instance);
        var instance = InstancePath(manifest.Instance); var previous = ReadInstalled(manifest.Instance);
        var previousFiles = previous?.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ContentFile>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<ContentFile>();
        foreach (var file in manifest.Files)
        {
            // Manifest paths were validated above. Missing files require no ancestor scan until they are written.
            var candidate=Path.Combine(instance,file.Path.Replace('/',Path.DirectorySeparatorChar));
            if(!File.Exists(candidate)){changed.Add(file);continue;}
            var path = ContentSecurity.SafePath(instance, file.Path);
            if (File.Exists(path) && file.Policy is FilePolicy.Seed or FilePolicy.Preserve) continue;
            if (!await ContentSecurity.Matches(path, file, token)) changed.Add(file);
        }
        var total = changed.Sum(f => f.Size); long completed = 0; var clock = Stopwatch.StartNew();
        var required = total * 2 + manifest.Runtime.Archive.Size + manifest.Runtime.ExpandedSize + 256L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.AvailableFreeSpace < required) throw new IOException($"磁盘空间不足，需要至少 {required / (1024.0*1024*1024):F1} GiB 可用空间。");
        // A first install can fetch compressed overrides once; later updates retain per-file differences.
        if(previous is null&&changed.Count>0)foreach(var bundle in manifest.Bundles??[])
        {
            long bundleDone=0;
            progress?.Report(new(manifest.Instance,"下载世界配置",0,bundle.Archive.Size,0));
            await downloader.PrimeBundle(bundle,changed.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase),
                count=>{bundleDone+=count;progress?.Report(new(manifest.Instance,"下载世界配置",bundleDone,bundle.Archive.Size,bundleDone/Math.Max(1,clock.Elapsed.TotalSeconds)));},token);
        }
        var downloaded = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(changed, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (file, ct) => {
            var path = await downloader.Get(file, count => { var done = Interlocked.Add(ref completed, count); progress?.Report(new(manifest.Instance, "正在下载世界内容", Math.Min(total,done), total, done / Math.Max(1,clock.Elapsed.TotalSeconds))); }, ct);
            downloaded[file.Path] = path;
        });
        await RuntimeManager.Install(root, manifest.Runtime, downloader, token);
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N"); var actions = new List<FileChange>();
        var incoming = manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        foreach(var file in changed)
        {
            var target = ContentSecurity.SafePath(instance, file.Path); var exists = File.Exists(target);
            if (exists)
            {
                AtomicCopy(target, ContentSecurity.SafePath(instance, $".hub/transactions/{id}/backup/{file.Path}"));
                if (!previousFiles.TryGetValue(file.Path, out var old) || !await ContentSecurity.Matches(target, old, token))
                    AtomicCopy(target, ContentSecurity.SafePath(instance, $".hub/disabled/{id}/{file.Path}"));
            }
            actions.Add(new(file.Path, exists, false, downloaded[file.Path]));
        }
        foreach(var old in previousFiles.Values.Where(f => f.Policy == FilePolicy.Managed && !incoming.ContainsKey(f.Path)))
        {
            var path = ContentSecurity.SafePath(instance, old.Path); if (!File.Exists(path)) continue;
            // Modified formerly managed files are preserved in the recoverable disabled area.
            AtomicCopy(path, ContentSecurity.SafePath(instance, $".hub/transactions/{id}/backup/{old.Path}"));
            if (!await ContentSecurity.Matches(path, old, token)) AtomicCopy(path, ContentSecurity.SafePath(instance, $".hub/disabled/{id}/{old.Path}"));
            actions.Add(new(old.Path, true, true, null));
        }
        var journalPath = Path.Combine(instance, ".hub", "journal.json");
        Json.Write(journalPath, new UpdateJournal(id, "committing", manifest, previous, actions.ToArray()));
        // Commit is deliberately not cancelable. Any exception or process interruption is recovered from the journal.
        try
        {
            progress?.Report(new(manifest.Instance, "应用更新", total, total, 0));
            foreach(var change in actions)
            {
                var target = ContentSecurity.SafePath(instance, change.Path);
                if (change.Delete) File.Delete(target); else AtomicCopy(change.CachePath!, target);
            }
            if (previous is not null) Json.Write(Path.Combine(instance, ".hub", "previous.json"), previous);
            Json.Write(Path.Combine(instance, ".hub", "installed.json"), new InstalledPack(manifest, DateTimeOffset.UtcNow));
            Json.Write(journalPath, new UpdateJournal(id, "committed", manifest, previous, actions.ToArray()));
            File.Delete(journalPath);
        }
        catch { Recover(manifest.Instance); throw; }
    }
    public static void AtomicCopy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".hub-" + Guid.NewGuid().ToString("N");
        using (var input = File.OpenRead(source)) using(var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
        File.Move(temp, target, true);
    }
}
