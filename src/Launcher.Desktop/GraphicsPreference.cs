using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Boshan.Desktop;

public sealed record GraphicsPreferenceResult(string Status, string Message, bool Changed, bool Success);

/// <summary>Applies a per-Java Windows preference without changing drivers or requesting elevation.</summary>
public static class GraphicsPreference
{
    private const string JournalName = "graphics-preferences.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GraphicsPreferenceResult Apply(string javaPath, bool enabled, string stateRoot)
    {
        if (!OperatingSystem.IsWindows()) return Result("unsupported", "当前系统不支持 Windows 显卡偏好。");
        return Apply(javaPath, enabled, stateRoot, new WindowsGraphicsPreferenceRegistry());
    }

    public static IReadOnlyList<GraphicsPreferenceResult> RestoreAll(string stateRoot)
    {
        if (!OperatingSystem.IsWindows()) return [];
        return RestoreAll(stateRoot, new WindowsGraphicsPreferenceRegistry());
    }

    // Tests supply an in-memory registry; they never change the computer's GPU settings.
    internal static GraphicsPreferenceResult Apply(string javaPath, bool enabled, string stateRoot, IGraphicsPreferenceRegistry registry)
    {
        try
        {
            var path = ValidateJavaPath(javaPath);
            if (enabled && !File.Exists(path)) return Result("failed", "未找到实际 Java 程序，保留系统显卡设置。", success: false);
            lock (Sync)
            {
                var journalPath = JournalPath(stateRoot);
                using var lease = LockJournal(journalPath);
                var journal = ReadJournal(journalPath);
                var entry = journal.Entries.FirstOrDefault(value => string.Equals(value.JavaPath, path, StringComparison.OrdinalIgnoreCase));
                if (!enabled) return Restore(path, entry, journal, journalPath, registry);

                var current = registry.Read(path);
                if (entry is not null)
                {
                    if (entry.Suppressed || current != entry.Written)
                    {
                        // Keep this marker until the option is disabled: a later launch must
                        // not silently overwrite a preference the player changed in Windows.
                        if (!entry.Suppressed)
                        {
                            journal.Entries[journal.Entries.IndexOf(entry)] = entry with { Suppressed = true };
                            WriteJournal(journalPath, journal);
                        }
                        return Result("external-change", "显卡偏好已由其他程序修改，保留当前设置。");
                    }
                    return Result("already-applied", "已为本次 Java 设置高性能显卡偏好。");
                }

                var written = PreferHighPerformance(current);
                if (written == current) return Result("already-preferred", "本次 Java 已使用高性能显卡偏好。");
                entry = new(path, current, written);
                journal.Entries.Add(entry);
                // Save the exact original value before touching the registry. A crash or
                // failed registry write must never leave an unrecorded override behind.
                WriteJournal(journalPath, journal);
                if (registry.Read(path) != current)
                {
                    journal.Entries[journal.Entries.IndexOf(entry)] = entry with { Suppressed = true };
                    WriteJournal(journalPath, journal);
                    return Result("external-change", "显卡偏好刚被其他程序修改，保留当前设置。");
                }
                registry.Write(path, written);
                return Result("applied", "已为本次 Java 设置高性能显卡偏好。", changed: true);
            }
        }
        catch (Exception exception) { return Failure(exception); }
    }

    internal static IReadOnlyList<GraphicsPreferenceResult> RestoreAll(string stateRoot, IGraphicsPreferenceRegistry registry)
    {
        try
        {
            lock (Sync)
            {
                var journalPath = JournalPath(stateRoot);
                if (!File.Exists(journalPath)) return [];
                using var lease = LockJournal(journalPath);
                var journal = ReadJournal(journalPath);
                var results = new List<GraphicsPreferenceResult>();
                foreach (var entry in journal.Entries.ToArray())
                {
                    try { results.Add(Restore(entry.JavaPath, entry, journal, journalPath, registry)); }
                    catch (Exception exception) { results.Add(Failure(exception)); }
                }
                return results;
            }
        }
        catch (Exception exception) { return [Failure(exception)]; }
    }

    private static GraphicsPreferenceResult Restore(string path, GraphicsPreferenceEntry? entry,
        GraphicsPreferenceJournal journal, string journalPath, IGraphicsPreferenceRegistry registry)
    {
        if (entry is null) return Result("disabled", "保留系统显卡设置。");
        var current = registry.Read(path);
        var owned = !entry.Suppressed && current == entry.Written;
        if (owned)
        {
            if (entry.Original is null) registry.Delete(path);
            else registry.Write(path, entry.Original);
        }
        // Restore first. If saving fails, the stale record cannot cause a future
        // restore to overwrite the original value because it no longer matches Written.
        journal.Entries.Remove(entry);
        WriteJournal(journalPath, journal);
        return owned
            ? Result("restored", "已恢复原有显卡偏好。", changed: true)
            : Result("external-change", "显卡偏好已由其他程序修改，保留当前设置。");
    }

