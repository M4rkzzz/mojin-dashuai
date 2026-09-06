# 统一端登录前认证组件

`mojin-join-agent.jar` 是 Java 8 字节码的通用 premain agent。ASM 已重定位到组件自己的包，不使用或更换 Forge/Cleanroom 的 ASM。完整 ASM BSD 许可随 JAR 分发。

## 客户端

由统一端提取组件并注入：

```
-javaagent:<实例>/.hub/join/agent-<sha256>.jar
-Dmojin.join.instance=m3e
-Dmojin.join.pipe=mojin-join-<32位随机十六进制>
```

实例仅允许 `m3e/dc2/mb/vw`。仅在 LOGIN 握手构造期间领取新票据；服务器列表 STATUS 查询不申请、不附带票据。管道为 `\\.\pipe\<属性值>`，一行 UTF-8 JSON：请求 `{"instance":"m3e"}`；响应含 `ticket`，43 位 Base64URL。响应中的账号凭据不得存在，也不需要将账号令牌给 Java。

Windows `RandomAccessFile` 命名管道已在实际 Java 8、17、25 运行时验证。IPC 在有界工作线程执行，连接线程最多等 20 秒；不会阻塞 Netty 收包线程。不存在加载完成后的计时刷新。

旧版 Forge 的 `FMLSecurityManager` 会通过 `FilePermission` 路径规范化短暂探测管道。打开管道遇到 `FileNotFoundException` 时最多重试 3 秒、间隔 25 ms，让统一端丢弃空探测并重新接受正式请求；不更换管道、不绕过进程身份校验、不降级认证。

1.7.10 / Cleanroom 客户端同时替换 Forge 的启动连接入口，直接进入游戏原生连接页面，避免 `connectToServerAtStartup` 在主线程等待服务器状态查询 30 秒。保留服务器列表初始化和正常 Forge 连接逻辑。

握手主机字段附加 `NUL + MOJIN1 + NUL + ticket`；为 Forge 随后追加的标记预留 16 字符。服务端在原握手处理前移除此扩展，原地址和 FML 尾缀字节保持原序。任何实际域名解析/套接字目标不受影响。

## 服务端

1.0 客户端内嵌 JAR 固定为 `d870e72522897d529ef35ac5b1f48fb8478216eff5eb4e44de2b9495fdce43d6`，服务端单独使用 `mojin-join-server-agent.jar`。两者使用相同线协议，服务端解码兼容修复无需玩家重新安装统一端。

把服务端 JAR 放在私有运维目录，添加在 `-jar` 或主类之前：

```
-javaagent:/private/mojin-join-server-agent.jar
-Dmojin.join.server.config=/private/join-auth.properties
```

配置 UTF-8，不要收入 Git、玩家内容包或诊断导出：

```properties
mode=observe
instance=m3e
redeemUrl=http://hub-api:8080/internal/v1/join/redeem
allowLocalContainerHttp=true
secret=<该服独立服务凭据，至少24字符>
```

只有明确开启 `allowLocalContainerHttp` 时允许上述精确 Docker 服务 URL。其余 URL 必须 HTTPS 或 loopback HTTP；拒绝 userinfo/query/fragment 和 HTTP 重定向。

`mode` 只接受 `off/observe/enforce`，每次新握手按文件 mtime 检查，不做 tick 轮询。改 mode 后下一条连接生效，无需再次重启。URL、凭据、实例固定在进程启动时读入。配置缺失/损坏/非法 mode 会切到 enforce，不会自动关闭认证。已放行连接不会因模式切换追踢。

- **off**：还原扩展，保持原登录行为。
- **observe**：普通无票据登录放行并记录；有票据异步核验，失败也记录后放行。
- **enforce**：无票据、重复登录、错误顺序或核验失败在原登录包处理前拒绝。

每服最多 32 个并行核验；不排无界队列。内部 HTTP 连接/读超时各 8 秒，响应最多 8 KiB，单次票据只提交一次。重连必须重新通过统一端申请新票据。原登录包在回到所属 Netty 线程后仅递交一次。

服务端在握手包读取原始主机字符串后、Forge 的 NUL 分割前捕获票据，仅移除自定义扩展，原 FML/FML3 尾缀交还 Forge。票据按包对象存入弱映射，NetworkManager 收到握手时取走。收包注入及异步回送只选规范的 `channelRead0`，不匹配 Mixin 的同形辅助方法。若运行时类注入失败，私有配置目录旁保存固定错误类别与失败输入 class，便于定位早期控制台日志没有落盘的加载失败；这些文件不包含账号令牌或票据。

兑换 POST 的请求为 `ticket/instance/gameName`，Bearer 是私有配置凭据；只有 HTTP 200、`allowed=true`、精确名字相符、返回 UUID 与该名字既有 `OfflinePlayer:` UUID 相符时才放行。不改 online-mode，不覆盖游戏登录包、玩家 UUID、名字或存档。日志不打印票据、管道响应、扩展主机串或服务凭据。

## 三种固定协议

| 实例 | 网络入口 | 登录包 |
| --- | --- | --- |
| m3e / vw | `NetworkManager` | `C00PacketLoginStart` |
| mb | `NetworkManager` | `CPacketLoginStart` |
| dc2 | `Connection` | `ServerboundHelloPacket` |

未知的认证前数据包拒绝，不降级为进入世界后再踢。网络钩子形状不符合固定版本时，故意令目标类加载失败，避免 Instrumentation 静默吞掉变换错误而无认证启动。

## 构建与验收范围

```
python tools/build-join-agent.py
python tools/build-join-agent.py --server
python tests/game-integration/join/check.py
python tests/game-integration/join/check-fixed.py
python tests/game-integration/join/check-security.py
python tests/game-integration/join/prepare-server-classes.py
python tests/game-integration/join/check-wire.py
```

构建依赖固定 ASM 9.10.1 缓存和现有本机 Java 工具链；此版本支持 Cleanroom 在运行时使用的 Java 25 字节码。`check.py` 使用实际 Windows 管道、HTTP、Netty 与固定映射的包形状，覆盖三个 Java 运行时、三版登录包、无票据拒绝、单次异步递交、同票据重放、错误名字/UUID、HTTP 503、重复登录、热切换及配置损坏。`check-fixed.py` 使用三个固定真实游戏 JAR 的六个类做注入和 ASM 数据流校验。

`check-security.py` 使用实际 Java 8 和 Forge 的安全管理器，确认发生空探测后能重新接受管道连接，且正式票据请求只有一次。真实游戏启动与真实统一端管道互通另记录在本地验收报告中。

`prepare-server-classes.py` 从 NAS 只读复制的固定原包重建解码类并校验 Cleanroom 补丁源的 Adler32；`check-wire.py` 使用这些真实握手类、实际 1.20.1 服务端 SRG 类及 Forge 47.4.0 带 FML3 分割的类，运行实际解码字节码，再经真实 Netty/HTTP 认证链路验证有票据和无票据两条路径。依赖 `.local/join-agent` 中该准备脚本注明的三个原包副本；客户端线上的附票测试不能替代这项服务端解码验收。

**这些是组件验收，不代表四个线上服务器已经开启认证或实际入服成功。** 现场仍需每服实际认证连接成功、普通客户端在登录前被拒；四服重启/模式切换由发布流程执行。客户端和服务端注入不能错开后直接假定旧服能识别新的扩展。
