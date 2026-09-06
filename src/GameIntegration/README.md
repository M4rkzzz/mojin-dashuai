# 一、三服自动连接适配

Forge 1.7.10 的 `--server` 在启动资源处理完成前开始连接。MSE 首次原生启动实测在连接开始后仍处理资源，超过 Forge 客户端握手的五秒等待，出现 `NetHandlerLoginClient` 类型转换错误；返回多人游戏重连成功。

`m3e/MojinAutoConnect.java` 是原生客户端附带的轻量连接组件。它在客户端完成初始化、进入正常 tick 循环后等待 20 个 tick，再在客户端线程上调用 Forge 自身的 `connectToServerAtStartup`。只尝试一次；退出服务器后不会擅自重连。读取启动器传入的服务器主机和端口，不读取账号或令牌。没有启动器参数时不执行任何连接，服务端也不需要安装此组件。

构建顺序：准备一服引擎依赖 → `python tools/build-game-integration.py` → `python tools/prepare-native-content.py`。使用已经固定的一服 Forge/Guava 库及 Java 25 编译器生成 Java 8 字节码；玩家端仍只使用其随包 Java 8。产物通过签名原生文件清单自动安装，标准外部启动器导入包保持原样。

源码依据：[Forge 1.7.10 FMLClientHandler](https://github.com/MinecraftForge/FML/blob/1.7.10/src/main/java/cpw/mods/fml/client/FMLClientHandler.java)。本组件调用公开方法，没有复制或修改 Forge 类。

组件另为 Angelica 加入精确的重复日志过滤：只对 `SKIPPING glBindTexture for target 32879` 的 INFO 保留第一次，其他信息及 WARN/ERROR 原样保留。对应 Angelica 2.0.0-alpha19 源码先执行 OpenGL 纹理绑定再打印此提示；过滤不更改渲染。构建时使用实际 Log4j 2.0-beta9 跑 `tests/game-integration/RenderLogFilterCheck.java`，检查重复提示与错误级别。游戏中的日志增量仍要在下一次一服实测核对。

三服首次自动连接的实测现象是世界已加载，但 `GuiConnecting` 的“登入中”界面仍盖在画面上，点取消会退出连接；回主菜单重进正常。`mb/MojinAutoConnect.java` 使用 Cleanroom 的客户端 tick 事件推迟首次连接，原生层不再传入 `--server`，而是传入同样的主机和端口属性。组件使用固定 Cleanroom 0.5.17-alpha 库编译为 Java 25 字节码，不增加其他运行时或服务端组件。修正后的首次自动连接仍需游戏画面验证。

对应公开 API：[Cleanroom 0.5.17-alpha FMLClientHandler](https://github.com/CleanroomMC/Cleanroom/blob/0.5.17-alpha/src/main/java/net/minecraftforge/fml/client/FMLClientHandler.java)。只调用连接方法，不复制 Cleanroom 源码。

## 1.0 连接界面响应修复

旧自动连接组件在客户端 tick 调用 `connectToServerAtStartup`，该方法先查询状态并同步等待最多 30 秒，导致渲染线程无法绘制连接界面。启动器注入的 `join/JoinStartupConnection.java` 仅替换 Forge 1.7.10 与 Cleanroom 对应方法的入口：初始化服务器列表数据后，直接调用原有 `connectToServer`，由原生连接界面及其连接线程处理 DNS 和网络。保留正常登录握手、取消连接和返回菜单行为，不再在 tick 线程等待状态查询。二服 Forge 1.20.1 不匹配此补丁。

补丁随统一客户端认证代理交付，旧自动连接内容文件无需重新分发。`tests/game-integration/join/StartupConnectionCheck.java` 校验替换后的方法不执行原阻塞方法，并覆盖两种 ServerData 构造器；真实游戏验证见发布准备记录。
