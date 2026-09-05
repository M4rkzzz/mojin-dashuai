using System.Text.Json;

namespace Boshan.Launcher;

public static class ContentDirectorySetup
{
    public static LauncherSettings LoadSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return new();
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var settings = document.Deserialize<LauncherSettings>(Json.Options) ?? throw new InvalidDataException("设置文件为空。");
        settings.Validate();
        // Older launchers already used the saved root. Keep that choice when upgrading.
        if (!document.RootElement.EnumerateObject().Any(property => property.Name.Equals("contentDirectoryConfigured", StringComparison.OrdinalIgnoreCase)))
        {
            settings.ContentDirectoryConfigured = true;
            Json.Write(settingsPath, settings);
        }
        return settings;
    }

    public static LauncherSettings Complete(LauncherSettings current, string settingsPath, string chosenRoot)
    {
        if (current.ContentDirectoryConfigured) throw new InvalidDataException("保存位置已设置，请在设置中迁移目录。");
        if (string.IsNullOrWhiteSpace(chosenRoot) || !Path.IsPathFullyQualified(chosenRoot.Trim()))
            throw new InvalidDataException("请输入完整的文件夹路径。");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosenRoot.Trim()));
        if (destination.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请在磁盘内选择或新建一个文件夹。");
        var probe = ContentSecurity.SafePath(destination, ".mojin-write-check-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidDataException("请选择空文件夹，或新建一个文件夹。");
        Directory.CreateDirectory(destination);
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            stream.WriteByte(0);
            stream.Flush(true);
        }
        var next = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(current, Json.Options), Json.Options)!;
        next.Root = destination;
        next.ContentDirectoryConfigured = true;
        next.Validate();
        // Persist before returning, so a failed save cannot unlock the lobby in memory.
        Json.Write(settingsPath, next);
        return next;
    }
}
