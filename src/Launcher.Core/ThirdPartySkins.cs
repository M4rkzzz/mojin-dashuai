using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Boshan.Shared;

namespace Boshan.Launcher;

public sealed record SkinLoadResult(SkinTexture? Texture,string Status,string? Message=null);

/// <summary>Public skin textures only; this transport never receives account credentials.</summary>
public sealed class ThirdPartySkins(HttpClient http,SkinTextureCache cache)
{
    public const string LittleSkinRoot="https://littleskin.cn/csl/";
    public async Task<SkinTexture?> LittleSkin(string gameName,bool refresh=false,CancellationToken token=default)
        => (await LoadLittleSkin(gameName,refresh,token)).Texture;
    public async Task<SkinLoadResult> LoadLittleSkin(string gameName,bool refresh=false,CancellationToken token=default)
    {
        if(!Regex.IsMatch(gameName,"^[A-Za-z0-9_]{1,16}$"))return new(null,"missing","游戏名无法用于查询 LittleSkin 角色。");
        var result=await cache.Load("littleskin:"+gameName.ToLowerInvariant(),async cancellation=>
        {
            var metadata=await Read(new Uri(LittleSkinRoot+Uri.EscapeDataString(gameName)+".json"),32*1024,cancellation);
            if(metadata is null)return null;
            using var document=JsonDocument.Parse(metadata);
            var reference=TextureReference(document.RootElement);
            if(reference is null)return null;
            var bytes=await Read(reference.Value.Address,SkinImage.MaxTextureBytes,cancellation);
            return bytes is null?null:new SkinTexture(Convert.ToBase64String(SkinImage.NormalizeTexture(bytes)),reference.Value.Model);
        },refresh,token);
        return result.Status=="missing"?result with {Message=$"未找到 {gameName} 的 LittleSkin 皮肤，请检查同名角色是否已设置皮肤。"}:result;
    }

    public static (Uri Address,string Model)? TextureReference(JsonElement profile)
    {
        if(profile.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("皮肤资料格式无效。");
        // LittleSkin currently returns skins; CustomSkinAPI revision 2 also defines textures.
        foreach(var field in new[]{"textures","skins"})
        {
            if(!profile.TryGetProperty(field,out var textures)||textures.ValueKind==JsonValueKind.Null)continue;
            if(textures.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("皮肤资料格式无效。");
            foreach(var texture in textures.EnumerateObject())
                if(texture.Name is "default" or "slim" && texture.Value.ValueKind!=JsonValueKind.Null)
                {
                    var found=Reference(texture.Value,texture.Name=="slim"?"slim":"classic");
                    if(found is not null)return found;
                }
        }
        return profile.TryGetProperty("skin",out var legacy)?Reference(legacy,"classic"):null;
    }

    private static (Uri Address,string Model)? Reference(JsonElement value,string model)
    {
        if(value.ValueKind==JsonValueKind.Null)return null;
        if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("皮肤资源标识无效。");
        var hash=value.GetString();
        if(string.IsNullOrEmpty(hash))return null;
        if(!Regex.IsMatch(hash,"^[a-fA-F0-9]{64}$"))throw new InvalidDataException("皮肤资源标识无效。");
        return (new Uri(LittleSkinRoot+"textures/"+hash),model);
    }

    private async Task<byte[]?> Read(Uri address,int limit,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,address);
        request.Headers.UserAgent.ParseAdd("MojinDashuai/0.1.2");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        // Redirects are deliberately disabled on the dedicated HTTP client.
        NetworkPolicy.EnsureSuccess(response,"获取 LittleSkin 皮肤");
        if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("皮肤文件过大。");
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        using var output=new MemoryStream();
        var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,token))>0)
        {
            if(output.Length+count>limit)throw new InvalidDataException("皮肤文件过大。");
            output.Write(buffer,0,count);
        }
        return output.ToArray();
    }

    /// <summary>Only local configuration work is performed on the game launch path.</summary>
    public static bool ConfigureInstance(string instance,string source)
    {
        if(source is not ("account" or "littleskin"))return false;
        try
        {
            var path=ContentSecurity.SafePath(instance,"CustomSkinLoader/CustomSkinLoader.json");
            var original=File.Exists(path)?File.ReadAllText(path):null;
            if(original?.Length>1024*1024)return false;
            var config=original is null?new JsonObject():JsonNode.Parse(original,new JsonNodeOptions(),new JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip}) as JsonObject;
            if(config is null)return false;
            var items=(config["loadlist"] as JsonArray)?.Select(n=>n?.DeepClone()).ToArray()??[];
            var custom=items.Where(n=>!Managed(n)).ToArray();
            var mojang=items.FirstOrDefault(n=>n?["type"]?.GetValue<string>()=="MojangAPI")??new JsonObject{{"name","Mojang"},{"type","MojangAPI"}};
            var little=new JsonObject{{"name","LittleSkin"},{"type","CustomSkinAPI"},{"root",LittleSkinRoot}};
            var own=new JsonObject{{"name","Mojin"},{"type","CustomSkinAPI"},{"root",NetworkPolicy.DirectApi+"/v1/skins/csl/"}};
            var fallback=new JsonObject{{"name","Mojin Cloudflare"},{"type","CustomSkinAPI"},{"root",NetworkPolicy.LegacyApi+"/v1/skins/csl/"}};
            var list=new JsonArray();
            if(source=="littleskin")list.Add(little);
            list.Add(own);list.Add(fallback);list.Add(mojang);
            if(source=="account")list.Add(little);
            foreach(var item in custom)list.Add(item);
            config["loadlist"]=list;
            config["enableLocalProfileCache"]=true;
            var serialized=config.ToJsonString(new JsonSerializerOptions{WriteIndented=true})+"\n";
            if(serialized==original)return true;
            if(original is not null)
            {
                var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
                var backup=ContentSecurity.SafePath(instance,".hub/skin-config-backups/"+hash+".json");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if(!File.Exists(backup))File.WriteAllText(backup,original);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,serialized);File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return true;
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException){return false;}
    }

    private static bool Managed(JsonNode? node)
    {
        if(node is not JsonObject item)return false;
        if(item["type"]?.GetValue<string>()=="MojangAPI")return true;
        return item["type"]?.GetValue<string>()=="CustomSkinAPI"&&item["root"]?.GetValue<string>() is string root&&
            (root.TrimEnd('/')==LittleSkinRoot.TrimEnd('/')||root.TrimEnd('/')==NetworkPolicy.DirectApi+"/v1/skins/csl"||root.TrimEnd('/')==NetworkPolicy.LegacyApi+"/v1/skins/csl");
    }
}

