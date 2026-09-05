using System.Text.Json;

namespace Boshan.Hub;

public static class PublicMetadata
{
    public const long MaxBytes=16*1024*1024;
    public static byte[]? Catalog(string root)=>Read(root,["catalog.signed.json"]);
    public static byte[]? Manifest(string root,string instance,long sequence)
    {
        if(instance is not ("m3e" or "dc2" or "mb")||sequence<=0)return null;
        return Read(root,["manifests",instance,sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)+".signed.json"]);
    }
    private static byte[]? Read(string root,string[] components)
    {
        var path=Path.GetFullPath(root);
        if(!Directory.Exists(path))return null;
        RejectLink(path);
        for(var i=0;i<components.Length;i++)
        {
            path=Path.Combine(path,components[i]);
            if(i<components.Length-1&&!Directory.Exists(path)||i==components.Length-1&&!File.Exists(path))return null;
            RejectLink(path);
        }
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<=0||stream.Length>MaxBytes)throw new InvalidDataException("Invalid public metadata size.");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=8});
        var envelope=json.RootElement;
        if(envelope.ValueKind!=JsonValueKind.Object||!Text(envelope,"keyId")||!Text(envelope,"payload")||!Text(envelope,"signature"))
            throw new InvalidDataException("Only signed public metadata envelopes may be served.");
        return bytes;
    }
    private static bool Text(JsonElement json,string key)=>json.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(value.GetString());
    private static void RejectLink(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Public metadata cannot be a symbolic link or junction.");
    }
}
