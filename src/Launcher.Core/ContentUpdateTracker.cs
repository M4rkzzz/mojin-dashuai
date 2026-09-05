namespace Boshan.Launcher;

// Only a CatalogClient-verified directory may enter this tracker. Failed refreshes leave
// the last trusted directory intact; local installation changes are evaluated separately.
public sealed class ContentUpdateTracker
{
    private Catalog? directory;
    public void Accept(Catalog verified)
    {
        if(directory is not null&&verified.Sequence<directory.Sequence)throw new InvalidDataException("拒绝过期的客户端更新目录。");
        directory=verified;
    }
    public async Task Refresh(Func<CancellationToken,Task<Catalog>> fetch,CancellationToken token=default)
    {
        var verified=await fetch(token);
        Accept(verified);
    }
    public Task<ReleaseRef?> Available(PackManifest installed,RollbackPin? pin=null)
    {
        var snapshot=directory;
        return snapshot is null?Task.FromResult<ReleaseRef?>(null):LaunchUpdates.Check(installed,pin,_=>Task.FromResult(snapshot));
    }
}