public sealed class SkinTextureCache(string root)
{
    private sealed record Entry(SkinTexture Texture,DateTimeOffset UpdatedAt);
    public SkinTexture? Cached(string key)=>Read(key)?.Texture;
    public async Task<SkinTexture?> Get(string key,Func<CancellationToken,Task<SkinTexture?>> fetch,bool refresh=false,CancellationToken token=default)
        => (await Load(key,fetch,refresh,token)).Texture;
    public async Task<SkinLoadResult> Load(string key,Func<CancellationToken,Task<SkinTexture?>> fetch,bool refresh=false,CancellationToken token=default)
    {
        var cached=Read(key);
        if(!refresh&&cached?.UpdatedAt>=DateTimeOffset.UtcNow.AddMinutes(-10))return new(cached.Texture,"ready");
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var texture=await fetch(deadline.Token);
            if(texture is null){Forget(key);return new(null,"missing","尚未设置皮肤。");}
            texture=SkinImage.NormalizeTexture(texture);Store(key,texture);return new(texture,"ready");
        }
        catch(Exception ex)when(NetworkPolicy.IsNetwork(ex)||ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            var reason=ex is OperationCanceledException?"皮肤请求超时":NetworkPolicy.IsNetwork(ex)?"皮肤网络请求失败":ex is InvalidDataException or JsonException or FormatException?"皮肤数据格式不受支持":"皮肤读取失败";
            return new(cached?.Texture,cached is null?"error":"cached",reason+(cached is null?"，请稍后刷新。":"，正在显示此来源上次获取的皮肤。"));
        }
    }
    public void Store(string key,SkinTexture texture)
    {
        try
        {
            texture=SkinImage.NormalizeTexture(texture);Directory.CreateDirectory(root);var path=PathFor(key);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,JsonSerializer.Serialize(new Entry(texture,DateTimeOffset.UtcNow),Json.Options));File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
    }
    private void Forget(string key)
    {
        try{File.Delete(PathFor(key));}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
    }
    private Entry? Read(string key)
    {
        try
        {
            var path=PathFor(key);if(!File.Exists(path)||new FileInfo(path).Length>SkinImage.MaxTextureBytes*2)return null;
            var result=JsonSerializer.Deserialize<Entry>(File.ReadAllText(path),Json.Options);
            return result?.Texture is null?null:result with {Texture=SkinImage.NormalizeTexture(result.Texture)};
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException){return null;}
    }
    private string PathFor(string key)=>Path.Combine(root,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()+".json");
}
