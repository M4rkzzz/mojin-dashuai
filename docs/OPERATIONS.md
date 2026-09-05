# 运维

配置目录 `/var/apps/mc-client-hub`，持久数据 `/vol1/mc-client-hub`。使用独立 Compose，不操作生产游戏容器。

下载入口的地址、端口与验证方法见 [FRP 配置](FRP.md)。

## 管理命令

在 124 上进入配置目录后执行：

```sh
docker compose exec hub-api dotnet Hub.Api.dll admin invite-create super
docker compose exec hub-api dotnet Hub.Api.dll admin invite-create single OldPlayer 7
docker compose exec hub-api dotnet Hub.Api.dll admin invite-revoke INVITATION_ID
docker compose exec hub-api dotnet Hub.Api.dll admin invite-list
docker compose exec hub-api dotnet Hub.Api.dll admin invite-uses INVITATION_ID
docker compose exec hub-api dotnet Hub.Api.dll admin protect OldPlayer
docker compose exec hub-api dotnet Hub.Api.dll admin protect-conflict CaseName casename
docker compose exec hub-api dotnet Hub.Api.dll admin disable LoginName
docker compose exec hub-api dotnet Hub.Api.dll admin reset LoginName
```

普通邀请码只能使用一次；超级邀请码重复使用且默认不自动过期。任何邀请码均不赋予游戏权限。邀请码和管理员重置凭据只在生成命令的终端显示一次，请勿把终端输出复制到公开日志。

上线注册前，先导入三个游戏服的既有玩家名字为受保护名字；保留原大小写。不要公开发放未保护旧名字时生成的超级邀请码。

## Cloudflare

按用户最新要求，Cloudflare Global API Key 明文保存在本机 `D:/project/cfapi/credentials.json`，不依赖 GitHub Secrets。通过 `python tools/cloudflare-local.py inspect` 验证，`provision-launcher` 配置专用 Tunnel/DNS。主密钥不上传服务器；仅将单独生成的 Tunnel 凭据安装至 `secrets/tunnel-token`。完整说明见 [共用凭据](CLOUDFLARE-CREDENTIALS.md)。

当前入口 `https://launcher.boshan.uk` 已接通；cloudflared 固定使用 HTTP/2，因为 124 的出站 UDP 不通。API 主机的 Browser Integrity Check 单独关闭，账号限流与验证由 API 执行。

```sh
docker compose --profile tunnel up -d --no-deps cloudflared
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


## 皮肤与 API 升级

`SkinPath=/data/skins` 对应 `/vol1/mc-client-hub/api/skins`，由 API 容器用户 1654 写入。`POST /v1/account/skin` 需要账号会话；`GET /v1/skins/{gameName}` 公开返回 PNG 和 `X-Skin-Model`，下载请求不需要令牌。皮肤资源接口不等于游戏内皮肤模组已经验收。

`protect-conflict` 将大小写冲突组整体保留，数据库中以空 `ExactName` 标记未核实身份。该组不能生成绑定邀请码，既有绑定码也无法认领；普通 `protect` 不会自动解除冲突。管理员需要先核实历史角色，不能自动选择拼写或合并角色。

先在隔离容器执行 `tests/api-acceptance.py`，通过后可在 124 执行 `python3 upgrade-api.py 0.1.1`。脚本检查发行目录和镜像，额外备份数据库，更新本项目 API，并验证健康状态；失败时恢复原镜像配置。只重建 `hub-api`，不操作其他服务。皮肤备份与数据库备份分别保留七份。
