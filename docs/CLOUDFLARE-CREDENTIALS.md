# Cloudflare 共用凭据

2026-09-05 最新决定：用户明确要求在本地明文保存可复用的 Global API Key，后续直接调用 API，不再依赖原 GitHub 仓库。此决定替代此前将新密钥放入 Actions Secrets 的方案。

## 当前使用方式

凭据位于 `D:/project/cfapi/credentials.json`，在代码仓库之外。已按用户提供的值保存并验证可访问目标账户与域名。不要将实际内容复制到本文档、Git、日志或回复中。

```powershell
python tools/cloudflare-local.py inspect
python tools/cloudflare-local.py provision-launcher
python tools/cloudflare-local.py configure-native-api
```

本地工具使用 Global API Key + 邮箱请求 Cloudflare API，禁止凭据跟随重定向；输出仅包含状态和资源数量。`provision-launcher` 仅配置专用 Tunnel 与 `launcher.boshan.uk`，遇到已有不同目标的域名会停止。Tunnel 部署凭据单独写到 `D:/project/cfapi/mojin-tunnel-token.txt`；主密钥不发到 124。

## 先前准备的 GitHub 方式（当前无需使用）

- 账号邮箱：`1464549052@qq.com`
- 域名：`boshan.uk`
- 管理页：https://dash.cloudflare.com/profile/api-tokens
- Secrets：https://github.com/M4rkzzz/cloud-mail/settings/secrets/actions
- 新增 `CLOUDFLARE_GLOBAL_API_KEY`，值从 **API Keys → Global API Key → View** 取得。
- 既有 `CLOUDFLARE_ACCOUNT_ID` 与 `CLOUDFLARE_API_TOKEN` 保留。

Global API Key 与用户账号具有相同权限。工作流优先使用 Global API Key + 账号邮箱；未配置时仍使用旧 Token。Global API Key 不能作为 Bearer Token 传递，也不能填入旧 `CLOUDFLARE_API_TOKEN`。

现有旧 Token 新建 Tunnel 返回 403，不能靠修改仓库 Secret 名称增加权限。新增 Key 必须由账号持有人在 Cloudflare 查看并直接保存到 GitHub；不要发到聊天、源码、命令行参数或日志。GitHub 不能读取 Secret 原值。

后续任务在此仓库执行经用户授权的具体 API 工作流，不需要把主密钥复制到每个项目。这里只保存使用方式和名称，不保存凭据原值。可用密钥不代表未来任务获得任意修改授权。

本项目工作流：

- `boshan-launcher-access.yml`：只读检查 Zone、DNS、Tunnel；仅输出状态和数量。
- `boshan-launcher-provision.yml`：创建专用 Tunnel 和 `launcher.boshan.uk`，部署所需 Tunnel 凭据以接收端 RSA 公钥加密交付，不导出 Global API Key。
- 仓库内对应源文件：`tools/cloudflare-permissions.yml`、`tools/cloudflare-provision.yml`。

参考：https://developers.cloudflare.com/fundamentals/api/get-started/keys/
