using System.Text;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static class LegacySplashCompatibility
{
    private const string Angelica = "mods/angelica-2.1.38.jar";
    private const string Backup = ".hub/compatibility/angelica-2.1.38/splash.properties.original";

    public static void Prepare(string instance, PackManifest manifest)
    {
        if (manifest.Instance != "vw" || !manifest.Files.Any(file => file.Path.Equals(Angelica, StringComparison.OrdinalIgnoreCase))) return;
        var path = ContentSecurity.SafePath(instance, "config/splash.properties");
        var existed = File.Exists(path);
        var original = existed ? File.ReadAllBytes(path) : [];
        // Properties written by Forge are byte-oriented. Latin1 is a reversible
        // mapping here, so comments, escapes and any non-ASCII bytes stay intact.
        var text = Encoding.Latin1.GetString(original);
        var updated = Disable(text);
        if (updated == text) return;
        if (existed)
        {
            var backup = ContentSecurity.SafePath(instance, Backup);
            if (!File.Exists(backup)) WriteAtomic(backup, original, overwrite: false);
        }
        WriteAtomic(path, Encoding.Latin1.GetBytes(updated), overwrite: true);
    }

    private static string Disable(string text)
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
            var comment = record.Length == 0 && body.TrimStart(' ', '\t', '\f').StartsWithAny('#', '!');
            record.Append(line);
            var slashes = body.Length - body.TrimEnd('\\').Length;
            continued = !comment && slashes % 2 != 0;
            if (continued) continue;
            AppendRecord();
        }
        if (record.Length > 0) AppendRecord();
        if (!found)
        {
            var newline = Regex.Match(text, @"\r\n|\r|\n").Value;
            if (newline.Length == 0) newline = Environment.NewLine;
            if (text.Length > 0 && text[^1] is not ('\r' or '\n')) result.Append(newline);
            // A final continued value must end before the new property begins.
            if (continued) result.Append(newline);
            result.Append("enabled=false").Append(newline);
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
                if (oldValue.Trim(' ', '\t', '\f') == "false") result.Append(value);
                else
                {
                    var trailing = Regex.Match(oldValue, @"[ \t\f]*\z").Value;
                    result.Append(match.Value);
                    if (match.Groups["separator"].Length == 0) result.Append('=');
                    result.Append("false").Append(trailing).Append(end);
                }
            }
            record.Clear();
        }
    }

    private static bool StartsWithAny(this string value, char first, char second) => value.Length > 0 && (value[0] == first || value[0] == second);

    private static void WriteAtomic(string path, byte[] data, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
