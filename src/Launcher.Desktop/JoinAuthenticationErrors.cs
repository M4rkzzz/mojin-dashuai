using System.IO;
using System.Net.Http;
using Boshan.Launcher;

namespace Boshan.Desktop;

internal sealed record JoinAuthenticationError(string ErrorCode,string Error);

/// <summary>Only fixed player-facing messages cross the game pipe; never forward API exception text.</summary>
internal static class JoinAuthenticationErrors
{
    internal const string HttpStatusKey="MojinAccountHttpStatus";
    internal static JoinAuthenticationError From(Exception error)
    {
        for(Exception? current=error;current is not null;current=current.InnerException)
        {
            if(current is InvalidDataException)
            {
                // These exact local messages distinguish a switched account and an ownership
                // failure from a session expiry. Unrecognized server text is never displayed.
                var known=current.Message switch
                {
                    "启动账号已切换，请从统一客户端重新启动游戏。" or "入服角色与启动账号不一致。"=>"account_changed",
                    "微软登录已失效，或此账号没有 Minecraft Java 版。" or "微软账号资料或 Minecraft Java 版所有权验证失败。" or "此微软账号未拥有 Minecraft Java 版。" or "此微软账号没有可用的 Minecraft Java 版角色。" or "微软账号未返回有效的 Minecraft 角色。"=>"ownership_required",
                    "请先登录账号。" or "请重新登录统一客户端。" or "登录已失效，请重新登录。" or "登录已过期。" or "微软账号需要重新登录。" or "正版登录配置已更新，请重新登录。" or "微软登录凭据已失效，请重新登录。" or "Minecraft 账号验证未完成，请重新登录。" or "微软授权未完成，请重新登录。" or "微软登录未完成，请重新登录。" or "登录码已过期，请重新登录。"=>"login_expired",
                    "Minecraft 所有权验证暂时不可用，请稍后重试。" or "微软登录暂时不可用，请稍后重试。"=>"service_unavailable",
                    "入服授权响应无效。" or "入服凭据响应无效。" or "入服凭据校验失败。" or "服务器无效。"=>"invalid_response",
                    _=>null
                };
                if(known is not null)return ForCode(known);
            }
            var status=current.Data[HttpStatusKey] is int number?number:current is HttpRequestException {StatusCode:{} http}?(int)http:(int?)null;
            if(status==409)return ForCode("role_conflict");
            if(status is 401 or 403)return ForCode("login_expired");
            if(status==429)return ForCode("rate_limited");
            if(status is 408 or 504)return ForCode("service_timeout");
            if(status is >=500)return ForCode("service_unavailable");
            if(current is OperationCanceledException or TimeoutException)return ForCode("service_timeout");
        }
        if(NetworkPolicy.IsNetwork(error))return ForCode("service_unavailable");
        return ForCode("authentication_failed");
    }

    internal static JoinAuthenticationError ForCode(string code)=>new(code,code switch
    {
        "role_conflict"=>"此角色的归属或名称存在冲突，请联系管理员核实关联后再连接。",
        "login_expired"=>"登录已失效，请回统一客户端重新登录，再重新启动游戏。",
        "account_changed"=>"启动账号已切换，请从统一客户端重新启动游戏。",
        "ownership_required"=>"登录或 Minecraft Java 版所有权验证未通过，请使用拥有 Java 版的微软账号重新登录。",
        "service_unavailable"=>"入服认证服务暂时无法连接，请检查网络后重试；仍失败请联系管理员。",
        "service_timeout"=>"入服认证请求超时，请稍后重新连接；仍失败请检查网络。",
        "rate_limited"=>"入服请求过于频繁，请稍后重新连接。",
        "invalid_response"=>"入服认证返回异常，请更新统一客户端后重试；仍失败请联系管理员。",
        _=>"入服认证未通过，请回统一客户端检查登录状态；仍失败请联系管理员。"
    });
}
