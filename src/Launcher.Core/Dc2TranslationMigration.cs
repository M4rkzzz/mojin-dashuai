using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

/// <summary>Enables the installed Chinese resource pack once, without replacing player settings.</summary>
public static class Dc2TranslationMigration
{
    public const string ResourcePath = "resourcepacks/DeceasedCraft Chinese Translation Resource.zip";
    public const string ResourceId = "file/DeceasedCraft Chinese Translation Resource.zip";
    public const string MigrationId = "dc2-chinese-resource-pack-v1";
    public const string CompletionMarker = ".hub/compatibility/" + MigrationId + "/completed.json";
    public const string BackupPath = ".hub/compatibility/" + MigrationId + "/original/options.txt";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Call after content installation, under the instance lock and before starting Java.</summary>
    public static async Task<bool> PrepareAsync(string instance, string instanceId, CancellationToken token = default)
    {
        if (instanceId != "dc2" || !File.Exists(ContentSecurity.SafePath(instance, ResourcePath))) return false;
        var marker = ContentSecurity.SafePath(instance, CompletionMarker);
        if (File.Exists(marker)) return false;
        if (Directory.Exists(marker)) throw new IOException("二服设置迁移记录路径被目录占用。");
        var options = ContentSecurity.SafePath(instance, "options.txt");
        if (Directory.Exists(options)) throw new IOException("二服游戏设置路径被目录占用。");
        var original = File.Exists(options) ? await File.ReadAllBytesAsync(options, token).ConfigureAwait(false) : null;
        var bytes = original ?? [];
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Utf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var updatedText = EnableTranslation(text);
        var updated = Utf8.GetBytes(updatedText);
        if (hasBom) updated = [.. Encoding.UTF8.Preamble, .. updated];
        var changed = original is null || !updated.AsSpan().SequenceEqual(original);

        // Back up the first original before making any change. Retries never replace it.
        if (original is not null)
        {
            var backup = ContentSecurity.SafePath(instance, BackupPath);
            if (!File.Exists(backup)) WriteAtomic(backup, original, overwrite: false);
        }
        token.ThrowIfCancellationRequested();
        var applied = false;
        try
        {
            // Commit is short and non-cancellable, with the completion marker written last.
            if (changed) { WriteAtomic(options, updated, overwrite: true); applied = true; }
            WriteAtomic(marker, JsonSerializer.SerializeToUtf8Bytes(new
            {
                migrationId = MigrationId,
                completedAt = DateTimeOffset.UtcNow,
                originalOptionsExisted = original is not null
            }), overwrite: false);
        }
        catch
        {
            if (applied)
            {
                if (original is null) File.Delete(options);
                else WriteAtomic(options, original, overwrite: true);
            }
            throw;
        }
        return true;
    }

    private static string EnableTranslation(string text)
    {
        var newline = Regex.Match(text, @"\r\n|\r|\n").Value;
        if (newline.Length == 0) newline = Environment.NewLine;
        var matches = Regex.Matches(text, @"(?m)^resourcePacks:(?<value>[^\r\n]*)");
        var match = matches.Count == 0 ? null : matches[^1];
        List<string> packs;
        try
        {
            packs = match is null ? [] : JsonSerializer.Deserialize<List<string>>(match.Groups["value"].Value)
                ?? throw new JsonException("Resource pack list is null.");
            if (packs.Any(pack => pack is null)) throw new JsonException("Resource pack list contains null.");
        }
        catch (JsonException error) { throw new InvalidDataException("二服资源包设置格式异常，请检查游戏设置文件。", error); }
        // Minecraft applies later selected packs over earlier packs. Keep every other pack in order.
        packs.RemoveAll(pack => string.Equals(pack, ResourceId, StringComparison.OrdinalIgnoreCase));
        packs.Add(ResourceId);
        var line = "resourcePacks:" + JsonSerializer.Serialize(packs);
        if (match is not null) text = text[..match.Index] + line + text[(match.Index + match.Length)..];
        else text = AppendLine(text, line, newline);
        // Any explicit language, including a non-Chinese choice, belongs to the player.
        if (!Regex.Matches(text, @"(?m)^lang:(?<value>[^\r\n]*)").Any(item => !string.IsNullOrWhiteSpace(item.Groups["value"].Value)))
            text = AppendLine(text, "lang:zh_cn", newline);
        return text;
    }

    private static string AppendLine(string text, string line, string newline) =>
        text + (text.Length > 0 && text[^1] is not ('\r' or '\n') ? newline : "") + line + newline;

    private static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
