# 运维

配置目录 `/var/apps/mc-client-hub`，持久数据 `/vol1/mc-client-hub`。使用独立 Compose，不操作生产游戏容器。

## 管理命令

在 124 上进入配置目录后执行：

```sh
docker compose exec hub-api dotnet Hub.Api.dll admin invite-create super
docker compose exec hub-api dotnet Hub.Api.dll admin invite-create single OldPlayer 7
docker compose exec hub-api dotnet Hub.Api.dll admin invite-revoke INVITATION_ID
docker compose exec hub-api dotnet Hub.Api.dll admin invite-list
docker compose exec hub-api dotnet Hub.Api.dll admin invite-uses INVITATION_ID
docker compose exec hub-api dotnet Hub.Api.dll admin protect OldPlayer
docker compose exec hub-api dotnet Hub.Api.dll admin disable LoginName
docker compose exec hub-api dotnet Hub.Api.dll admin reset LoginName
```

普通邀请码只能使用一次；超级邀请码重复使用且默认不自动过期。任何邀请码均不赋予游戏权限。邀请码和管理员重置凭据只在生成命令的终端显示一次，请勿把终端输出复制到公开日志。

上线注册前，先导入三个游戏服的既有玩家名字为受保护名字；保留原大小写。不要公开发放未保护旧名字时生成的超级邀请码。

## Cloudflare

新项目仓库：https://github.com/M4rkzzz/mojin-dashuai 。在其 Actions Secrets 保存独立最小权限 Token 和 Account ID。原仓库的现有 Token 已验证缺少创建 Tunnel 的权限，不能通过重复调用绕过该限制。

`tools/cloudflare-provision.yml` 为手动运维工作流模板，只处理 `boshan-client-hub` Tunnel 与 `launcher.boshan.uk`。Tunnel 凭据仅以本机 RSA 公钥加密的密文交付，私钥保持本机私有目录；解密后将 Tunnel 凭据安装至 `secrets/tunnel-token`。

```sh
docker compose --profile tunnel up -d cloudflared
```

账号 API 的直接端口仅绑定主机回环地址。`TrustCloudflareTunnel=true` 只允许在此独立 Tunnel 网络结构使用，不要把 API 端口直接映射到公网。

## 数据备份

`mc-client-hub-backup.timer` 每日执行 `backup.sh`，最近 7 份存于 `/vol1/mc-client-hub/backups`。升级前先执行一次备份，并另存升级快照。备份不挂载到 downloads。

还原应在停止本项目 hub-api 后操作，通过 `pg_restore` 恢复到新建数据库并核对账号数，再切换连接字符串。禁止在未经核对时覆盖当前数据库。

## 签名与版本

`Publisher keygen PRIVATE_PATH PUBLIC_PATH` 生成发布密钥，私钥不得进入 Git。将公钥加入原生 `launcher.json` 的 `publicKeys.release-1`。

`Publisher verify MANIFEST` 验证静态约束；`Publisher sign MANIFEST PRIVATE OUTPUT` 还要求独立验收文件明确通过干净 Windows、真实入服、全自动来源与精确 Java 版本。三服还需地图、任务书及机器界面验证。

`Publisher sign-catalog CATALOG PRIVATE OUTPUT` 签名目录。目录序号只能递增，授权回退放在新目录的 `rollbacks` 中。不要通过降低目录序号回滚。三服回退目标始终是已验证的 Cleanroom + Java 25。

上传文件时不附带账号令牌。发布大文件前逐项完成 `packs/*-source-audit.json` 的来源和分发依据审计；“本地已有”不构成再分发依据。
