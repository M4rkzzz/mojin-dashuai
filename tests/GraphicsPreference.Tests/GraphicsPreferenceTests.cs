using Boshan.Desktop;
using Microsoft.Win32;
using Xunit;

namespace Boshan.Tests;

public sealed class GraphicsPreferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mojin-gpu-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryRegistry registry = new();
    private string StateRoot => Path.Combine(root, "state");
    private string Java(string runtime = "Java 中文路径")
    {
        var path = Path.Combine(root, runtime, "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }
    private GraphicsPreferenceResult Apply(string java, bool enabled = true) => GraphicsPreference.Apply(java, enabled, StateRoot, registry);

    [Fact]
    public void NewPreferenceIsTrackedThenDeletedWhenDisabled()
    {
        var java = Java();
        Assert.Equal("applied", Apply(java).Status);
        Assert.Equal("GpuPreference=2;", registry.Values[java].Text);
        Assert.Equal("already-applied", Apply(java).Status);
        Assert.Equal(1, registry.Writes);
        Assert.Equal("restored", Apply(java, false).Status);
        Assert.False(registry.Values.ContainsKey(java));
        Assert.Equal("disabled", Apply(java, false).Status);
    }

    [Fact]
    public void OtherFieldsAndOriginalRegistryTypeArePreservedAndExactlyRestored()
    {
        var java = Java();
        var original = new GraphicsRegistryValue("AutoHDREnable=1;GpuPreference=1;SwapEffectUpgradeEnable=0;", RegistryValueKind.ExpandString);
        registry.Values[java] = original;
        Assert.True(Apply(java).Success);
        Assert.Equal("AutoHDREnable=1;GpuPreference=2;SwapEffectUpgradeEnable=0;", registry.Values[java].Text);
        Assert.Equal(RegistryValueKind.ExpandString, registry.Values[java].Kind);
        Apply(java, false);
        Assert.Equal(original, registry.Values[java]);
    }

    [Fact]
    public void ExistingHighPerformancePreferenceIsNeverClaimedOrRemoved()
    {
        var java = Java();
        var original = new GraphicsRegistryValue("GpuPreference=2;AutoHDREnable=1;");
        registry.Values[java] = original;
        Assert.Equal("already-preferred", Apply(java).Status);
        Assert.Equal("disabled", Apply(java, false).Status);
        Assert.Equal(original, registry.Values[java]);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void WindowsChangesRemainUntouchedByLaterLaunchesAndDisabling()
    {
        var java = Java();
        Apply(java);
        var manual = new GraphicsRegistryValue("GpuPreference=1;AutoHDREnable=1;");
        registry.Values[java] = manual;
        Assert.Equal("external-change", Apply(java).Status);
        Assert.Equal("external-change", Apply(java).Status);
        Assert.Equal("external-change", Apply(java, false).Status);
        Assert.Equal(manual, registry.Values[java]);
        Assert.Equal(1, registry.Writes);
        // An explicit off/on transition gives the launcher a fresh request and backup.
        Assert.Equal("applied", Apply(java).Status);
        Apply(java, false);
        Assert.Equal(manual, registry.Values[java]);
    }

    [Fact]
    public void RemovingPreferenceInWindowsDoesNotCauseItToReappear()
    {
        var java = Java();
        Apply(java);
        registry.Values.Remove(java);
        Assert.Equal("external-change", Apply(java).Status);
        Apply(java, false);
        Assert.False(registry.Values.ContainsKey(java));
    }

    [Fact]
    public void RestoreAllHandlesEachActualRuntimeIndependentlyIncludingRemovedJava()
    {
        var java8 = Java("runtime 8");
        var java17 = Java("runtime 17");
        var java25 = Java("runtime 25");
        var before = new GraphicsRegistryValue("GpuPreference=1;");
        registry.Values[java17] = before;
        Apply(java8); Apply(java17); Apply(java25);
        registry.Values[java25] = new("GpuPreference=1;AutoHDREnable=0;");
        File.Delete(java8);
        var results = GraphicsPreference.RestoreAll(StateRoot, registry);
        Assert.Equal(2, results.Count(value => value.Status == "restored"));
        Assert.Single(results, value => value.Status == "external-change");
        Assert.False(registry.Values.ContainsKey(java8));
        Assert.Equal(before, registry.Values[java17]);
        Assert.Equal("GpuPreference=1;AutoHDREnable=0;", registry.Values[java25].Text);
    }

    [Fact]
    public void FailedRegistryWriteDoesNotLoseTheBackupOrPreventFutureSafeDisable()
    {
        var java = Java();
        var before = new GraphicsRegistryValue("GpuPreference=1;");
        registry.Values[java] = before;
        registry.FailWrites = true;
        var result = Apply(java);
        Assert.False(result.Success);
        Assert.Equal("failed", result.Status);
        Assert.DoesNotContain(root, result.Message);
        Assert.Equal(before, registry.Values[java]);
        registry.FailWrites = false;
        Assert.Equal("external-change", Apply(java, false).Status);
        Assert.Equal(before, registry.Values[java]);
    }

    [Fact]
    public void JournalWriteFailureOrCorruptionCannotLeaveAnUntrackedRegistryChange()
    {
        var java = Java();
        var blockedRoot = Path.Combine(root, "file-not-directory");
        File.WriteAllText(blockedRoot, "fixture");
        Assert.False(GraphicsPreference.Apply(java, true, blockedRoot, registry).Success);
        Assert.Equal(0, registry.Writes);
        Directory.CreateDirectory(StateRoot);
        File.WriteAllText(Path.Combine(StateRoot, "graphics-preferences.json"), "{invalid");
        Assert.False(Apply(java).Success);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void ExternalChangeDuringJournalSaveIsPreserved()
    {
        var java = Java();
        var external = new GraphicsRegistryValue("GpuPreference=1;AutoHDREnable=1;");
        registry.BeforeRead = (_, count) => { if (count == 2) registry.Values[java] = external; };
        Assert.Equal("external-change", Apply(java).Status);
        Assert.Equal(external, registry.Values[java]);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void NonJavaExecutableAndRelativeJavaPathNeverWriteRegistry()
    {
        var java = Java();
        var launcher = Path.Combine(Path.GetDirectoryName(java)!, "MojinDashuai.Launcher.exe");
        File.WriteAllBytes(launcher, []);
        Assert.False(Apply(launcher).Success);
        Assert.False(Apply("java.exe").Success);
        Assert.Equal(0, registry.Writes);
    }

    [Theory]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("AutoHDREnable=1", "AutoHDREnable=1;GpuPreference=2;")]
    [InlineData("AutoHDREnable=1;", "AutoHDREnable=1;GpuPreference=2;")]
    [InlineData("SomeGpuPreference=1;gpupreference=1;GpuPreference=0;", "SomeGpuPreference=1;gpupreference=2;GpuPreference=2;")]
    public void OnlyGpuFieldsAreChanged(string original, string expected)
    {
        Assert.Equal(expected, GraphicsPreference.PreferHighPerformance(new(original)).Text);
    }

    private sealed class MemoryRegistry : IGraphicsPreferenceRegistry
    {
        public Dictionary<string, GraphicsRegistryValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public bool FailWrites;
        private int reads;
        public Action<string, int>? BeforeRead;
        public GraphicsRegistryValue? Read(string javaPath)
        {
            BeforeRead?.Invoke(javaPath, ++reads);
            return Values.GetValueOrDefault(javaPath);
        }
        public void Write(string javaPath, GraphicsRegistryValue value)
        {
            if (FailWrites) throw new UnauthorizedAccessException("fixture denied");
            Writes++;
            Values[javaPath] = value;
        }
        public void Delete(string javaPath) => Values.Remove(javaPath);
    }

    public void Dispose()
    {
        // This class creates a unique temp directory and never accepts user deletion targets.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
