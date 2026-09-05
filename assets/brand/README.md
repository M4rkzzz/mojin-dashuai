# 魔金大帅图标

`square-source.png` 是用户选定的黑底白字「帅」方块版原图。保留原有图形和黑白配色，不叠加文字、描边或服务器主题色。

运行 `npm --prefix ui run build:icon`，从同一原图生成：

- `ui/public/brand/logo.png`：启动器品牌、登录及首次目录设置、三服列表/卡片/详情和设置实例选择器。
- `ui/public/brand/favicon.png`：本地前端页面图标。
- `src/Launcher.Desktop/Assets/launcher.ico`：Windows 程序、任务栏和快捷方式，多尺寸 16–256 像素。
- `ui/public/brand/server-icon.png`：Minecraft 多人游戏列表，64×64 PNG。

三个整合包的游戏主菜单、场景背景和玩家皮肤头像保留。沉浸式标题栏左侧不增加重复品牌。

服务器图标通过 `python tools/deploy-server-icons.py` 部署，已有图标会先备份。此命令不会重启游戏服。`python tools/check-server-icons.py` 只读取六个线路入口的状态包，结果写入 `packs/server-icons.json`。运行中的服务可能保留启动时的图标缓存，实际显示以状态包验证为准。
