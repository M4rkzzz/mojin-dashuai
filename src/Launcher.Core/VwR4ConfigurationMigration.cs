using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

/// <summary>One-time migration of retained r1/r2/r3 rendering seeds to the tested r4 defaults.</summary>
public static class VwR4ConfigurationMigration
{
    public const string MigrationId = "vw-r4-angelica-2.2.11-defaults-v1";
    public const string CompletionMarker = ".hub/compatibility/" + MigrationId + "/completed.json";
    public const string BackupRoot = ".hub/compatibility/" + MigrationId + "/original/";
    private const string ModPath = "mods/angelica-2.2.11.jar";
    private const string ModHash = "85f7955eed1d1f07d9c0da4b09101b793ef5d7db779541067c8542c77bb35b70";
    private static readonly (string Path, long Size, string Hash)[] Seeds =
    [
        ("config/angelica-compat.cfg", 797, "c6880f64cf9027973dae411c7db60b6d31b2482a67a723c7a0c73331c316ce7c"),
        ("config/angelica-modules.cfg", 18609, "e975c39ff180e7afdafac85f7a759d57199cb6f11a52bf3e4d27c7f0b86c3bc0"),
        ("config/angelica-options.json", 1000, "f01e1e143832b252c30fdb52afe70b8ac175771e2c9c96304d1609c1ecde4182"),
        ("config/angelica.cfg", 2385, "a4fa455132daa599ba57d3246649e343e85a9c66b8611be545d2cbe2e28ff4ef")
    ];

