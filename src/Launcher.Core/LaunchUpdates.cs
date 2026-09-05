using System.Net;

namespace Boshan.Launcher;

public sealed record RollbackPin(string Version, long Sequence, long CurrentReleaseSequence);

public static class LaunchUpdates
{
    public static async Task<ReleaseRef?> Check(PackManifest installed, RollbackPin? pin,
        Func<CancellationToken, Task<Catalog>> fetch, CancellationToken timeout = default)
    {
        Catalog directory;
        try { directory = await fetch(timeout); }
        // A temporary outage may use the installed pack. Invalid signatures and local IO failures must still surface.
        catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)e.StatusCode >= 500) { return null; }
        catch (NetworkFailure e) when(e.Diagnostic.HttpStatus is null or 408 or 429 or >=500){return null;}
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return null; }
        var server = directory.Servers.SingleOrDefault(s => s.Id == installed.Instance);
        var release = server?.Release;
        if (release is null || release.Sequence < installed.Sequence) return null;
        if (release.Sequence == installed.Sequence)
        {
            if (release.Version != installed.Version || release.Compatibility != installed.Compatibility)
                throw new InvalidDataException("同一发布序号的内容版本发生变化，请联系管理员。");
            return null;
        }
        // Keep an explicitly selected rollback while the same current release still authorizes it.
        if (pin is not null && pin.Version == installed.Version && pin.Sequence == installed.Sequence
            && pin.CurrentReleaseSequence == release.Sequence && release.Compatibility == installed.Compatibility
            && server!.Rollbacks.Any(r => r.Version == installed.Version && r.Sequence == installed.Sequence && r.Compatibility == installed.Compatibility))
            return null;
        return release;
    }
}
