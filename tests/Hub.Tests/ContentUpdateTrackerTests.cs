using System.Net;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class ContentUpdateTrackerTests
{
    private static readonly ContentFile Archive=new("java.zip",1,new string('a',64),["https://example.invalid/java.zip"],FilePolicy.Managed,"fixture");
    private static PackManifest Installed(string id,long sequence=1)=>new(id,id+"-"+sequence,sequence,"1.20.1","forge","47","fixture",new("java17",17,"17","windows-x64",Archive,"bin/java.exe",1),8192,id+"-compatible",[],["fixture"]);
    private static ReleaseRef Release(string id,long sequence)=>new(id+"-"+sequence,sequence,"https://example.invalid/"+id+".json",new string('b',64),id+"-compatible");
    private static Catalog Directory(long sequence=1)=>new(sequence,"0.1.2.12",DateTimeOffset.UtcNow.AddDays(1),[
        new("m3e","one",[],Release("m3e",1),[]),new("dc2","two",[],Release("dc2",2),[]),new("mb","three",[],Release("mb",3),[])]);

    [Fact]
    public async Task OneCatalogFetchSelectsUpdatesIndependentlyForEachInstalledInstance()
    {
        var tracker=new ContentUpdateTracker();var fetches=0;
        await tracker.Refresh(_=>{fetches++;return Task.FromResult(Directory());});
        Assert.Null(await tracker.Available(Installed("m3e")));
        Assert.Equal(Release("dc2",2),await tracker.Available(Installed("dc2")));
        Assert.Equal(Release("mb",3),await tracker.Available(Installed("mb")));
        Assert.Equal(1,fetches);
    }
    [Fact]
    public async Task NoTrustedCatalogOrAnOlderReleaseNeverCreatesAnUpdate()
    {
        var tracker=new ContentUpdateTracker();Assert.Null(await tracker.Available(Installed("dc2")));
        tracker.Accept(Directory());Assert.Null(await tracker.Available(Installed("dc2",3)));
        Assert.Null(await tracker.Available(Installed("not-in-catalog")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledRefreshDoesNotClearKnownUpdates(bool cancelled)
    {
        var tracker=new ContentUpdateTracker();tracker.Accept(Directory());
        var failure=cancelled?(Exception)new OperationCanceledException():new HttpRequestException("offline",null,HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAnyAsync<Exception>(()=>tracker.Refresh(_=>Task.FromException<Catalog>(failure)));
        Assert.Equal(Release("dc2",2),await tracker.Available(Installed("dc2")));
        Assert.Equal(Release("mb",3),await tracker.Available(Installed("mb")));
    }
    [Fact]
    public async Task CompletingOneInstallationClearsOnlyItsOwnPendingUpdate()
    {
        var tracker=new ContentUpdateTracker();tracker.Accept(Directory());
        Assert.Null(await tracker.Available(Installed("dc2",2)));
        Assert.Equal(Release("mb",3),await tracker.Available(Installed("mb")));
        Assert.Null(await tracker.Available(Installed("m3e")));
    }
    [Fact]
    public async Task AnAuthorizedRollbackStaysSelectedUntilTheCurrentReleaseChanges()
    {
        var tracker=new ContentUpdateTracker();var original=Directory();
        tracker.Accept(original with {Servers=original.Servers.Select(server=>server.Id=="dc2"?server with {Rollbacks=[Release("dc2",1)]}:server).ToArray()});
        var pin=new RollbackPin("dc2-1",1,2);
        Assert.Null(await tracker.Available(Installed("dc2"),pin));
        Assert.Equal(Release("mb",3),await tracker.Available(Installed("mb"),pin));
        var newer=Directory(2);tracker.Accept(newer with {Servers=newer.Servers.Select(server=>server.Id=="dc2"?server with {Release=Release("dc2",3),Rollbacks=[Release("dc2",1)]}:server).ToArray()});
        Assert.Equal(Release("dc2",3),await tracker.Available(Installed("dc2"),pin));
    }
    [Fact]
    public async Task AnUnauthorizedRollbackOrInvalidRefreshCannotHideARequiredUpdate()
    {
        var tracker=new ContentUpdateTracker();tracker.Accept(Directory(2));
        Assert.Equal(Release("dc2",2),await tracker.Available(Installed("dc2"),new("dc2-1",1,2)));
        Assert.Throws<InvalidDataException>(()=>tracker.Accept(Directory(1)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>tracker.Refresh(_=>Task.FromException<Catalog>(new InvalidDataException("signature failed"))));
        Assert.Equal(Release("dc2",2),await tracker.Available(Installed("dc2")));
    }
}
