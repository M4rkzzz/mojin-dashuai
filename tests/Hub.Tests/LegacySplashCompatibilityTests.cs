using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class LegacySplashCompatibilityTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-splash-compatibility-"+Guid.NewGuid().ToString("N"));
    private string Config=>Path.Combine(root,"config","splash.properties");
    private string Backup=>Path.Combine(root,".hub","compatibility","angelica-2.1.38","loading-screen","splash.properties.original");
    private string Modules=>Path.Combine(root,"config","angelica-modules.cfg");
    private static PackManifest Manifest(string instance="vw",string mod="mods/angelica-2.1.38.jar")
    {
        var file=new ContentFile(mod,1,new string('a',64),["https://fixture.invalid/mod.jar"],FilePolicy.Managed,"fixture");
        return new(instance,"fixture",1,"1.7.10","forge","10.13.4.1614","fixture",new("java8",8,"8","windows-x64",file,"bin/java.exe",1),4096,"fixture",[file],["fixture"]);
    }
    private void Put(byte[] bytes){Directory.CreateDirectory(Path.GetDirectoryName(Config)!);File.WriteAllBytes(Config,bytes);}

    [Fact]
    public void NativeForgeR2RepairsTheDisabledSplashRetainedFromR1()
    {
        var original=Encoding.UTF8.GetBytes("# r1 retained config\nenabled=false\nrotate=false\n");Put(original);
        var manifest=Manifest() with {Sequence=2,Files=[]};
        LegacySplashCompatibility.Prepare(root,manifest);
        Assert.Equal("# r1 retained config\nenabled=true\nrotate=false\n",File.ReadAllText(Config));
        Assert.Equal(original,File.ReadAllBytes(Backup));Assert.False(File.Exists(Modules));
        LegacySplashCompatibility.Prepare(root,manifest);Assert.Equal(original,File.ReadAllBytes(Backup));
    }

    [Theory]
    [InlineData("vw","1.7.10","forge","mods/angelica-2.1.39.jar")]
    [InlineData("m3e","1.7.10","forge","mods/example.jar")]
    [InlineData("vw","1.12.2","cleanroom","mods/example.jar")]
    public void NativeForgeMigrationDoesNotChangeOtherRenderers(string instance,string minecraft,string loader,string mod)
    {
        Put(Encoding.UTF8.GetBytes("enabled=false\n"));
        var manifest=Manifest(instance,mod) with {Sequence=2,Minecraft=minecraft,Loader=loader};
        LegacySplashCompatibility.Prepare(root,manifest);
        Assert.Equal("enabled=false\n",File.ReadAllText(Config));Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void RestoresSplashPreservesOriginalBytesAndDoesNotRewriteOnRepeatedLaunch()
    {
        var original=Encoding.Latin1.GetBytes("#Splash screen properties\r\n!Keep comments: café\r\nlogoTexture=fml\\:textures/gui/forge.gif\r\n  enabled : false  \r\nfont=0x0\r\n");
        Put(original);LegacySplashCompatibility.Prepare(root,Manifest());
        var changed=Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(original).Replace("enabled : false","enabled : true"));
        Assert.Equal(changed,File.ReadAllBytes(Config));Assert.Equal(original,File.ReadAllBytes(Backup));
        Assert.Contains("B:enableFontRenderer=false",File.ReadAllText(Modules));
        var stamp=new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(Config,stamp);
        LegacySplashCompatibility.Prepare(root,Manifest());
        Assert.Equal(changed,File.ReadAllBytes(Config));Assert.Equal(stamp,File.GetLastWriteTimeUtc(Config));
        Assert.Equal(original,File.ReadAllBytes(Backup));Assert.Empty(Directory.GetFiles(root,"*.tmp",SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("m3e","mods/angelica-2.1.38.jar")]
    [InlineData("dc2","mods/angelica-2.1.38.jar")]
    [InlineData("vw","mods/angelica-2.1.39.jar")]
    [InlineData("vw","optional/angelica-2.1.38.jar")]
    [InlineData("vw","mods/angelica-2.1.38.jar.disabled")]
    public void UnrelatedInstancesAndVersionsAreNotChanged(string instance,string mod)
    {
        LegacySplashCompatibility.Prepare(root,Manifest(instance,mod));Assert.False(Directory.Exists(root));
        Put(Encoding.UTF8.GetBytes("enabled=true\n"));LegacySplashCompatibility.Prepare(root,Manifest(instance,mod));
        Assert.Equal("enabled=true\n",File.ReadAllText(Config));Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void AlreadyEnabledSplashIsUntouchedButTheConflictingFontModuleIsDisabled()
    {
        var original=Encoding.UTF8.GetBytes("# Splash screen\nenabled=true\nrotate=false\n");Put(original);
        LegacySplashCompatibility.Prepare(root,Manifest());Assert.Equal(original,File.ReadAllBytes(Config));Assert.False(File.Exists(Backup));
        Assert.Contains("B:enableFontRenderer=false",File.ReadAllText(Modules));
    }

    [Fact]
    public void MissingConfigGetsTheCompatibilityDefaultAndNoInventedOriginalBackup()
    {
        LegacySplashCompatibility.Prepare(root,Manifest());Assert.Equal("enabled=true"+Environment.NewLine,File.ReadAllText(Config));
        Assert.False(File.Exists(Backup));LegacySplashCompatibility.Prepare(root,Manifest());Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void MissingSwitchIsAppendedAndContinuationValuesArePreserved()
    {
        var original="# keep\nother=value\\\nenabled=true\nfont=0x0";Put(Encoding.UTF8.GetBytes(original));
        LegacySplashCompatibility.Prepare(root,Manifest());
        Assert.Equal(original+"\nenabled=true\n",File.ReadAllText(Config));Assert.Equal(original,File.ReadAllText(Backup));
    }

    [Fact]
    public void FirstBackupSurvivesRepeatedRepairsAndContinuedSwitchesBecomeTrue()
    {
        var original="# initial\nenabled=\\\n false\nfont=0x0\n";Put(Encoding.UTF8.GetBytes(original));
        LegacySplashCompatibility.Prepare(root,Manifest());Assert.Equal("# initial\nenabled=true\nfont=0x0\n",File.ReadAllText(Config));
        File.WriteAllText(Config,"# later\nenabled=false\nfont=0xFF\n");LegacySplashCompatibility.Prepare(root,Manifest());
        Assert.Equal("# later\nenabled=true\nfont=0xFF\n",File.ReadAllText(Config));Assert.Equal(original,File.ReadAllText(Backup));
    }

    [Fact]
    public void PreservesOtherAngelicaOptionsAndOriginalBackupFromBeta14()
    {
        Put(Encoding.UTF8.GetBytes("enabled=false\n"));
        const string original="# options\r\ngeneral {\r\n    B:enableFontRenderer=true\r\n    B:enableHudCaching=true\r\n}\r\n";
        File.WriteAllText(Modules,original);
        var oldBackup=Path.Combine(root,".hub","compatibility","angelica-2.1.38","splash.properties.original");
        Directory.CreateDirectory(Path.GetDirectoryName(oldBackup)!);File.WriteAllText(oldBackup,"beta14-original");
        LegacySplashCompatibility.Prepare(root,Manifest());
        Assert.Equal(original.Replace("enableFontRenderer=true","enableFontRenderer=false"),File.ReadAllText(Modules));
        var backup=Path.Combine(Path.GetDirectoryName(Backup)!,"angelica-modules.cfg.original");
        Assert.Equal(original,File.ReadAllText(backup));Assert.Equal("beta14-original",File.ReadAllText(oldBackup));
        var stamp=new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(Modules,stamp);
        LegacySplashCompatibility.Prepare(root,Manifest());Assert.Equal(stamp,File.GetLastWriteTimeUtc(Modules));
    }

    [Fact]
    public void AddsMissingModuleOptionInsideTheExistingGeneralCategory()
    {
        Put(Encoding.UTF8.GetBytes("enabled=false\n"));
        File.WriteAllText(Modules,"# config\ngeneral {\n    B:enableHudCaching=true\n}\n");
        LegacySplashCompatibility.Prepare(root,Manifest());
        Assert.Equal("# config\ngeneral {\n    B:enableFontRenderer=false\n    B:enableHudCaching=true\n}\n",File.ReadAllText(Modules));
    }

    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
