using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace Boshan.Desktop;

// Stage library writes until the complete Microsoft/Xbox/Minecraft login succeeds.
public sealed class MicrosoftAccountStorage(Vault vault,bool fresh,CancellationToken token) : IJsonStorage
{
    public const string Key="cml-microsoft";
    private byte[]? pending;
    public JsonNode? ReadAsJsonNode()
    {
        token.ThrowIfCancellationRequested();
        var bytes=pending??(fresh?null:vault.ReadBytes(Key));
        return bytes is null?null:JsonNode.Parse(bytes);
    }
    public void Write(JsonNode node,JsonSerializerOptions? serializerOptions)
    {
        token.ThrowIfCancellationRequested();
        pending=JsonSerializer.SerializeToUtf8Bytes(node,serializerOptions);
    }
    public void Commit()
    {
        token.ThrowIfCancellationRequested();
        if(pending is not null)vault.WriteBytes(Key,pending);
    }
}
