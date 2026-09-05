using System.Text;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static class LegacySplashCompatibility
{
    private const string Angelica = "mods/angelica-2.1.38.jar";
    private const string BackupRoot = ".hub/compatibility/angelica-2.1.38/loading-screen/";

    public static void Prepare(string instance, PackManifest manifest)
    {
        if (manifest.Instance != "vw") return;
        // r2 uses Forge's original renderer. Existing r1 installs retain their
        // seeded splash.properties, including the old enabled=false workaround.
        // Repair that setting after the managed Angelica JAR has been retired.
        var nativeForge = manifest.Sequence >= 2 && manifest.Minecraft == "1.7.10" && manifest.Loader == "forge"
            && !manifest.Files.Any(file => file.Path.StartsWith("mods/angelica-", StringComparison.OrdinalIgnoreCase) && file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (nativeForge)
        {
            Update(instance,"splash.properties",EnableSplash);
            return;
        }
        if (!manifest.Files.Any(file => file.Path.Equals(Angelica, StringComparison.OrdinalIgnoreCase))) return;
        // Angelica's font mixin routes Forge's SplashFontRenderer through a texture
        // binding path that Forge rejects. Disable that module before restoring
        // Forge's progress screen; do not leave the whole loading window blank.
        Update(instance,"angelica-modules.cfg",DisableFontBatching);
        Update(instance,"splash.properties",EnableSplash);
    }

    private static void Update(string instance,string file,Func<string,string> change)
    {
        var path = ContentSecurity.SafePath(instance, "config/"+file);
        var existed = File.Exists(path);
        var original = existed ? File.ReadAllBytes(path) : [];
        // Properties written by Forge are byte-oriented. Latin1 is a reversible
        // mapping here, so comments, escapes and any non-ASCII bytes stay intact.
        var text = Encoding.Latin1.GetString(original);
        var updated = change(text);
        if (updated == text) return;
        if (existed)
        {
            var backup = ContentSecurity.SafePath(instance, BackupRoot+file+".original");
            if (!File.Exists(backup)) WriteAtomic(backup, original, overwrite: false);
        }
        WriteAtomic(path, Encoding.Latin1.GetBytes(updated), overwrite: true);
    }

    private static string DisableFontBatching(string text)
    {
        var option = new Regex(@"(?m)^(?<prefix>[ \t]*B:(?:enableFontRenderer|""enableFontRenderer"")[ \t]*=[ \t]*)(?:true|false)(?<suffix>[ \t]*)(?=\r?$)");
        if(option.IsMatch(text))return option.Replace(text,"${prefix}false${suffix}");
        var newline=Regex.Match(text,@"\r\n|\r|\n").Value;
        if(newline.Length==0)newline=Environment.NewLine;
        var general=Regex.Match(text,@"(?m)^[ \t]*(?:general|""general"")[ \t]*\{[ \t]*(?:\r\n|\r|\n|$)");
        if(general.Success)
        {
            var at=general.Index+general.Length;
            var separator=at>0&&text[at-1] is '\r' or '\n'?"":newline;
            return text.Insert(at,separator+"    B:enableFontRenderer=false"+newline);
        }
        return text+(text.Length>0&&text[^1] is not ('\r' or '\n')?newline:"")+"general {"+newline+"    B:enableFontRenderer=false"+newline+"}"+newline;
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
                    var trailing = Regex.Match(oldValue, @"[ \t\f]*\z").Value;
                    result.Append(match.Value);
                    if (match.Groups["separator"].Length == 0) result.Append('=');
                    result.Append("true").Append(trailing).Append(end);
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
