using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boshan.Launcher;

public sealed record CatalogCheckpoint(long Sequence, string Hash);
public sealed class CatalogClient(string api, IReadOnlyDictionary<string,string> publicKeys, string checkpointPath)
{
    private readonly HttpClient client = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 16*1024*1024 };
    public async Task<Catalog> Fetch(CancellationToken token = default)
    {
        if(client.DefaultRequestHeaders.UserAgent.Count==0)client.DefaultRequestHeaders.UserAgent.ParseAdd("MojinDashuai/"+typeof(CatalogClient).Assembly.GetName().Version);
        var uri = new Uri(new Uri(api), "/v1/catalog");
        if (uri.Scheme != "https") throw new InvalidDataException("发布目录必须通过 HTTPS 获取。");
        using var response = await client.GetAsync(uri, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("正式内容尚未发布，或目录服务暂时不可用。请稍后重试。", null, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
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
        var bytes = await client.GetByteArrayAsync(uri,token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布清单哈希无效。");
        var manifest=ContentSecurity.Verify<PackManifest>(JsonSerializer.Deserialize<SignedEnvelope>(bytes,Json.Options)!,publicKeys);
        ContentSecurity.Validate(manifest);
        if (manifest.Instance != instance || manifest.Version != release.Version || manifest.Sequence != release.Sequence || manifest.Compatibility != release.Compatibility) throw new InvalidDataException("清单与选中的服务器版本不一致。");
        return manifest;
    }
}
