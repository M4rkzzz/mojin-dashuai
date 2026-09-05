using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class ReleaseAcceptanceTests
{
    private static readonly PackManifest Pack=new("mb","test",1,"1.12.2","cleanroom","0.5.17-alpha","test",new("java25",25,"25","windows-x64",null!,"java.exe",1),8736,"cleanroom-java25",[],["evidence.json"]);
    private static readonly AcceptanceReport Report=new(true,"exact-hash",false,true,true,25,"cleanroom",true,true,true,"beta");
    [Fact] public void BetaMayDeferCleanWindowsButStableStillRequiresIt()
    {
        ReleaseAcceptance.Require(Pack,"exact-hash",Report,true);
        Assert.Throws<InvalidDataException>(()=>ReleaseAcceptance.Require(Pack,"exact-hash",Report,false));
        ReleaseAcceptance.Require(Pack,"exact-hash",Report with {CleanWindows=true},false);
    }
    [Fact] public void BetaCannotSkipContentIdentityRuntimeOrGameAcceptance()
    {
        foreach(var invalid in new[]{Report with {Passed=false},Report with {ManifestSha256="other"},Report with {JoinedServer=false},Report with {AllSourcesAutomated=false},Report with {JavaMajor=8},Report with {Loader="forge"},Report with {Map=false},Report with {Quests=false},Report with {Machines=false},Report with {Channel="stable"}})
            Assert.Throws<InvalidDataException>(()=>ReleaseAcceptance.Require(Pack,"exact-hash",invalid,true));
    }
}
