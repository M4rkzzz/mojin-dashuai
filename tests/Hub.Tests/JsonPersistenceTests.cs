using System.Text.Json;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;

public sealed class JsonPersistenceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-json-persistence-"+Guid.NewGuid().ToString("N"));
    private string Ledger=>Path.Combine(root,"installed.json");
    private static InstalledPack Pack(string version)
    {
        var files=Enumerable.Range(0,1714).Select(i=>new ContentFile($"config/四服/{i}.json",i,new string('a',64),["https://unified.example/objects/sha256/"+new string('a',64)],FilePolicy.Managed,"Fixture distribution notice")).ToArray();
        return new(new("vw",version,1,"1.7.10","forge","10.13.4.1614","fixture",new("java8",8,"8.0.504","windows-x64",files[0] with{Path="runtime.zip"},"bin/java.exe",1),4096,"fixture",files,["fixture"]),DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void LargeInstalledLedgerKeepsTheExistingUtf8JsonFormatWhenReplaced()
    {
        var first=Pack("old");var next=Pack("new");Json.Write(Ledger,first);
        Json.Write(Ledger,next);
        Assert.Equal("new",Json.Read<InstalledPack>(Ledger).Manifest.Version);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(next,Json.Options),File.ReadAllBytes(Ledger));
        Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }

    private sealed class BrokenDocument
    {
        public string Value=>throw new InvalidOperationException("simulated serialization failure");
    }

    [Fact]
    public void SerializationFailurePreservesThePreviousLedgerAndLeavesNoTemporaryFile()
    {
        Json.Write(Ledger,Pack("old"));var original=File.ReadAllBytes(Ledger);
        Assert.Throws<InvalidOperationException>(()=>Json.Write(Ledger,new BrokenDocument()));
        Assert.Equal(original,File.ReadAllBytes(Ledger));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }

    [Fact]
    public void FailedReplacementPreservesTheLockedLedgerAndCleansTheFlushedTemporaryFile()
    {
        if(!OperatingSystem.IsWindows())return;
        Json.Write(Ledger,Pack("old"));var original=File.ReadAllBytes(Ledger);
        using(var locked=new FileStream(Ledger,FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.True(Record.Exception(()=>Json.Write(Ledger,Pack("new"))) is IOException or UnauthorizedAccessException);
        Assert.Equal(original,File.ReadAllBytes(Ledger));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }

    public void Dispose()
    {
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
}
