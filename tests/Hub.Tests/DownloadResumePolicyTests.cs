using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public class DownloadResumePolicyTests
{
    private static readonly ReleaseRef Old=new("old",2,"https://fixture.invalid/2",new string('a',64),"forge-r4");
    private static readonly ReleaseRef Current=new("current",3,"https://fixture.invalid/3",new string('b',64),"forge-r4");
    private static ServerCatalog Server(params ReleaseRef[] rollbacks)=>new("dc2","亡者世界",[],Current,rollbacks);

    [Fact] public void OrdinaryResumeUsesCurrentEvenWhenOldReleaseIsStillAllowedForRollback()
        =>Assert.Same(Current,DownloadResumePolicy.SelectRelease(Server(Old),Old,false));

    [Fact] public void RetiredOrdinaryDownloadResumesCurrentWithoutRequiringOldManifest()
        =>Assert.Same(Current,DownloadResumePolicy.SelectRelease(Server(),Old,false));

    [Fact] public void ExplicitRollbackKeepsAuthorizedTarget()
        =>Assert.Same(Old,DownloadResumePolicy.SelectRelease(Server(Old),Old,true));

    [Fact] public void RetiredRollbackIsRejectedInsteadOfSilentlyChangingItsTarget()
        =>Assert.Throws<InvalidDataException>(()=>DownloadResumePolicy.SelectRelease(Server(),Old,true));

    [Fact] public void RollbackCannotCrossCompatibilityBoundary()
        =>Assert.Throws<InvalidDataException>(()=>DownloadResumePolicy.SelectRelease(Server(Old with {Compatibility="other"}),Old,true));
}
