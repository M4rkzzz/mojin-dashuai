using System.Text.Json.Nodes;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class ContentDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mojin-directory-test-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(root, "launcher", "settings.json");

    [Fact]
    public void NewInstallationWaitsForDirectorySelection()
    {
        var programDirectory=Path.Combine(root,"魔金大帅 安装目录");
        var loaded=ContentDirectorySetup.LoadSettings(SettingsPath,programDirectory);
        Assert.False(loaded.ContentDirectoryConfigured);
        Assert.Equal(Path.Combine(programDirectory,"content"),loaded.Root);
        Assert.False(Directory.Exists(loaded.Root));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void ConfirmedDirectorySurvivesDifferentProgramAndUpdateDirectories()
    {
        var original=Path.Combine(root,"魔金大帅","content");
        Json.Write(SettingsPath,new LauncherSettings{Root=original,ContentDirectoryConfigured=true});
        Assert.Equal(original,ContentDirectorySetup.LoadSettings(SettingsPath,Path.Combine(root,"updates","release-7")).Root);
    }

    [Fact]
    public void UpgradePreservesLegacyDirectoryAndSettings()
    {
        var old = new LauncherSettings { Root = Path.Combine(root, "existing-games"), Concurrency = 7 };
        Json.Write(SettingsPath, old);
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        json.Remove("contentDirectoryConfigured");
        File.WriteAllText(SettingsPath, json.ToJsonString());
        var loaded = ContentDirectorySetup.LoadSettings(SettingsPath);
        Assert.True(loaded.ContentDirectoryConfigured);
        Assert.Equal(old.Root, loaded.Root);
        Assert.Equal(7, loaded.Concurrency);
        Assert.True(ContentDirectorySetup.LoadSettings(SettingsPath).ContentDirectoryConfigured);
        Assert.False(Directory.Exists(old.Root));
    }

    [Fact]
    public void ThreeServerSettingsGainFourthWithoutLosingCustomSettings()
    {
        var old=new LauncherSettings{Root=Path.Combine(root,"old-games"),ContentDirectoryConfigured=true};
        old.Memory["m3e"]=6144;old.SelectedRoutes["mb"]="1";
        old.Memory.Remove("vw");old.Java.Remove("vw");old.Jvm.Remove("vw");old.SelectedRoutes.Remove("vw");
        Json.Write(SettingsPath,old);
        var loaded=ContentDirectorySetup.LoadSettings(SettingsPath);
        Assert.Equal(4096,loaded.Memory["vw"]);Assert.Equal("auto",loaded.SelectedRoutes["vw"]);
        Assert.Equal(6144,loaded.Memory["m3e"]);Assert.Equal("1",loaded.SelectedRoutes["mb"]);
        Assert.Equal(old.Root,loaded.Root);Assert.True(loaded.ContentDirectoryConfigured);
    }

    [Fact]
    public void IncompleteSavedSettingsStillRequireSelection()
    {
        Json.Write(SettingsPath, new LauncherSettings { Root = Path.Combine(root, "games") });
        var program=Path.Combine(root,"程序");
        var loaded=ContentDirectorySetup.LoadSettings(SettingsPath,program);
        Assert.False(loaded.ContentDirectoryConfigured);
        Assert.Equal(Path.Combine(program,"content"),loaded.Root);
    }

    [Fact]
    public void DirectoryChoicePersistsWithoutDownloadingOrTouchingOldRoot()
    {
        var current = new LauncherSettings { Root = Path.Combine(root, "old-default"), Concurrency = 6 };
        var selected = Path.Combine(root, "游戏文件");
        var saved = ContentDirectorySetup.Complete(current, SettingsPath, selected);
        Assert.True(saved.ContentDirectoryConfigured);
        Assert.Equal(selected, ContentDirectorySetup.LoadSettings(SettingsPath).Root);
        Assert.Equal(6, saved.Concurrency);
        Assert.False(current.ContentDirectoryConfigured);
        Assert.False(Directory.Exists(current.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(selected));
        Assert.Throws<InvalidDataException>(() => ContentDirectorySetup.Complete(saved, SettingsPath, Path.Combine(root, "second")));
    }

    [Fact]
    public void NonemptyFolderCannotBeOverwrittenBySetup()
    {
        var selected = Path.Combine(root, "personal-files");
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "keep.txt"), "player data");
        Assert.Throws<InvalidDataException>(() => ContentDirectorySetup.Complete(new(), SettingsPath, selected));
        Assert.Equal("player data", File.ReadAllText(Path.Combine(selected, "keep.txt")));
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-folder")]
    public void InvalidPathDoesNotCompleteSetup(string selected)
    {
        Assert.Throws<InvalidDataException>(() => ContentDirectorySetup.Complete(new(), SettingsPath, selected));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void FailedSettingsWriteDoesNotCompleteSetup()
    {
        Directory.CreateDirectory(root);
        var blockedParent = Path.Combine(root, "a-file");
        File.WriteAllText(blockedParent, "keep");
        var current = new LauncherSettings();
        var selected = Path.Combine(root, "games");
        Assert.ThrowsAny<IOException>(() => ContentDirectorySetup.Complete(current, Path.Combine(blockedParent, "settings.json"), selected));
        Assert.False(current.ContentDirectoryConfigured);
        Assert.Equal("keep", File.ReadAllText(blockedParent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(selected));
    }

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "mojin-directory-test-");
        if (!fullRoot.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test directory.");
        if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
    }
}
