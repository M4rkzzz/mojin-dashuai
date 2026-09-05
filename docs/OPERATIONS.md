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

API 0.1.5 按用户要求放开超级邀请码的保护名限制：尚未绑定账号的名字可以注册；已经绑定账号的名字仍由唯一性约束保护。普通绑定邀请码保留原有用途。新注册不自动迁移、合并历史角色，也不改变游戏服离线验证方式。

## Cloudflare

按用户最新要求，Cloudflare Global API Key 明文保存在本机 `D:/project/cfapi/credentials.json`，不依赖 GitHub Secrets。通过 `python tools/cloudflare-local.py inspect` 验证，`provision-launcher` 配置专用 Tunnel/DNS。主密钥不上传服务器；仅将单独生成的 Tunnel 凭据安装至 `secrets/tunnel-token`。完整说明见 [共用凭据](CLOUDFLARE-CREDENTIALS.md)。

旧入口 `https://launcher.boshan.uk` 保留兼容，cloudflared 使用 HTTP/2。beta.10 使用 `https://launcher-direct.boshan.uk:21708` 的统一 HTTPS FRP 入口，不在玩家侧回退到旧域名或第三方文件源。Cloudflare 在此用于 DNS 等维护，不再是新版客户端的内容分发必经入口。

```sh
docker compose --profile tunnel up -d --no-deps cloudflared
```

账号 API 的内部端口仍仅绑定主机回环地址，由受控 HTTPS 网关转发；不要将内部明文 API 端口直接映射到公网。代理信任范围必须与网关和容器网络配置一致，不能盲目信任公网传入的转发头。

## 数据备份

`mc-client-hub-backup.timer` 每日执行 `backup.sh`，最近 7 份存于 `/vol1/mc-client-hub/backups`。升级前先执行一次备份，并另存升级快照。备份不挂载到 downloads。

还原应在停止本项目 hub-api 后操作，通过 `pg_restore` 恢复到新建数据库并核对账号数，再切换连接字符串。禁止在未经核对时覆盖当前数据库。

## 签名与版本

`Publisher keygen PRIVATE_PATH PUBLIC_PATH` 生成发布密钥，私钥不得进入 Git。将公钥加入原生 `launcher.json` 的 `publicKeys.release-1`。

`Publisher verify MANIFEST` 验证静态约束；`Publisher sign MANIFEST PRIVATE OUTPUT` 还要求独立验收文件明确通过干净 Windows、真实入服、全自动来源与精确 Java 版本。三服还需地图、任务书及机器界面验证。

`Publisher sign-catalog CATALOG PRIVATE OUTPUT` 签名目录。目录序号只能递增，授权回退放在新目录的 `rollbacks` 中。不要通过降低目录序号回滚。三服回退目标始终是已验证的 Cleanroom + Java 25。

上传和下载文件不附带玩家账号令牌。执行 `python tools/build-standard-packs.py --publish-missing` 时，缺少原站的文件自动从用户提供的客户端或固定缓存上传到自建源，保留原文件和现有内嵌声明；无需逐个补充发布出处。只核对必要的大小、SHA256 和公开地址可用性。操作细节见 [标准分发](STANDARD-DISTRIBUTIONS.md)。

玩家启动已安装实例时会先检查新版；临时网络故障允许使用已安装内容，签名或内容异常仍会报错。下载暂停后不进入游戏，续传完成后继续启动。手动回退在当前正式版本仍授权该回退时保持，下次正式版本更新或撤销该回退时恢复自动更新。


## 皮肤与 API 升级

`SkinPath=/data/skins` 对应 `/vol1/mc-client-hub/api/skins`，由 API 容器用户 1654 写入。`POST /v1/account/skin` 需要账号会话；`GET /v1/skins/{gameName}` 公开返回 PNG 和 `X-Skin-Model`，下载请求不需要令牌。皮肤资源接口不等于游戏内皮肤模组已经验收。

`protect-conflict` 将大小写冲突组整体保留，数据库中以空 `ExactName` 标记未核实身份。该组不能生成绑定邀请码，既有绑定码也无法认领；普通 `protect` 不会自动解除冲突。管理员需要先核实历史角色，不能自动选择拼写或合并角色。

