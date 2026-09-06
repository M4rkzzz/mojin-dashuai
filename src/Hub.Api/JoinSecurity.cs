using System.Security.Cryptography;
using System.Text;

namespace Boshan.Hub;

public static class JoinSecurity
{
    public static bool ValidInstance(string? instance) => instance is "m3e" or "dc2" or "mb" or "vw";
    public static string NewTicket() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool ValidTicket(string? ticket) => ticket is { Length: 43 } && ticket.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidBearer(string? token) => token is { Length: 64 } && token.All(c => char.IsAsciiHexDigit(c));
    public static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
    public static JoinIdentity ForHubUser(HubUser u) => new() { HubUserId = u.Id, GameName = u.GameName, GameNameKey = u.GameNameKey, GameUuid = OfflineUuid(u.GameName) };
    public static bool Matches(JoinTicket ticket, JoinIdentity identity, string instance, string name, DateTimeOffset now) =>
        !identity.Disabled && ticket.ConsumedAt is null && ticket.ExpiresAt > now && ticket.InstanceId == instance &&
        ticket.ExactName == name && identity.GameName == name && ticket.GameUuid == identity.GameUuid;
}
