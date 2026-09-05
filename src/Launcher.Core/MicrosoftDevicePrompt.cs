using System.Text.RegularExpressions;

namespace Boshan.Launcher;

// This is the only part of a device-code response allowed across the WebView bridge.
public sealed record MicrosoftDevicePrompt(string UserCode, string VerificationUrl, DateTimeOffset ExpiresAt)
{
    public static MicrosoftDevicePrompt Create(string code, string address, DateTimeOffset expiresAt)
    {
        if (!Regex.IsMatch(code, "^[A-Z0-9-]{6,20}$", RegexOptions.CultureInvariant) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "microsoft.com" or "www.microsoft.com") ||
            !(uri.AbsolutePath.TrimEnd('/') is "/link" or "/devicelogin"))
            throw new InvalidDataException("微软登录页面地址无效，请重试。");
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("登录码已过期，请重新登录。");
        return new(code, uri.AbsoluteUri, expiresAt);
    }
}
