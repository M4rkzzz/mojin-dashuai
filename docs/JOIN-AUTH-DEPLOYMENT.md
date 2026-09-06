# 入服认证 API 部署说明

API 版本 1.1.0。本文是发布准备资料，不代表生产已启用。

## 15:30 切换脚本

将 `deploy/activate-join-api.py` 传入 NAS。先以 root 执行 `python3 activate-join-api.py --prepare`：只创建私有目录 `/var/apps/mc-client-hub/staging/join-auth-1.0.0`，四服密钥在其中的 `server-keys.json`，权限 0600。重复执行沿用已有四份密钥。游戏服务端配置从这里取对应实例的值，不能另生成一组，也不要打印到终端。准备阶段不修改 API、网络或游戏服。

`--activate` 只允许从 **2026-09-06 15:30 北京时间**开始。前提是 `/var/apps/mc-client-hub/releases/api-1.1.0/api/Hub.Api.dll`、`boshan/hub-api:1.1.0` 镜像和 `/var/apps/mc-client-hub/upgrade-api.py` 已准备，隔离 PostgreSQL 验收已通过。脚本先保存 `api.env`、compose、网关配置、旧 API 文件和数据库备份，再把运行中的 `gsmanager` 接入 `mc-client-hub_edge`，以实际地址 `/32` 作为内部白名单，写入已准备的四个密钥，并通过既有 `upgrade-api.py` 仅更新 Hub API。

脚本不给游戏服务器启用 enforce，也不重启任何游戏进程或 gsmanager。公网网关补充 `/internal` 返回 404 并校验、reload；Cloudflare 直达 API 的流量由 API 的 TCP 来源白名单拒绝。激活记录包含版本、健康、内部 IP 和备份位置，不含密钥。`state.json` 以独占文件锁防重复执行；每次尝试有独立快照，`firstBackup` 保留首次快照，不覆盖失败证据。

失败会恢复旧私有环境、compose、API 和网关配置；若本次新加了 gsmanager 网络才会断开该新增连接。数据库新增表保留，避免覆盖切换期间新增账号；原数据库 dump 留作人工恢复。如果恢复不完整或进程被外部中断，脚本拒绝再次自动激活，先检查 `state.json` 所指快照。成功后重复运行只核对现有健康与网络，不再次重启。以后若重建 gsmanager 容器，需要重新接入网络并更新实际 IP 白名单。

## 部署配置

在 API 私有环境文件增加：

```
JoinAuth__Enabled=true
JoinAuth__InternalNetworks=<gsmanager 在 edge 私有网络内的固定 IPv4>/32
JoinAuth__ServerKeys__m3e=<该服独立随机密钥>
JoinAuth__ServerKeys__dc2=<该服独立随机密钥>
JoinAuth__ServerKeys__mb=<该服独立随机密钥>
JoinAuth__ServerKeys__vw=<该服独立随机密钥>
```

每个密钥使用独立 32 字节随机数（64 位十六进制即可），只存于 API 私有配置和对应游戏服务端，不放分发包、Git 或日志。API 容器保持既有网络和数据库配置。公网网关对 `/internal/` 整段返回 404/403，游戏服务端通过受控内网/加密通道访问 API；跨主机不能明文传递密钥。

内部兑换额外检查真实 TCP 来源，只有 `JoinAuth__InternalNetworks` CIDR 白名单中的地址可进入密钥核验（逗号分隔；未配置默认拒绝）。不使用 CF 或 X-Forwarded-* 请求头判断来源。生产只填游戏服务容器固定 `/32`，不要填整个 Docker 网段；Cloudflare 代理容器不能代替游戏服务访问。游戏服务与 API 同机时，可加入同一个私有 Docker 网络，通过 `http://hub-api:8080/internal/v1/join/redeem` 访问，不增加公网映射。

新镜像启动前可运行 `dotnet Hub.Api.dll admin join-init`；新版本启动也会执行相同幂等迁移。仅新增三张表和索引，保留所有原表、角色精确名和离线 UUID。`InitializeDatabase` 保留原值即可。API 启动成功不代表游戏服启用拦截；认证适配器仍需单独部署和逐服验收。

Docker 最小步骤：构建 1.1.0 镜像；备份数据库与私有环境文件；增加上述环境配置；用既有 compose 更新 **api 一个服务**；检查 `/health`；从公网确认 `/internal/` 被阻挡；从游戏服内部通道确认兑换端点可达且无密钥被拒。不重启数据库、FRP 或其他无关服务。