    /// <summary>
    /// The caller must verify the manifest signature and hold the instance lock, after a
    /// successful content installation and before starting Java. Returns true only when
    /// this call completes the migration; later launches preserve the player's new choices.
    /// </summary>
    public static async Task<bool> PrepareAsync(string instance, PackManifest manifest, Downloader downloader, CancellationToken token = default)
    {
        if (manifest.Instance != "vw" || manifest.Sequence < 4 || !manifest.Files.Any(file => file.Path == ModPath)) return false;
        ContentSecurity.Validate(manifest);
        var mod = manifest.Files.Single(file => file.Path == ModPath);
        if (mod.Size != 9883686 || mod.Sha256 != ModHash || mod.Policy != FilePolicy.Managed)
            throw new InvalidDataException("四服渲染配置迁移与已验证模组不匹配。");
        var marker = ContentSecurity.SafePath(instance, CompletionMarker);
        if (File.Exists(marker)) return false;
        if (Directory.Exists(marker)) throw new IOException("四服配置迁移记录路径被目录占用。");

        // A retained seed is deliberately absent from the installer's download plan.
        // Resolve only the signed, hash-pinned seed entries, never an untrusted local URL.
        var incoming = Seeds.Select(seed =>
        {
            var file = manifest.Files.SingleOrDefault(file => file.Path == seed.Path);
            if (file is null || file.Size != seed.Size || file.Sha256 != seed.Hash || file.Policy != FilePolicy.Seed || file.OfficialOnly)
                throw new InvalidDataException($"四服配置迁移缺少已验证的默认配置：{seed.Path}");
            return file;
        }).ToArray();
        var changes = new List<Replacement>();
        foreach (var file in incoming)
        {
            token.ThrowIfCancellationRequested();
            var target = ContentSecurity.SafePath(instance, file.Path);
            if (Directory.Exists(target)) throw new IOException($"配置文件位置被目录占用：{file.Path}");
            var original = File.Exists(target) ? await File.ReadAllBytesAsync(target, token).ConfigureAwait(false) : null;
            if (original is not null && Matches(original, file)) continue;
            var cached = await downloader.Get(file, token: token).ConfigureAwait(false);
            var data = await File.ReadAllBytesAsync(cached, token).ConfigureAwait(false);
            if (!Matches(data, file)) throw new InvalidDataException($"四服默认配置校验失败：{file.Path}");
            changes.Add(new(file.Path, target, original, data));
        }

        const string splashPath = "config/splash.properties";
        var splash = ContentSecurity.SafePath(instance, splashPath);
        if (Directory.Exists(splash)) throw new IOException("加载画面配置位置被目录占用。");
        var splashOriginal = File.Exists(splash) ? await File.ReadAllBytesAsync(splash, token).ConfigureAwait(false) : null;
        // Java properties are byte-oriented. Latin1 preserves all unrelated bytes,
        // comments, escape sequences and line endings, including continued values.
        var splashData = Encoding.Latin1.GetBytes(EnableSplash(Encoding.Latin1.GetString(splashOriginal ?? [])));
        if (splashOriginal is null || !splashData.AsSpan().SequenceEqual(splashOriginal))
            changes.Add(new(splashPath, splash, splashOriginal, splashData));

        // Finish every input verification and every backup before replacing any config.
        // Existing backups are the first originals and must survive interrupted retries.
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (change.Original is null) continue;
            var backup = ContentSecurity.SafePath(instance, BackupRoot + change.Relative);
            if (!File.Exists(backup)) WriteAtomic(backup, change.Original, overwrite: false);
        }
        token.ThrowIfCancellationRequested();
        var applied = new List<Replacement>();
        try
        {
            // Like installer commit, this tiny section is not cancellation-interruptible.
            foreach (var change in changes)
            {
                WriteAtomic(change.Target, change.Data, overwrite: true);
                applied.Add(change);
            }
            Json.Write(marker, new { migrationId = MigrationId, version = manifest.Version, sequence = manifest.Sequence, completedAt = DateTimeOffset.UtcNow });
        }
        catch
        {
            // A normal failure restores the state from before this attempt. A process
            // interruption leaves no marker and the next run safely completes the seeds.
            foreach (var change in applied.AsEnumerable().Reverse())
            {
                if (change.Original is null) File.Delete(change.Target);
                else WriteAtomic(change.Target, change.Original, overwrite: true);
            }
            throw;
        }
        return true;
    }

    private static bool Matches(byte[] bytes, ContentFile file) => bytes.LongLength == file.Size
        && Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);

    private sealed record Replacement(string Relative, string Target, byte[]? Original, byte[] Data);

    private static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string EnableSplash(string text)
    {
        var result = new StringBuilder();
        var record = new StringBuilder();
        var found = false;
        var continued = false;
        foreach (Match physical in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\r|\n|$)"))
        {
            if (physical.Length == 0) continue;
            var line = physical.Value;
            var body = line.TrimEnd('\r', '\n');
            var trimmed = body.TrimStart(' ', '\t', '\f');
            var comment = record.Length == 0 && trimmed.Length > 0 && trimmed[0] is '#' or '!';
            record.Append(line);
            continued = !comment && (body.Length - body.TrimEnd('\\').Length) % 2 != 0;
            if (!continued) AppendRecord();
        }
        if (record.Length > 0) AppendRecord();
        if (!found)
        {
            var newline = Regex.Match(text, @"\r\n|\r|\n").Value;
            if (newline.Length == 0) newline = Environment.NewLine;
            if (text.Length > 0 && text[^1] is not ('\r' or '\n')) result.Append(newline);
            if (continued) result.Append(newline);
            result.Append("enabled=true").Append(newline);
        }
        return result.ToString();

        void AppendRecord()
        {
            var value = record.ToString();
            var match = Regex.Match(value, @"\A(?<key>[ \t\f]*enabled)(?=[ \t\f:=\r\n]|$)(?<separator>[ \t\f]*(?:[=:][ \t\f]*)?)");
            if (!match.Success) result.Append(value);
            else
            {
                found = true;
                var end = Regex.Match(value, @"(?:\r\n|\r|\n)\z").Value;
                var oldValue = value[match.Length..(value.Length - end.Length)];
                if (oldValue.Trim(' ', '\t', '\f') == "true") result.Append(value);
                else
                {
                    result.Append(match.Value);
                    if (match.Groups["separator"].Length == 0) result.Append('=');
                    result.Append("true").Append(Regex.Match(oldValue, @"[ \t\f]*\z").Value).Append(end);
                }
            }
            record.Clear();
        }
    }
}
