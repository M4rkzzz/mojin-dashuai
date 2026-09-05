using System.Text.Json;
using Boshan.Shared;

namespace Boshan.Hub;

public sealed class SkinService(IConfiguration configuration)
{
    private string Root => configuration["SkinPath"] ?? Path.Combine("data","skins");

    public async Task<SkinTexture?> Read(Guid userId)
    {
        var path = Path.Combine(Root,userId.ToString("N")+".json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SkinTexture>(stream);
    }
    public async Task<SkinTexture> Save(Guid userId,SkinTexture texture)
    {
        try { texture = SkinImage.Normalize(texture); }
        catch (InvalidDataException error) { throw new HubError(error.Message); }
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root,userId.ToString("N")+".json");
        var temporary = Path.Combine(Root,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(texture));
            File.Move(temporary,path,true);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
        return texture;
    }
}
