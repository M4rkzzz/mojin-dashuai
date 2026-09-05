using System.Net;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class LaunchUpdateTests
{
    private static readonly ContentFile Archive = new("java.zip", 1, new string('a', 64), ["https://example.org/java.zip"], FilePolicy.Managed, "test");
    private static readonly PackManifest Installed = new("mb", "old", 10, "1.12.2", "cleanroom", "0.5.17-alpha", "cleanroom",
        new("java25", 25, "25", "windows-x64", Archive, "jdk/bin/java.exe", 100), 8736, "mb-1", [], ["test"]);
    private static readonly ReleaseRef Latest = new("new", 11, "https://example.org/11.json", new string('b', 64), "mb-1");
    private static Catalog Directory(ReleaseRef release, params ReleaseRef[] rollbacks) => new(20, "0.1.0", DateTimeOffset.UtcNow.AddDays(1),
        [new("mb", "肉丸工艺", [], release, rollbacks)]);

    [Fact]
    public async Task UpdateUsesNewerReleaseAndNeverSilentlyDowngrades()
    {
        Assert.Equal(Latest, await LaunchUpdates.Check(Installed, null, _ => Task.FromResult(Directory(Latest))));
        Assert.Null(await LaunchUpdates.Check(Installed, null, _ => Task.FromResult(Directory(Latest with {Sequence = 9}))));
        Assert.Null(await LaunchUpdates.Check(Installed, null, _ => Task.FromResult(Directory(Latest with {Sequence = 10, Version = "old"}))));
        await Assert.ThrowsAsync<InvalidDataException>(() => LaunchUpdates.Check(Installed, null, _ => Task.FromResult(Directory(Latest with {Sequence = 10}))));
    }

    [Fact]
    public async Task AuthorizedRollbackRemainsSelectedUntilCurrentReleaseChanges()
    {
        var rollback = Latest with {Version = Installed.Version, Sequence = Installed.Sequence};
        var pin = new RollbackPin("old", 10, 11);
        Assert.Null(await LaunchUpdates.Check(Installed, pin, _ => Task.FromResult(Directory(Latest, rollback))));
        Assert.Equal(Latest, await LaunchUpdates.Check(Installed, pin, _ => Task.FromResult(Directory(Latest))));
        var newer = Latest with {Version = "newer", Sequence = 12};
        Assert.Equal(newer, await LaunchUpdates.Check(Installed, pin, _ => Task.FromResult(Directory(newer, rollback))));
    }

    [Fact]
    public async Task TemporaryOutageAllowsInstalledContentButDoesNotHideInvalidContent()
    {
        foreach(var status in new HttpStatusCode?[] {null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests})
            Assert.Null(await LaunchUpdates.Check(Installed, null, _ => Task.FromException<Catalog>(new HttpRequestException("unavailable", null, status))));
        using var timeout = new CancellationTokenSource();timeout.Cancel();
        Assert.Null(await LaunchUpdates.Check(Installed, null, _ => Task.FromCanceled<Catalog>(timeout.Token), timeout.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => LaunchUpdates.Check(Installed, null, _ => Task.FromException<Catalog>(new InvalidDataException("invalid signature"))));
        await Assert.ThrowsAsync<IOException>(() => LaunchUpdates.Check(Installed, null, _ => Task.FromException<Catalog>(new IOException("checkpoint write failed"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => LaunchUpdates.Check(Installed, null, _ => Task.FromException<Catalog>(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden))));
    }
}
