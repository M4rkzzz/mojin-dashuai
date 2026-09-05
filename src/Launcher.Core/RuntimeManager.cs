using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static partial class RuntimeManager
{
    public static string RuntimeRoot(string root, RuntimeSpec spec) => ContentSecurity.SafePath(root, "runtimes/" + spec.Archive.Sha256.ToLowerInvariant());
    public static async Task Install(string root, RuntimeSpec spec, Downloader downloader, CancellationToken token)
    {
        var path = RuntimeRoot(root, spec); var marker = Path.Combine(path, ".verified");
        if (File.Exists(marker)) { await Validate(ContentSecurity.SafePath(path, spec.JavaPath), spec.Major, token); return; }
        var locks = ContentSecurity.SafePath(root, "runtimes/" + spec.Archive.Sha256 + ".lock"); Directory.CreateDirectory(Path.GetDirectoryName(locks)!);
        await using var gate = new FileStream(locks, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var archive = await downloader.Get(spec.Archive, token: token);
        var temp = path + ".staging-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(temp);
        using(var zip = ZipFile.OpenRead(archive))
        {
            long size = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                var target = ContentSecurity.SafePath(temp, entry.FullName);
                if (!names.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Java 包含重复项或链接。");
                size += entry.Length; if (size > spec.ExpandedSize) throw new InvalidDataException("Java 解压大小超出清单。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open(); await using var output = File.Create(target);
                await input.CopyToAsync(output, token);
            }
        }
        await Validate(ContentSecurity.SafePath(temp, spec.JavaPath), spec.Major, token);
        // Runtime directories are immutable and addressed by archive hash, so another instance cannot be overwritten.
        if (Directory.Exists(path)) throw new IOException("运行环境目录不完整，请通过修复清理该未完成版本。");
        File.WriteAllText(Path.Combine(temp, ".verified"), spec.Archive.Sha256);
        Directory.Move(temp, path);
    }
    [GeneratedRegex("(?:java|openjdk) version \"(?<v>[0-9]+)(?:\\.(?<minor>[0-9]+))?")] private static partial Regex VersionPattern();
    public static async Task Validate(string java, int expected, CancellationToken token = default)
    {
        if (!File.Exists(java)) throw new FileNotFoundException("未找到配套 Java。请修复当前世界。");
        using var process = new Process { StartInfo = new(java) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        process.StartInfo.ArgumentList.Add("-version"); process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token); var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if(!process.HasExited)process.Kill(true); throw; }
        var text = await stderr + await stdout; var match = VersionPattern().Match(text);
        var major = match.Success ? int.Parse(match.Groups["v"].Value) : 0;
        if (major == 1) major = int.Parse(match.Groups["minor"].Value);
        if (process.ExitCode != 0 || major != expected || !text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"此世界必须使用 Java {expected} x64，请使用自动管理的运行环境。");
    }
}
