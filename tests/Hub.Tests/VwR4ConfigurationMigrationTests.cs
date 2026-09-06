using System.Net;
using System.Security.Cryptography;
using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class VwR4ConfigurationMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mojin-vw-r4-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Names = ["angelica-compat.cfg", "angelica-modules.cfg", "angelica-options.json", "angelica.cfg"];
    private string Instance => Path.Combine(root, "四服实例");
    private string Marker => ContentSecurity.SafePath(Instance, VwR4ConfigurationMigration.CompletionMarker);
    private readonly Dictionary<string, byte[]> fixture = LoadFixture();

    private static Dictionary<string, byte[]> LoadFixture()
    {
        var assembly = typeof(VwR4ConfigurationMigrationTests).Assembly;
        return Names.ToDictionary(name => "config/" + name, name =>
        {
            using var input = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(".VwR4." + name, StringComparison.Ordinal)))!;
            using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        });
    }

    private PackManifest Manifest()
    {
        var mod = new ContentFile("mods/angelica-2.2.11.jar", 9883686, "85f7955eed1d1f07d9c0da4b09101b793ef5d7db779541067c8542c77bb35b70", ["https://fixture.invalid/mod"], FilePolicy.Managed, "fixture");
        var files = fixture.Select(pair => new ContentFile(pair.Key, pair.Value.Length, Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(), ["https://fixture.invalid/" + pair.Key], FilePolicy.Seed, "tested r4 defaults")).Append(mod).ToArray();
        return new("vw", "1.1.9.1-boshan-r4", 4, "1.7.10", "forge", "10.13.4.1614", "fixture", new("java8", 8, "8", "windows-x64", mod, "bin/java.exe", 1), 4096, "vw-1.7.10-forge-r1", files, ["local fixture"]);
    }

    private string Put(string relative, byte[] bytes)
    {
        var target = ContentSecurity.SafePath(Instance, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes); return target;
    }
    private byte[] Read(string relative) => File.ReadAllBytes(ContentSecurity.SafePath(Instance, relative));
    private void PutOldSeeds() { foreach (var name in Names) Put("config/" + name, Encoding.UTF8.GetBytes("old player config " + name)); }
    private Downloader Download(FixtureHandler handler) => new(Path.Combine(root, "cache"), new LauncherSettings(), handler);

    [Fact]
    public async Task OldSeedsAreBackedUpAndReplacedOnceWhileOtherSettingsStayByteIdentical()
    {
        PutOldSeeds();
        var splash = Encoding.Latin1.GetBytes("# café\r\nlogo=fml\\:logo.png\r\n  enabled : false  \r\nrotate=false\r\n");
        Put("config/splash.properties", splash);
        var preserve = new Dictionary<string, byte[]> { ["options.txt"] = "guiScale:3\n"u8.ToArray(), ["servers.dat"] = [0, 7, 3], ["XaeroWaypoints/server/waypoints.txt"] = "home"u8.ToArray(), ["saves/world/level.dat"] = [1, 2, 3] };
        foreach (var pair in preserve) Put(pair.Key, pair.Value);
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);

        Assert.True(await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.True(File.Exists(Marker)); Assert.Equal(4, handler.Requests);
        foreach (var pair in fixture)
        {
            Assert.Equal(pair.Value, Read(pair.Key));
            Assert.Equal(Encoding.UTF8.GetBytes("old player config " + Path.GetFileName(pair.Key)), Read(VwR4ConfigurationMigration.BackupRoot + pair.Key));
        }
        Assert.Equal(splash, Read(VwR4ConfigurationMigration.BackupRoot + "config/splash.properties"));
        Assert.Equal(Encoding.Latin1.GetString(splash).Replace("enabled : false", "enabled : true"), Encoding.Latin1.GetString(Read("config/splash.properties")));
        foreach (var pair in preserve) Assert.Equal(pair.Value, Read(pair.Key));

        Put("config/angelica.cfg", "player changed after update"u8.ToArray());
        Put("config/splash.properties", "enabled=false\n"u8.ToArray());
        Assert.False(await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.Equal("player changed after update", Encoding.UTF8.GetString(Read("config/angelica.cfg")));
        Assert.Equal("enabled=false\n", Encoding.UTF8.GetString(Read("config/splash.properties")));
        Assert.Equal(4, handler.Requests);
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NewInstallAlreadyMatchesSoNoDownloadsOrInventedBackupsAreNeeded()
    {
        foreach (var pair in fixture) Put(pair.Key, pair.Value);
        var splash = Put("config/splash.properties", "enabled=true\nrotate=false\n"u8.ToArray());
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(splash, timestamp);
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);
        Assert.True(await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.True(File.Exists(Marker)); Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(ContentSecurity.SafePath(Instance, VwR4ConfigurationMigration.BackupRoot.TrimEnd('/'))));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(splash));
    }

    [Fact]
    public async Task BadLastDownloadCannotModifyEarlierConfigsOrMarkCompletion()
    {
        PutOldSeeds(); var original = fixture.Keys.ToDictionary(path => path, Read);
        using var handler = new FixtureHandler(fixture, corrupt: "config/angelica.cfg"); using var downloader = Download(handler);
        await Assert.ThrowsAsync<IOException>(() => VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.False(File.Exists(Marker)); Assert.True(handler.Requests >= 4);
        foreach (var pair in original) Assert.Equal(pair.Value, Read(pair.Key));
        Assert.False(Directory.Exists(ContentSecurity.SafePath(Instance, VwR4ConfigurationMigration.BackupRoot.TrimEnd('/'))));
    }

    [Fact]
    public async Task InvalidManifestSeedIsRejectedBeforeAnyDownloadOrWrite()
    {
        PutOldSeeds(); var manifest = Manifest();
        manifest = manifest with { Files = manifest.Files.Select(file => file.Path == "config/angelica.cfg" ? file with { Policy = FilePolicy.Managed } : file).ToArray() };
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => VwR4ConfigurationMigration.PrepareAsync(Instance, manifest, downloader));
        Assert.Equal(0, handler.Requests); Assert.False(File.Exists(Marker));
        Assert.Equal("old player config angelica.cfg", Encoding.UTF8.GetString(Read("config/angelica.cfg")));
    }

    [Fact]
    public async Task BackupFailureLeavesConfigsUntouchedAndRetryPreservesFirstOriginal()
    {
        PutOldSeeds(); var original = fixture.Keys.ToDictionary(path => path, Read);
        var blocked = ContentSecurity.SafePath(Instance, VwR4ConfigurationMigration.BackupRoot + "config/angelica-options.json"); Directory.CreateDirectory(blocked);
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);
        await Assert.ThrowsAnyAsync<IOException>(() => VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.False(File.Exists(Marker)); foreach (var pair in original) Assert.Equal(pair.Value, Read(pair.Key));
        Directory.Delete(blocked);
        Put("config/angelica-compat.cfg", "player edit between attempts"u8.ToArray());
        Assert.True(await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader));
        Assert.Equal(original["config/angelica-compat.cfg"], Read(VwR4ConfigurationMigration.BackupRoot + "config/angelica-compat.cfg"));
    }

    [Fact]
    public async Task ContinuedSplashValuesAndUnrelatedPropertiesArePreserved()
    {
        foreach (var pair in fixture) Put(pair.Key, pair.Value);
        const string splash = "# custom\nother=value\\\nenabled=false\nfont=abc\nenabled=\\\n false\nlast=12\n";
        Put("config/splash.properties", Encoding.UTF8.GetBytes(splash));
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);
        await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest(), downloader);
        Assert.Equal("# custom\nother=value\\\nenabled=false\nfont=abc\nenabled=true\nlast=12\n", Encoding.UTF8.GetString(Read("config/splash.properties")));
    }

    [Theory]
    [InlineData("vw", 3)]
    [InlineData("m3e", 4)]
    [InlineData("dc2", 4)]
    [InlineData("mb", 4)]
    public async Task OtherPacksOrOlderVersionsAreNotModified(string id, long sequence)
    {
        using var handler = new FixtureHandler(fixture); using var downloader = Download(handler);
        Assert.False(await VwR4ConfigurationMigration.PrepareAsync(Instance, Manifest() with { Instance = id, Sequence = sequence }, downloader));
        Assert.False(Directory.Exists(Instance)); Assert.Equal(0, handler.Requests);
    }

    private sealed class FixtureHandler(Dictionary<string, byte[]> contents, string? corrupt = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath.TrimStart('/'); var bytes = contents[path].ToArray();
            if (path == corrupt) bytes[0] ^= 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
