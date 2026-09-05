# 一服自动连接适配

Forge 1.7.10 的 `--server` 在启动资源处理完成前开始连接。MSE 首次原生启动实测在连接开始后仍处理资源，超过 Forge 客户端握手的五秒等待，出现 `NetHandlerLoginClient` 类型转换错误；返回多人游戏重连成功。

`m3e/MojinAutoConnect.java` 是原生客户端附带的轻量连接组件。它在客户端完成初始化、进入正常 tick 循环后等待 20 个 tick，再在客户端线程上调用 Forge 自身的 `connectToServerAtStartup`。只尝试一次；退出服务器后不会擅自重连。读取启动器传入的服务器主机和端口，不读取账号或令牌。没有启动器参数时不执行任何连接，服务端也不需要安装此组件。

构建顺序：准备一服引擎依赖 → `python tools/build-game-integration.py` → `python tools/prepare-native-content.py`。使用已经固定的一服 Forge/Guava 库及 Java 25 编译器生成 Java 8 字节码；玩家端仍只使用其随包 Java 8。产物通过签名原生文件清单自动安装，标准外部启动器导入包保持原样。

源码依据：[Forge 1.7.10 FMLClientHandler](https://github.com/MinecraftForge/FML/blob/1.7.10/src/main/java/cpw/mods/fml/client/FMLClientHandler.java)。本组件调用公开方法，没有复制或修改 Forge 类。
