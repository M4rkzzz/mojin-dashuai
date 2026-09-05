using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boshan.Launcher;

public static partial class ContentSecurity
{
    [GeneratedRegex("^[a-fA-F0-9]{64}$")] public static partial Regex HashPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DeviceName();
    public static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Any(char.IsControl)) throw new InvalidDataException("清单路径不安全。");
        var pieces = relative.Split('/');
        if (pieces.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(['*','?','"','<','>','|']) >= 0 || DeviceName().IsMatch(s))) throw new InvalidDataException("清单包含无效文件名。");
    }
    public static string SafePath(string root, string relative)
    {
        ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件超出实例目录。");
        // A valid lexical path must not escape through an existing junction or symlink.
        for (var check = path; check is not null; check = Path.GetDirectoryName(check))
            if ((File.Exists(check) || Directory.Exists(check)) && File.GetAttributes(check).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("内容路径中不能使用符号链接或目录联接。");
        return path;
    }
    public static void Validate(PackManifest manifest)
    {
        var expected = manifest.Instance switch { "m3e" => 8, "dc2" => 17, "mb" => 25, _ => throw new InvalidDataException("未知实例。") };
        if (manifest.Runtime.Major != expected || manifest.Runtime.Platform != "windows-x64") throw new InvalidDataException("运行环境不符合此服务器的要求。");
        if (manifest.Instance == "mb" && (manifest.Loader != "cleanroom" || manifest.Minecraft != "1.12.2")) throw new InvalidDataException("肉丸工艺只支持 Cleanroom + Java 25。");
        if (manifest.Instance != "mb" && manifest.Loader != "forge") throw new InvalidDataException("服务器加载器不匹配。");
        if (manifest.Sequence <= 0 || string.IsNullOrWhiteSpace(manifest.Compatibility) || manifest.ValidationEvidence.Length == 0) throw new InvalidDataException("此版本尚未完成发布验收。");
        if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length) throw new InvalidDataException("清单有重复文件路径。");
        var officialHashes=manifest.Files.Where(file=>file.OfficialOnly).Select(file=>file.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(manifest.Files.Append(manifest.Runtime.Archive).Any(file=>!file.OfficialOnly&&officialHashes.Contains(file.Sha256)))throw new InvalidDataException("仅限官方分发的文件不能以普通文件别名分发。");
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.Path); ValidateFile(file);
            var prefix = file.Path.Split('/')[0];
            if (prefix.Equals("__runtime", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布清单不能占用客户端包的运行环境目录。");
            if (new[] { ".hub", "saves", "screenshots", "logs", "crash-reports", "backups", "journeymap", "XaeroWaypoints", "XaeroWorldMap", "xaero" }.Contains(prefix, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("发布清单不能管理玩家数据。");
            if (file.Path.Equals("options.txt", StringComparison.OrdinalIgnoreCase) && file.Policy == FilePolicy.Managed) throw new InvalidDataException("玩家设置不能强制覆盖。");
        }
        ValidateFile(manifest.Runtime.Archive);
        if ((manifest.Bundles??[]).Count(bundle=>bundle.Complete)>1) throw new InvalidDataException("发布清单只能指定一个完整客户端包。");
        if ((manifest.Bundles??[]).Any(bundle=>bundle.Complete)&&manifest.Runtime.Archive.OfficialOnly) throw new InvalidDataException("完整客户端包的运行环境必须允许随包分发。");
        foreach(var bundle in manifest.Bundles??[])
        {
            ValidateFile(bundle.Archive);
            if(bundle.Complete&&bundle.Prefix.Length!=0)throw new InvalidDataException("完整客户端包必须从 ZIP 根目录提供文件。");
            if(bundle.Prefix.Length>0)ValidateRelativePath(bundle.Prefix.TrimEnd('/'));
            if(bundle.Prefix.Length>0&&!bundle.Prefix.EndsWith('/'))throw new InvalidDataException("内容包目录前缀无效。");
        }
        ValidateRelativePath(manifest.Runtime.JavaPath);
        ValidateRelativePath(manifest.Runtime.Id);
        if (manifest.Runtime.ExpandedSize <= 0) throw new InvalidDataException("Java 解压大小无效。");
    }
    public static void ValidateFile(ContentFile file)
    {
        if (file.Size < 0 || !HashPattern().IsMatch(file.Sha256) || file.Sources.Length == 0 || string.IsNullOrWhiteSpace(file.DistributionBasis)) throw new InvalidDataException("必需文件缺少大小、哈希、来源或分发依据。");
        foreach (var source in file.Sources)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0) throw new InvalidDataException("文件来源地址无效。");
            if(file.OfficialOnly&&(file.Sources.Length!=1||uri.Scheme!="https"||!uri.IsDefaultPort||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath=="/"
                ||!new[]{"cdn.modrinth.com","mediafilez.forgecdn.net","edge.forgecdn.net"}.Contains(uri.Host,StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("仅限官方分发的文件必须指定一条匿名 HTTPS 官方固定下载链接。");
        }
    }
    public static async Task<bool> Matches(string path, ContentFile file, CancellationToken token = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        return string.Equals(await HashFile(path, token), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<string> HashFile(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    public static T Verify<T>(SignedEnvelope envelope, IReadOnlyDictionary<string, string> publicKeys)
    {
        if (!publicKeys.TryGetValue(envelope.KeyId, out var pem)) throw new InvalidDataException("发布签名密钥未知。");
        var data = Convert.FromBase64String(envelope.Payload);
        using var ecdsa = ECDsa.Create(); ecdsa.ImportFromPem(pem);
        if (ecdsa.KeySize != 256 || !ecdsa.VerifyData(data, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new InvalidDataException("发布清单签名无效。");
        return JsonSerializer.Deserialize<T>(data, Json.Options) ?? throw new InvalidDataException("发布清单为空。");
    }
    public static SignedEnvelope Sign<T>(T value, string keyId, string privateKey)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(Json.Options){WriteIndented=false});
        using var key = ECDsa.Create(); key.ImportFromPem(privateKey);
        if (key.KeySize != 256) throw new InvalidDataException("只接受 ECDSA P-256。");
        return new(keyId, Convert.ToBase64String(bytes), Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }
}
