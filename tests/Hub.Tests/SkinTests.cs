using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Boshan.Shared;
using Xunit;

namespace Boshan.Tests;

public sealed class SkinTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void AcceptsStandardSkinDimensions(int height)
    {
        var png=SkinImage.Normalize(Png(height));
        Assert.Equal(64u,BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
        Assert.Equal((uint)height,BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }
    [Fact] public void RejectsOversizedDimensions()=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(Png(16384)));
    [Fact] public void RejectsInflationBeyondDeclaredPixels()=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(Png(64,rawSize:500000)));
    [Fact] public void RejectsInvalidRowFilter()=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(Png(64,filter:5)));
    [Fact] public void RejectsDamagedCrc()
    {
        var png=Png(64);png[29]^=255;
        Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(png));
    }
    [Fact] public void StripsMetadataBeforePublishing()
    {
        var png=SkinImage.Normalize(Png(64,metadata:"tEXt"));
        Assert.DoesNotContain("private-metadata",Encoding.ASCII.GetString(png));
    }
    [Fact] public void RejectsAnimatedSkin()=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(Png(64,metadata:"acTL")));
    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public void RejectsUnknownModel(string model)=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(new SkinTexture(Convert.ToBase64String(Png(64)),model)));
    [Fact] public void PreservesSlimModel()=>Assert.Equal("slim",SkinImage.Normalize(new SkinTexture(Convert.ToBase64String(Png(64)),"slim")).Model);
    [Fact] public void RejectsMalformedBase64()=>Assert.Throws<InvalidDataException>(()=>SkinImage.Normalize(new SkinTexture("not base64","classic")));

    private static byte[] Png(int height,int? rawSize=null,byte filter=0,string? metadata=null)
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        var header=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(header,64);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);header[8]=8;header[9]=6;
        WriteChunk(output,"IHDR",header);
        if(metadata is not null)WriteChunk(output,metadata,Encoding.ASCII.GetBytes("private-metadata"));
        var raw=new byte[rawSize??Math.Min(height,64)*257];raw[0]=filter;
        using var pixels=new MemoryStream();using(var compressor=new ZLibStream(pixels,CompressionLevel.Fastest,true))compressor.Write(raw);
        WriteChunk(output,"IDAT",pixels.ToArray());WriteChunk(output,"IEND",[]);return output.ToArray();
    }
    private static void WriteChunk(Stream stream,string name,byte[] data)
    {
        var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,data.Length);stream.Write(length);
        var content=Encoding.ASCII.GetBytes(name).Concat(data).ToArray();stream.Write(content);
        uint crc=0xffffffff;
        foreach(var value in content){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}
        BinaryPrimitives.WriteUInt32BigEndian(length,~crc);stream.Write(length);
    }
}
