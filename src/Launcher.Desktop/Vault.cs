using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Boshan.Launcher;

namespace Boshan.Desktop;

public sealed class Vault(string root)
{
    public byte[]? ReadBytes(string key)
    {
        var path=Path.Combine(root,key+".dpapi");
        if(!File.Exists(path))return null;
        return ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
    }
    public void WriteBytes(string key,byte[] value)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,key+".dpapi");
        var encrypted=ProtectedData.Protect(value,null,DataProtectionScope.CurrentUser);var temp=path+".tmp";
        File.WriteAllBytes(temp,encrypted);File.Move(temp,path,true);
    }
    public T? Read<T>(string key){var bytes=ReadBytes(key);return bytes is null?default:JsonSerializer.Deserialize<T>(bytes,Json.Options);}
    public void Write<T>(string key,T value)=>WriteBytes(key,JsonSerializer.SerializeToUtf8Bytes(value,Json.Options));
    public void Delete(string key){var path=Path.Combine(root,key+".dpapi");if(File.Exists(path))File.Delete(path);}
}