    internal static GraphicsRegistryValue PreferHighPerformance(GraphicsRegistryValue? original)
    {
        var text = original?.Text ?? "";
        var fields = text.Split(';');
        var found = false;
        for (var index = 0; index < fields.Length; index++)
        {
            var equals = fields[index].IndexOf('=');
            if (equals < 0 || !fields[index][..equals].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)) continue;
            fields[index] = fields[index][..(equals + 1)] + "2";
            found = true;
        }
        var updated = found ? string.Join(';', fields) : text + (text.Length == 0 || text.EndsWith(';') ? "" : ";") + "GpuPreference=2;";
        return new(updated, original?.Kind ?? RegistryValueKind.String);
    }

    private static string ValidateJavaPath(string javaPath)
    {
        if (!Path.IsPathFullyQualified(javaPath)) throw new InvalidDataException("Java path must be absolute.");
        var path = Path.GetFullPath(javaPath);
        var name = Path.GetFileName(path);
        if (!name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the selected Java executable may receive a graphics preference.");
        return path;
    }

    private static string JournalPath(string stateRoot)
    {
        if (!Path.IsPathFullyQualified(stateRoot)) throw new InvalidDataException("State path must be absolute.");
        return Path.Combine(Path.GetFullPath(stateRoot), JournalName);
    }

    private static FileStream LockJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Also coordinates separate launcher processes. Contention is reported, never waited on during launch.
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static GraphicsPreferenceJournal ReadJournal(string path)
    {
        if (!File.Exists(path)) return new(1, []);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Graphics preference journal is too large.");
        var journal = JsonSerializer.Deserialize<GraphicsPreferenceJournal>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Graphics preference journal is invalid.");
        if (journal.Version != 1 || journal.Entries is null || journal.Entries.Count > 2048) throw new InvalidDataException("Graphics preference journal is invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (entry is null || !paths.Add(ValidateJavaPath(entry.JavaPath)) || entry.Written is null
                || !ValidValue(entry.Written) || (entry.Original is not null && !ValidValue(entry.Original))
                || PreferHighPerformance(entry.Original) != entry.Written)
                throw new InvalidDataException("Graphics preference journal is invalid.");
        }
        return journal;
    }

    private static bool ValidValue(GraphicsRegistryValue value) => value.Text is not null && value.Text.Length <= 32768
        && value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString;

    private static void WriteJournal(string path, GraphicsPreferenceJournal journal)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, journal, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static GraphicsPreferenceResult Result(string status, string message, bool changed = false, bool success = true) => new(status, message, changed, success);
    private static GraphicsPreferenceResult Failure(Exception exception) => Result("failed",
        exception is UnauthorizedAccessException or SecurityException
            ? "没有权限调整显卡偏好，保留系统设置并继续启动。"
            : exception is InvalidDataException or JsonException or ArgumentException
                ? "显卡偏好记录或 Java 路径无效，保留系统设置并继续启动。"
                : "暂时无法调整显卡偏好，保留系统设置并继续启动。", success: false);
}

internal sealed record GraphicsRegistryValue(string Text, RegistryValueKind Kind = RegistryValueKind.String);
internal sealed record GraphicsPreferenceEntry(string JavaPath, GraphicsRegistryValue? Original, GraphicsRegistryValue Written, bool Suppressed = false);
internal sealed record GraphicsPreferenceJournal(int Version, List<GraphicsPreferenceEntry> Entries);

internal interface IGraphicsPreferenceRegistry
{
    GraphicsRegistryValue? Read(string javaPath);
    void Write(string javaPath, GraphicsRegistryValue value);
    void Delete(string javaPath);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsGraphicsPreferenceRegistry : IGraphicsPreferenceRegistry
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public GraphicsRegistryValue? Read(string javaPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(javaPath, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        var kind = key!.GetValueKind(javaPath);
        if (value is not string text || kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidDataException("Unexpected graphics preference value type.");
        return new(text, kind);
    }
    public void Write(string javaPath, GraphicsRegistryValue value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("Cannot open graphics preference key.");
        key.SetValue(javaPath, value.Text, value.Kind);
    }
    public void Delete(string javaPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(javaPath, throwOnMissingValue: false);
    }
}
