# 魔金大帅

Windows 四服统一客户端。React + TypeScript / WebView2 / .NET 10。自建账号服务使用 ASP.NET Core Identity + PostgreSQL。

**0.1.2-beta.14 已开放内测。** [下载 Windows 安装包](https://launcher-direct.boshan.uk:21708/launcher/0.1.2-beta.14/MojinDashuai-Setup-0.1.2-beta.14-x64.exe) · [使用说明](docs/BETA-0.1.2.md)。干净 Windows 验收按用户决定延后，仍保留未通过记录；本次不认定为完整稳定版交付。

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

四个游戏服务器保持现有离线认证。群服账号只验证启动器身份，不能阻止其他客户端使用同一昵称连接。

管理和部署步骤见 [运维文档](docs/OPERATIONS.md)。

beta.10 / beta.11 / beta.12 / beta.13 玩家重新打开启动器即可在登录前自动升级到 beta.14。启动器按文件差异更新；最小化按钮左侧常驻“检查更新”，有新版时变绿。设置页显示当前版本。QQ群 1105114550：[点击加入](https://qm.qq.com/q/Bfat8qcPvO)。

新增四服「虚空行者」格雷科技空岛，已通过统一端真实入服与自动分岛。大厅采用四张场景卡片；每服两条平级线路各自显示延迟，仅进入页面测一次，也可手动重测。

全部服务器首次下载安装含固定 Java 的完整客户端 ZIP，后续仅下载变化文件，统一使用 FRP 下载服务。二服必需模组已全部纳入完整包与自建对象源；三服修正高 DPI 下界面缩放上限。游戏内容有更新时，左侧显示橙色“需更新”，点击详情页“更新”，完成后再进入游戏。
