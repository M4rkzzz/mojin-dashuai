using System.Text;
using Boshan.Hub;
using Xunit;

namespace Boshan.Tests;

public sealed class PublicMetadataTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mojin-public-"+Guid.NewGuid().ToString("N"));
    private const string Envelope="{\"keyId\":\"test\",\"payload\":\"e30=\",\"signature\":\"test-signature\"}";
    public PublicMetadataTests()=>Directory.CreateDirectory(root);
    private string Write(string relative,string value)
    {
        var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value);return path;
    }
    [Fact]
    public void PublicMetadataServesOnlyTheNamedCatalogAndManifest()
    {
        Write("catalog.signed.json",Envelope);Write("manifests/mb/7.signed.json",Envelope);
        Assert.Equal(Envelope,Encoding.UTF8.GetString(PublicMetadata.Catalog(root)!));
        Assert.Equal(Envelope,Encoding.UTF8.GetString(PublicMetadata.Manifest(root,"mb",7)!));
        Assert.Null(PublicMetadata.Manifest(root,"mb",8));
        Assert.Null(PublicMetadata.Manifest(root,"m3e",7));
    }
    [Theory]
    [InlineData("../private")][InlineData("MB")][InlineData("other")][InlineData("C:/private")]
    public void ManifestIdentifiersCannotChooseArbitraryFiles(string instance)=>Assert.Null(PublicMetadata.Manifest(root,instance,1));
    [Theory][InlineData(0)][InlineData(-1)]
    public void SequenceMustBePositive(long sequence)=>Assert.Null(PublicMetadata.Manifest(root,"mb",sequence));
    [Theory]
    [InlineData("{\"password\":\"private\"}")][InlineData("{\"keyId\":\"x\",\"payload\":\"\",\"signature\":\"x\"}")][InlineData("[]")]
    public void NonEnvelopeJsonIsNotServed(string json)
    {
        Write("catalog.signed.json",json);
        Assert.Throws<InvalidDataException>(()=>PublicMetadata.Catalog(root));
    }
    [Fact]
    public void SymlinksCannotExposeFilesOutsideThePublicDirectory()
    {
        var source=Write("outside-private.json",Envelope);
        var directory=Path.Combine(root,"public");Directory.CreateDirectory(directory);
        var link=Path.Combine(directory,"catalog.signed.json");
        File.CreateSymbolicLink(link,source);
        try{Assert.Throws<InvalidDataException>(()=>PublicMetadata.Catalog(directory));}
        finally{File.Delete(link);}
    }
    public void Dispose()=>Directory.Delete(root,true);
}
