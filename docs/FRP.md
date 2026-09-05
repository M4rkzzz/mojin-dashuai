# 下载服务 FRP 配置

2026-09-05 核实：下载服务已经监听 `192.168.5.124:18080`。河北阿里云、宿迁三网分别建立一条 TCP 隧道，两条线路平级。

| 面板字段 | 值 |
| --- | --- |
| 隧道名称 | `mojin-downloads` |
| 隧道类型 | `TCP` |
| 本地地址 | `192.168.5.124` |
| 本地端口 | `18080` |
| 远程端口 | 对应节点实际分配的空闲端口 |

FRP 客户端所在机器必须能访问 `192.168.5.124:18080`。下载容器绑定的是这个局域网地址，不能直接将本地地址改为 `127.0.0.1`。如果 frpc 在容器中，容器同样需要能访问该局域网地址。

使用 TOML 时，在对应节点已有的 frpc 配置中追加代理块，保留原有 `serverAddr`、`serverPort` 和认证配置：

```toml
[[proxies]]
name = "mojin-downloads"
type = "tcp"
localIP = "192.168.5.124"
localPort = 18080
remotePort = 18080 # 示例；替换为该节点实际分配的端口
```

两个不同 frps 节点需要各自的 frpc 连接或由穿透面板分别管理；不能在同一 TOML 中写两个顶层 `serverAddr`。此配置只新增下载代理，不替换已有游戏代理。

检查：

1. 在 frpc 所在机器访问 `http://192.168.5.124:18080/health`，应返回 `ok`。
2. 启动新增代理，再从外网访问 `http://节点公网地址:远程端口/health`，应返回 `ok`。
3. 根路径 `/` 没有索引页，返回 404 不代表故障。
4. 将两条实际公网入口写入经签名的内容清单，并完成真实文件下载、SHA256 和断点续传检查。健康检查不代表三服内容已经发布。

下载服务仅公开 `/vol1/mc-client-hub/public`，不需要账号密码或访问令牌。文件清单通过 HTTPS 获取并验证 ECDSA 签名，下载文件按清单验证 SHA256。

账号 API 当前绑定 `127.0.0.1:18081`，是内部 HTTP 接口；不应按上述下载代理直接开放。账号入口仍为计划中的 `https://launcher.boshan.uk`，待 Cloudflare Tunnel 凭据配置。若另行采用 FRP 承载账号入口，需先配置有效 HTTPS 证书与受信任反向代理。FRP 传输加密不能替代玩家到公网入口之间的 HTTPS。

参考：[FRP TCP 配置](https://gofrp.org/en/docs/examples/ssh/)、[FRP HTTP 与 HTTPS](https://gofrp.org/en/docs/examples/vhost-http/)。