## 历史身份关联

```
dotnet Hub.Api.dll admin join-init
dotnet Hub.Api.dll admin join-list
dotnet Hub.Api.dll admin join-protected
dotnet Hub.Api.dll admin join-bind-minecraft <官方UUID> <精确游戏名>
```

`join-list` 仅列角色名、绑定提供方布尔值、禁用状态及离线 UUID；`join-protected` 列仍未关联微软的受保护名字，不显示任何凭据。管理员先通过官方资料和历史角色归属核实，再执行绑定。若该名字已属于群服账号，默认拒绝；只有独立确认两种登录确属同一人，才追加 `--link-existing-hub`。不同大小写、已有其他微软 UUID、官方改名均不自动覆盖或合并。

普通微软首次交换，遇受保护名或已有群服同名返回 409，需要上述管理员核实。已关联的身份每次仍需服务端验证官方 access token；客户端传来的名字/UUID不作为证明。

## API 合同

- `POST /v1/auth/minecraft/exchange`，JSON `{accessToken}`：仅固定官方 HTTPS profile、entitlements 验证；不跟重定向，合计 12 秒超时。返回 `{accessToken, expiresAt, gameName, gameUuid}`，入服授权 10 分钟，不保存官方凭据。
- `POST /v1/join/tickets`，Bearer 现有 Hub 会话或上一步授权；JSON `{instance}`。返回 `{ticket, expiresAt, gameName, gameUuid}`。票据是 32 字节随机数，Base64URL 43 字符，120 秒有效。只持久化 SHA-256。
- `POST /internal/v1/join/redeem`，Bearer 对应服独立服务密钥；JSON `{ticket, instance, gameName}`。成功 `{allowed:true, gameName, gameUuid}`；失败非 2xx 且不返回身份。事务行锁保证并发兑换只有一次成功。每次校验精确名字、实例、有效期、未消费、账号禁用和授权撤销。

所有响应 no-store，失败不记录传入凭据。账号领票每分钟 30 次，服务核验每服每分钟 240 次，避免按 FRP 共享 IP 合并配额。API 每 5 分钟清理过期记录；游戏运行期间无需认证心跳。

## 验收

`JoinAuthTests` 验证票据格式/UUID、范围绑定、过期/消费/禁用拒绝、固定微软端点/所有权/重定向拒绝以及独立配额。`tests/join-api-acceptance.py` 在目标宿主的一次性数据库和隔离 API 容器验证真实 PostgreSQL 并发消费、跨服/大小写/撤销拒绝，完成后移除该隔离容器和数据库。生产切换前还需新统一端与三个网络实现的互通，以及每服“统一端可入、普通客户端拒绝”结果。单测不能替代这些上线验收。

2026-09-06 本地 9 项 `JoinAuthTests` 通过；NAS `boshan/hub-api:1.1.0` 的隔离 PostgreSQL 验收通过，包括真实并发兑换只有一次成功、幂等建表、撤销和身份冲突拒绝。首次隔离尝试因为测试容器缺少 edge 网络而无法从宿主连接，补齐与生产相同的网络后通过；失败证据保留在 NAS 私有 secrets 目录，测试容器/数据库已清理。API 未因此部署生产。

`tests/test_join_api_activation.py` 使用本地独立临时目录模拟升级失败，确认环境、compose、API、网关全部恢复；新增网络撤销；重复失败时保留第一次快照。测试不调用 NAS，也不重启任何服务。

`NativeAccountSmoke --join-api-identity` 使用现有 M4rkzzz 的 DPAPI 副本做静默官方验证，仅输出角色名、官方 UUID 和服务端离线 UUID，已通过。15:30 API 激活并完成管理员关联后运行 `NativeAccountSmoke --join-api-live dc2`，验证真实微软 exchange→ticket；不会打印票据、grant 或官方 token，不接收内部服务密钥，不把领票结果记为游戏服兑换/入服验收。临时加密副本完成后删除，原账号文件不修改。

在新 API 初始化前也可执行 NAS `python3 tools/join-identity-preflight.py M4rkzzz`，它只查受保护名字和现有绑定元数据，不修改数据库，不读取会话或密码。14:43 的只读结果为：M4rkzzz 受保护，精确大小写一致，无同名 Hub 账号，生产入服身份表尚未创建。
