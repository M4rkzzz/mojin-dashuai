using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Boshan.Shared;

public sealed record SkinTexture(string PngBase64, string Model);

public static class SkinImage
{
    public const int MaxBytes = 128 * 1024;
    private static readonly byte[] Signature = [137,80,78,71,13,10,26,10];

    public static SkinTexture Normalize(SkinTexture texture)
    {
        if (texture.Model is not ("classic" or "slim")) throw new InvalidDataException("请选择标准或纤细模型。");
        if (string.IsNullOrWhiteSpace(texture.PngBase64)) throw new InvalidDataException("皮肤文件无效。");
        if (texture.PngBase64.Length > (MaxBytes + 2) / 3 * 4) throw new InvalidDataException("皮肤文件过大。");
        byte[] input;
        try { input = Convert.FromBase64String(texture.PngBase64); }
        catch (FormatException) { throw new InvalidDataException("皮肤文件无效。"); }
        return new(Convert.ToBase64String(Normalize(input)), texture.Model);
    }

    public static byte[] Normalize(byte[] input)
    {
        void Invalid() => throw new InvalidDataException("请选择 64×64 或 64×32 的 PNG 皮肤。");
        if (input.Length < 45 || input.Length > MaxBytes || !input.AsSpan(0,8).SequenceEqual(Signature)) Invalid();
        byte[]? header = null;
        using var compressed = new MemoryStream();
        var offset = 8;
        var ended = false;
        while (offset + 12 <= input.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset,4));
            if (length > input.Length - offset - 12) Invalid();
            var size = (int)length;
            var type = Encoding.ASCII.GetString(input,offset+4,4);
            if (Crc(input.AsSpan(offset+4,size+4)) != BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset+8+size,4))) Invalid();
            var data = input.AsSpan(offset+8,size);
            if (header is null && type != "IHDR") Invalid();
            if (type == "IHDR")
            {
                if (header is not null || size != 13) Invalid();
                header = data.ToArray();
                var width = BinaryPrimitives.ReadUInt32BigEndian(header);
                var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (width != 64 || height is not (32 or 64) || header[8] != 8 || header[9] is not (2 or 6) || header[10] != 0 || header[11] != 0 || header[12] != 0) Invalid();
            }
            else if (type == "IDAT") compressed.Write(data);
            else if (type == "IEND")
            {
                if (size != 0 || offset + 12 != input.Length) Invalid();
                ended = true; break;
            }
            else if (type is "acTL" or "fcTL" or "fdAT" || (input[offset+4] & 32) == 0 && type != "PLTE") Invalid();
            offset += size + 12;
        }
        if (!ended || header is null || compressed.Length == 0) Invalid();
        var row = 64 * (header![9] == 6 ? 4 : 3) + 1;
        var raw = new byte[row * (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4))];
        compressed.Position = 0;
        try
        {
            using var decoder = new ZLibStream(compressed,CompressionMode.Decompress,true);
            decoder.ReadExactly(raw);
            if (decoder.ReadByte() != -1) Invalid();
        }
        catch (IOException) { Invalid(); }
        for (var y = 0; y < raw.Length; y += row) if (raw[y] > 4) Invalid();
        using var pixels = new MemoryStream();
        using (var encoder = new ZLibStream(pixels,CompressionLevel.Fastest,true)) encoder.Write(raw);
        using var output = new MemoryStream();
        output.Write(Signature);
        Chunk(output,"IHDR",header);
        Chunk(output,"IDAT",pixels.ToArray());
        Chunk(output,"IEND",[]);
        return output.ToArray();
    }

    private static void Chunk(Stream stream,string name,byte[] data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size,data.Length);stream.Write(size);
        var bytes = Encoding.ASCII.GetBytes(name).Concat(data).ToArray();stream.Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(size,Crc(bytes));stream.Write(size);
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit=0;bit<8;bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
