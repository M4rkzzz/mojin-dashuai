using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Boshan.Hub;

public static class GameSkinEndpoints
{
    public static void MapGameSkins(this WebApplication app)
    {
        // CustomSkinLoader's public CustomSkinAPI format. No account/session
        // identifiers are exposed; texture URLs change when the PNG changes.
        app.MapGet("/v1/skins/csl/{gameName}.json", async (string gameName, HubDb db, SkinService skins, HttpContext context) =>
        {
            var skin = await Find(gameName, db, skins);
            if (skin is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(new { username = gameName, skins = new Dictionary<string,string>
            {
                [skin.Value.Model == "slim" ? "slim" : "default"] = gameName + "/" + skin.Value.Hash + ".png"
            }});
        });
        app.MapGet("/v1/skins/csl/textures/{gameName}/{hash}.png", async (string gameName, string hash, HubDb db, SkinService skins, HttpContext context) =>
        {
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return Results.NotFound();
            var skin = await Find(gameName, db, skins);
            if (skin is null || skin.Value.Hash != hash) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=86400, immutable";
            return Results.File(skin.Value.Png, "image/png");
        });
    }

    private static async Task<(byte[] Png, string Model, string Hash)?> Find(string gameName, HubDb db, SkinService skins)
    {
        if (!Secret.GameNamePattern().IsMatch(gameName)) return null;
        var key = Secret.NameKey(gameName);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.GameNameKey == key && !x.Disabled);
        var skin = user is null ? null : await skins.Read(user.Id);
        if (skin is null) return null;
        var png = Convert.FromBase64String(skin.PngBase64);
        return (png, skin.Model, Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
    }
}
