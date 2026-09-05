using System.Text.Json;
using System.Text.Json.Serialization;

namespace Boshan.Launcher;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("文件内容为空。");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, value, Options); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
public enum FilePolicy { Managed, Seed, Preserve }
public sealed record ContentFile(string Path, long Size, string Sha256, string[] Sources, FilePolicy Policy, string DistributionBasis);
public sealed record RuntimeSpec(string Id, int Major, string Version, string Platform, ContentFile Archive, string JavaPath, long ExpandedSize);
public sealed record ContentBundle(ContentFile Archive, string Prefix);
public sealed record PackManifest(string Instance, string Version, long Sequence, string Minecraft, string Loader, string LoaderVersion, string LaunchVersion, RuntimeSpec Runtime, int MemoryMiB, string Compatibility, ContentFile[] Files, string[] ValidationEvidence, ContentBundle[]? Bundles = null);
public sealed record SignedEnvelope(string KeyId, string Payload, string Signature);
public sealed record ReleaseRef(string Version, long Sequence, string ManifestUrl, string Sha256, string Compatibility);
public sealed record ServerCatalog(string Id, string Name, string[] Routes, ReleaseRef? Release, ReleaseRef[] Rollbacks);
public sealed record Catalog(long Sequence, string MinimumLauncher, DateTimeOffset ExpiresAt, ServerCatalog[] Servers);
public sealed record InstalledPack(PackManifest Manifest, DateTimeOffset InstalledAt);
public sealed record TransferProgress(string Instance, string Phase, long Completed, long Total, double BytesPerSecond, bool Paused = false);
public sealed class LauncherSettings
{
    public string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "content");
    public bool ContentDirectoryConfigured { get; set; }
    public Dictionary<string, int> Memory { get; set; } = new() { ["m3e"] = 8192, ["dc2"] = 8192, ["mb"] = 8736 };
    public Dictionary<string, string> Java { get; set; } = new() { ["m3e"] = "", ["dc2"] = "", ["mb"] = "" };
    public Dictionary<string, string> Jvm { get; set; } = new() { ["m3e"] = "", ["dc2"] = "", ["mb"] = "-XX:+UseZGC" };
    public Dictionary<string, string> SelectedRoutes { get; set; } = new() { ["m3e"] = "auto", ["dc2"] = "auto", ["mb"] = "auto" };
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public string WindowBehavior { get; set; } = "keep";
    public int Concurrency { get; set; } = 4;
    public int LimitMiB { get; set; }
    public string Proxy { get; set; } = "";
    public string ProxyMode { get; set; } = "direct";
    public string SkinSource { get; set; } = "account";
    public bool ReducedMotion { get; set; }
    public string Theme { get; set; } = "dark";
    public void Validate()
    {
        if (ProxyMode is not ("direct" or "system" or "manual") || SkinSource is not ("account" or "littleskin")) throw new InvalidDataException("网络或皮肤来源设置无效。");
        if (ProxyMode == "manual" && string.IsNullOrWhiteSpace(Proxy)) throw new InvalidDataException("请填写代理地址。");
        if (!Path.IsPathFullyQualified(Root) || Concurrency is < 1 or > 16 || LimitMiB is < 0 or > 10240 || Width is < 640 or > 16384 || Height is < 480 or > 16384 || Memory.Values.Any(m => m is < 1024 or > 65536)) throw new InvalidDataException("请检查目录、内存、窗口或下载设置的范围。");
        if (Proxy.Length > 0 && (!Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5") || proxy.UserInfo.Length != 0)) throw new InvalidDataException("代理地址无效，请勿在代理地址中保存账号凭据。");
        foreach (var id in new[] { "m3e", "dc2", "mb" }) if (!Memory.ContainsKey(id) || !Java.ContainsKey(id) || !Jvm.ContainsKey(id) || !SelectedRoutes.ContainsKey(id) || SelectedRoutes[id] is not ("auto" or "0" or "1")) throw new InvalidDataException("实例设置不完整。");
        if (WindowBehavior is not ("keep" or "minimize" or "hide")) throw new InvalidDataException("窗口设置无效。");
        if (Theme is not ("dark" or "magic" or "waste" or "industry")) throw new InvalidDataException("主题设置无效。");
    }
}