先在隔离容器执行 `tests/api-acceptance.py`，通过后可在 124 执行 `python3 upgrade-api.py 0.1.2`。脚本检查发行目录和镜像，额外备份数据库，更新本项目 API，并验证健康状态；失败时恢复原镜像配置。只重建 `hub-api`，不操作其他服务。皮肤备份与数据库备份分别保留七份。

API 0.1.2 已部署，升级备份为 `/vol1/mc-client-hub/backups/upgrades/api-0.1.2-20260905T060658Z`。`GET /v1/catalog` 返回公开目录签名封包；`GET /v1/manifests/{instance}/{sequence}` 返回 `public/manifests/{instance}/{sequence}.signed.json`，只接受三个实例和正整数发布序号。未发布时分别返回 503/404；下载不需要账号凭据。客户端负责验签、哈希与发布序号验证。

## 微软登录验收

Windows 默认使用 CmlLib 的官方文档接法，`microsoftClientId` 留空。自有 Azure 应用 ID 仅用于可选设备码方式，不是默认登录的必需配置。用户就绪后，先重新构建 `tests/NativeAccountSmoke` 的 Release，再手动执行 `python tools/accept-microsoft-login.py --dotnet PATH_TO_DOTNET`。此命令会显示一个微软授权窗口，不显示启动器大厅或启动游戏。

成功后检查真实游戏所有权、DPAPI 会话恢复、静默认证、角色与皮肤，并生成不含账号标识或凭据的 `packs/launcher-acceptance.json`。认证代码或配置变化会使验收指纹失效；需要重新验收。合成测试仅使用 `--microsoft-contract`，不会写入真人验收记录。

## 启动器更新与当前 API

API 0.1.5 已部署。beta.10 的专项及最终发布状态见 `packs/beta10-acceptance.json`；不沿用 API 0.1.4 的检查次数作为本次结果。`/v1/launcher` 提供公开签名清单；尚未激活时返回 404。打包、签名、上传及激活见 [启动器更新](LAUNCHER-UPDATES.md)。历史 API 0.1.4 的升级备份与验收记录仍保留。

## 统一下载对象

三服原版资源、模组、依赖库、Java 和配置压缩包全部预存至 `/vol1/mc-client-hub/public/objects/sha256/{sha}`，客户端只请求统一 HTTPS 入口的对应路径。构建阶段保留原始来源用于维护，玩家侧不按原站列表切换来源。

`tools/audit-single-origin.py` 对三份 native 清单和保留文件做只读 SHA256 审计；`tools/stage-single-origin.py prepare INVENTORY OUTPUT.tar` 仅打包服务器缺少的本地对象。上传 tar 和脚本后，在 124 执行 `python3 stage-single-origin.py import INPUT.tar`：重新校验 SHA256、复用已有文件的硬链接、原子添加不可变对象，不改正式清单，不覆盖既有对象。

只有导入报告 `complete=true`，并完成统一入口实际下载、Java 安装和文件核验后，才切换本版正式清单。初始审计共 33,962 个对象、3,967,908,019 字节；不能将来源齐备当作下载验收通过。

## 内测目录发布

0.1.2-beta.10 已开放，用户批准暂缓干净 Windows 验收，记录见 `packs/beta-authorization.json`。`Publisher sign-beta` 只允许 beta 验收记录延后这一项，真实入服、正确 Java、自动来源以及三服地图/任务书/机器要求仍保留。稳定版 `sign` 不接受未通过干净 Windows 的记录。

```powershell
python tools/publish-catalog.py --beta --sequence 1 --key PRIVATE_KEY_PATH
python tools/publish-launcher-update.py artifacts/launcher-update-0.1.2-beta.10 --activate --beta
```

后续目录发布必须提高序号；脚本先上传不可变清单，保留旧目录备份，再原子切换。当前目录序号 1，启动器更新序号 10。本次 beta.10 未重启三服生产进程；beta.6 的图标重启已另有授权及记录。

三服统一入口空缓存安装、真实中断续传及文件校验均已通过，证据见 `packs/acceptance/beta8-network-install.json`。beta.10 的 UI、Windows 和 API 构建检查通过；已切换启动器签名清单，未重启三服。
