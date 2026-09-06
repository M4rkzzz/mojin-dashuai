using System.Text;
using System.Text.Json;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class Dc2TranslationMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mojin-dc2-translation-" + Guid.NewGuid().ToString("N"));
    private string PathOf(string name) => ContentSecurity.SafePath(root, name);
    private void Put(string name, byte[] bytes)
    { var path = PathOf(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
    private void InstallPack() => Put(Dc2TranslationMigration.ResourcePath, [0x50, 0x4b]);
    private string ReadOptions() => File.ReadAllText(PathOf("options.txt"));
    private string[] Packs() => JsonSerializer.Deserialize<string[]>(ReadOptions().Split('\n').Last(line => line.StartsWith("resourcePacks:", StringComparison.Ordinal))["resourcePacks:".Length..])!;

    [Fact]
    public async Task MinimalNewInstallSeedGetsInstalledTranslationAndKeepsExistingSettings()
    {
        InstallPack();
        var original = "lang:zh_cn\nfullscreen:false\n"u8.ToArray();
        Put("options.txt", original);
        Assert.True(await Dc2TranslationMigration.PrepareAsync(root, "dc2"));
        Assert.StartsWith(Encoding.UTF8.GetString(original), ReadOptions());
        Assert.Equal([Dc2TranslationMigration.ResourceId], Packs());
        Assert.Equal(original, File.ReadAllBytes(PathOf(Dc2TranslationMigration.BackupPath)));
        Assert.True(File.Exists(PathOf(Dc2TranslationMigration.CompletionMarker)));
    }

    [Fact]
    public async Task ExistingPlayerKeepsOtherPackOrderLanguageBomAndEveryOtherSetting()
    {
        InstallPack();
        var body = "lang:en_us\r\nguiScale:4\r\nresourcePacks:" + JsonSerializer.Serialize(new[] { "vanilla", "file/玩家资源.zip", Dc2TranslationMigration.ResourceId, "file/scholar.zip", Dc2TranslationMigration.ResourceId }) + "\r\nfullscreen:false\r\nfov:0.75";
        byte[] original = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(body)];
        Put("options.txt", original);
        await Dc2TranslationMigration.PrepareAsync(root, "dc2");
        Assert.Equal(["vanilla", "file/玩家资源.zip", "file/scholar.zip", Dc2TranslationMigration.ResourceId], Packs());
        Assert.StartsWith("lang:en_us\r\nguiScale:4\r\n", ReadOptions());
        Assert.EndsWith("\r\nfullscreen:false\r\nfov:0.75", ReadOptions());
        Assert.True(File.ReadAllBytes(PathOf("options.txt")).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(original, File.ReadAllBytes(PathOf(Dc2TranslationMigration.BackupPath)));
        Assert.DoesNotContain("lang:zh_cn", ReadOptions());
    }

    [Fact]
    public async Task LaterPlayerDisablingTranslationIsPreservedByteForByte()
    {
        InstallPack();
        var original = "resourcePacks:[\"vanilla\"]\nlang:zh_cn\n"u8.ToArray();
        Put("options.txt", original);
        await Dc2TranslationMigration.PrepareAsync(root, "dc2");
        var playerEdit = "resourcePacks:[\"vanilla\",\"file/new.zip\"]\nlang:ja_jp\nguiScale:2\n"u8.ToArray();
        Put("options.txt", playerEdit);
        Assert.False(await Dc2TranslationMigration.PrepareAsync(root, "dc2"));
        Assert.Equal(playerEdit, File.ReadAllBytes(PathOf("options.txt")));
        Assert.Equal(original, File.ReadAllBytes(PathOf(Dc2TranslationMigration.BackupPath)));
    }

    [Fact]
    public async Task MissingOptionsGetDefaultChineseWithoutInventingAnOriginalBackup()
    {
        InstallPack();
        Assert.True(await Dc2TranslationMigration.PrepareAsync(root, "dc2"));
        Assert.Contains("lang:zh_cn", ReadOptions());
        Assert.Equal([Dc2TranslationMigration.ResourceId], Packs());
        Assert.False(File.Exists(PathOf(Dc2TranslationMigration.BackupPath)));
    }

    [Theory]
    [InlineData("dc2", false)]
    [InlineData("m3e", true)]
    [InlineData("mb", true)]
    [InlineData("vw", true)]
    public async Task MissingPackOrDifferentServerDoesNotModifyOptionsOrMarkCompletion(string instanceId, bool installed)
    {
        if (installed) InstallPack();
        var original = "guiScale:3\n"u8.ToArray();
        Put("options.txt", original);
        Assert.False(await Dc2TranslationMigration.PrepareAsync(root, instanceId));
        Assert.Equal(original, File.ReadAllBytes(PathOf("options.txt")));
        Assert.False(File.Exists(PathOf(Dc2TranslationMigration.CompletionMarker)));
    }

    [Fact]
    public async Task InvalidResourceListLeavesTheOriginalAndDoesNotMarkCompletion()
    {
        InstallPack();
        var original = "resourcePacks:not-json\nlang:en_us\n"u8.ToArray();
        Put("options.txt", original);
        await Assert.ThrowsAsync<InvalidDataException>(() => Dc2TranslationMigration.PrepareAsync(root, "dc2"));
        Assert.Equal(original, File.ReadAllBytes(PathOf("options.txt")));
        Assert.False(File.Exists(PathOf(Dc2TranslationMigration.CompletionMarker)));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
