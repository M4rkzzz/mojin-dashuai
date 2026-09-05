using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boshan.Launcher;

public sealed record CatalogCheckpoint(long Sequence, string Hash);
public sealed class CatalogClient(string api, IReadOnlyDictionary<string,string> publicKeys, string checkpointPath)
{
    public async Task<Catalog> Fetch(CancellationToken token = default)
    {
        var uri = new Uri(new Uri(api), "/v1/catalog");
        if (uri.Scheme != "https") throw new InvalidDataException("发布目录必须通过 HTTPS 获取。");
        var bytes = await NetworkPolicy.Metadata(uri,"获取服务器目录",token);
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(bytes,Json.Options)!;
        var catalog = ContentSecurity.Verify<Catalog>(envelope,publicKeys);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (catalog.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("发布目录已过期，请重新获取。");
        ValidateMinimumLauncher(catalog.MinimumLauncher,typeof(CatalogClient).Assembly.GetName().Version!);
        if (File.Exists(checkpointPath))
        {
            var old = Json.Read<CatalogCheckpoint>(checkpointPath);
            if (catalog.Sequence < old.Sequence || (catalog.Sequence == old.Sequence && hash != old.Hash)) throw new InvalidDataException("拒绝过期或被替换的发布目录。");
        }
        Json.Write(checkpointPath,new CatalogCheckpoint(catalog.Sequence,hash)); return catalog;
    }
    public static void ValidateMinimumLauncher(string minimum,Version current)
    {
        if(!Version.TryParse(minimum,out var required))throw new InvalidDataException("发布目录的启动器版本要求无效。");
        if(required>current)throw new InvalidDataException("请更新启动器后再安装此版本。");
    }
    public async Task<PackManifest> GetManifest(string instance, ReleaseRef release, CancellationToken token = default)
    {
        if (!ContentSecurity.HashPattern().IsMatch(release.Sha256)) throw new InvalidDataException("发布引用缺少校验信息。");
        var uri = new Uri(release.ManifestUrl);
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidDataException("发布清单来源无效。");
        var bytes = await NetworkPolicy.Metadata(uri,"获取客户端清单",token).ConfigureAwait(false);
        return await Task.Run(()=>{
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布清单哈希无效。");
        var manifest=ContentSecurity.Verify<PackManifest>(JsonSerializer.Deserialize<SignedEnvelope>(bytes,Json.Options)!,publicKeys);
        ContentSecurity.Validate(manifest);
        if (manifest.Instance != instance || manifest.Version != release.Version || manifest.Sequence != release.Sequence || manifest.Compatibility != release.Compatibility) throw new InvalidDataException("清单与选中的服务器版本不一致。");
        return manifest;
        },token).ConfigureAwait(false);
    }
}
