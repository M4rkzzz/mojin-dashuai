using Boshan.Launcher;

internal static class ReleaseAcceptance
{
    internal static void Require(PackManifest manifest,string hash,AcceptanceReport report,bool beta)
    {
        if(!report.Passed||report.ManifestSha256!=hash||!report.JoinedServer||!report.AllSourcesAutomated||report.JavaMajor!=manifest.Runtime.Major)
            throw new InvalidDataException("Release blocked: acceptance evidence does not cover this exact manifest.");
        if(!report.CleanWindows&&(!beta||report.Channel!="beta"))
            throw new InvalidDataException("Stable release requires clean Windows acceptance; explicitly approved beta publication may defer only this check.");
        if(manifest.Instance=="mb"&&(!report.Map||!report.Quests||!report.Machines||report.Loader!="cleanroom"))
            throw new InvalidDataException("Release blocked: MeatballCraft acceptance incomplete.");
    }
}
public sealed record AcceptanceReport(bool Passed,string ManifestSha256,bool CleanWindows,bool JoinedServer,bool AllSourcesAutomated,int JavaMajor,string Loader,bool Map,bool Quests,bool Machines,string Channel="stable");
