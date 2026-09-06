# 魔金大帅

Windows 四服统一客户端。React + TypeScript / WebView2 / .NET 10。自建账号服务使用 ASP.NET Core Identity + PostgreSQL。

**1.0.0 已发布。** [下载 Windows 安装包](https://launcher-direct.boshan.uk:21708/launcher/1.0.0/chinese-fix/MojinDashuai-Setup-1.0.0-x64.exe) · [本版更新](docs/1.0.0.md) · [使用说明](docs/BETA-0.1.2.md)。干净 Windows 首次安装验收按用户决定延后，保留未验收记录。

四服现为 **r4**：更新渲染组件，领地地图增加缩放、拖动和键位提示。先将启动器更新至 1.0，再点击四服“更新”；内容按差异下载，保留玩家设置和地图数据。

本版增加四服主题加载界面、总加载进度和统一客户端入服认证，修复连接时长时间未响应以及二服汉化资源包未启用的问题。

## 启动开发环境

需要 Node.js 22、.NET SDK 10。先执行 `npm ci` 和 `npm run build`（在 `ui` 目录），再执行 `dotnet build Boshan.slnx`。

前端预览：`npm run dev -- --port 18473`。浏览器预览不持有登录凭据，业务能力通过 Windows 原生桥接调用。

Windows 发行包：

```powershell
dotnet publish src/Launcher.Desktop/Launcher.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/MojinDashuai
```

游戏 Java 按选服下载：魔法金属 Java 8、亡者世界 Java 17、肉丸工艺 Cleanroom + Java 25、虚空行者 Java 8。肉丸工艺不提供其他 Java 或 Forge 回退方案。

## 项目目录

| 目录 | 内容 |
|---|---|
| ui | 本地前端 |
| src/Launcher.Desktop | WebView2、DPAPI、微软及群服账号、原生设置 |
| src/Launcher.Core | 签名清单、下载、事务恢复、Java 约束、选线和游戏启动 |
| src/Hub.Api | Identity、邀请码、会话、恢复和管理 CLI |
| src/Publisher | 签名、差异检查、线路检查和隔离测试工具 |
| deploy | 124 的 Compose、下载配置和数据库备份 |
| packs | 逐文件来源审计与固定 Java 来源 |
| tests | 核心与隔离账号验收 |

私钥、账号、恢复码、数据备份、个人游戏数据及第三方未授权文件不进入仓库或公开目录。

## 发布规则

发布清单使用 ECDSA P-256 和 SHA256。签名前必须提供与清单哈希对应的验收记录。稳定版必须通过干净 Windows；明确批准的 beta 可以只延后这一项，仍要求真实入服、全自动来源与正确 Java。先上传不可变文件与清单，最后切换目录；GitHub 的客户端发布流程只创建草稿。

四个游戏服务器保留现有角色与世界数据，由服务端在登录前核验统一客户端签发的一次性入服票据。玩家通过新版统一客户端启动游戏，继续使用原账号和游戏名。

管理和部署步骤见 [运维文档](docs/OPERATIONS.md)。

beta10 至 beta16 玩家重新打开启动器即可在登录前自动检测升级到 1.0.0。启动器按文件差异更新，从 beta16 实测下载约 2 MiB；最小化按钮左侧常驻“检查更新”文字，有新版时变绿。设置页显示当前版本。QQ群 1105114550：[点击加入](https://qm.qq.com/q/Bfat8qcPvO)。

新增四服「虚空行者」格雷科技空岛，已通过统一端真实入服与自动分岛。大厅采用四张场景卡片；每服两条平级线路各自显示延迟，仅进入页面测一次，也可手动重测。

全部服务器首次下载安装含固定 Java 的完整客户端 ZIP，后续仅下载变化文件，统一使用 FRP 下载服务。二服必需模组已全部纳入完整包与自建对象源；三服修正高 DPI 下界面缩放上限。游戏内容有更新时，左侧显示橙色“需更新”，点击详情页“更新”，完成后再进入游戏。
