# 游戏显卡偏好

“优先独立显卡”默认开启，可在游戏设置中关闭。该选项申请 Windows 的高性能图形偏好，并不能保证所有设备最终都使用独显。实际渲染设备需要通过游戏 F3 的显卡信息或 OpenGL renderer 日志确认。

## 接入方式

- 先完成 `GameLauncher.Prepare`，再取最终 `process.StartInfo.FileName`。
- 在 `process.Start()` 前调用 `GraphicsPreference.Apply(javaPath, enabled, appData)`。不得在仅下载、运行时校验或无窗口安装验收中调用。
- 用户关闭选项时调用 `GraphicsPreference.RestoreAll(appData)`，恢复所有已记录的游戏 Java 偏好。
- 方法返回 `Status`、`Message`、`Changed`、`Success`。错误信息不包含用户路径；失败继续启动游戏，不提权、不打开 UAC、不修改驱动或全局图形设置。

三服目前的清单均指定各自 Temurin 运行时中的 `bin/java.exe`；手动 Java 仍应使用最终启动进程中的路径，不使用固定运行时名称、启动器路径或另一服的 Java。辅助实现也接受实际选中的 `javaw.exe`。

## 写入与恢复

仅修改当前 Windows 用户的 `Software\Microsoft\DirectX\UserGpuPreferences`，值名为实际游戏 Java 的完整路径，`GpuPreference` 设置为 `2`。同一个值中 HDR 等其他字段及字符串类型保持不变。原本已为高性能的值不纳入本启动器管理，关闭选项时也不删除。

首次修改前，将完整原值与准备写入的值原子保存到 `appData/graphics-preferences.json`。状态文件使用独占锁协调多个启动器进程。缺少记录、记录损坏、目录不可写或注册表失败时，不冒险覆盖现有偏好。

关闭时仅在当前注册表值仍与本启动器留下的完整值一致时恢复原值；原值不存在则删除我们创建的该项。发现玩家或其他程序改动后保留当前值，并停止在后续启动中重新覆盖，直到用户再次明确关闭、开启该选项。已检测到外部改动的记录不会在关闭时恢复旧值。Windows 注册表未提供这里可用的跨进程比较交换操作，仍存在外部程序恰好在最终读取与写入之间同时修改的极短竞争窗口。

## PCL 官方依据

研究固定于官方 `Meloong-Git/PCL` 提交 `8f7686457443e790670ee22157d98eee8ca2e20c`：

- [`Settings.vb:108`](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/Plain%20Craft%20Launcher%202/Pages/PageSetup/Settings.vb#L108)：`LaunchAdvanceGraphicCard` 默认 `True`。
- [`ModMain.vb:801–811`](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/Plain%20Craft%20Launcher%202/Modules/ModMain.vb#L801-L811)：写入当前用户的 `UserGpuPreferences` / `GpuPreference=2;`。
- [`ModLaunch.vb:1757–1779`](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb#L1757-L1779)：每次启动前按开关处理选定 Java 和 PCL 自身；失败尝试管理员辅助进程。本客户端只处理游戏 Java，失败不提权。
- [`Java.cs:15`](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/PCLCS/Java.cs#L15)、[`ModLaunch.vb:2123`](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb#L2123)：当前 PCL 使用所选运行时中的 `java.exe`。

公开源码检索未发现 PCL 实现 `NvOptimusEnablement` 或 `AmdPowerXpressRequestHighPerformance`。Microsoft Build of OpenJDK 的部分版本包含这两个驱动提示，但不能据此认为所有 Java 都会天然使用独显；三服当前采用 Temurin。参考 [Microsoft OpenJDK 发布记录](https://learn.microsoft.com/en-us/java/openjdk/release-notes#openjdk-2500)。

## Windows 与驱动边界

[Microsoft 官方说明](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11)支持逐应用选择省电或高性能，并要求重新启动应用生效；这里采用的是逐应用 GPU 偏好，不是该文的 DirectX 窗口化游戏优化选项。[NVIDIA 官方说明](https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Setting_the_Preferred_Graphics_Processor.htm)指出 Windows 10 20H1 起，Windows 的逐应用 GPU 偏好优先于 NVIDIA 控制面板的对应设置。

## 验证范围

`tests/GraphicsPreference.Tests` 链接辅助实现，通过内存注册表验证字段保留、已有高性能偏好、三运行时隔离、精确恢复、外部改动保护、失败状态和中文路径；测试不读写真实显卡注册表，不启动 Minecraft。真实双显卡设备上的渲染器与帧率验证仍需另行验收。
