# 魔金大帅 0.1.2-beta.11

beta.11 使用新的版本号和更新序号 13，让 beta.10 在下次打开时检测并下载变化文件，校验后静默切换，不需要登录后手动检查。beta.7 首次升级仍使用完整更新包。

- 发现新版时，最小化按钮左侧显示绿色“更新”，登录页与大厅均可使用。点击查看下载进度、重试或重启更新。
- 更新失败保留当前版本；已经确认有新版时保留更新入口。游戏、下载或登录任务进行中时，不强制重启。
- 更新面板与设置共用状态，较晚返回的初始查询不会覆盖新的进度；重新检查时保持入口稳定。
- 保留 beta.10 的测速修复：进入页面测一次，之后仅手动重测；重测期间保留原读数。

[下载 Windows 安装包](https://launcher-direct.boshan.uk:21708/launcher/0.1.2-beta.11/MojinDashuai-Setup-0.1.2-beta.11-x64.exe)。[上一轮功能与修复](BETA-0.1.2-beta.10.md)全部保留。

发布和验收证据见 [beta.11 验收记录](../packs/beta11-acceptance.json)。使用无窗口测试和隔离安装目录，不关闭玩家现有启动器或游戏。干净 Windows、LittleSkin 实际游戏画面及真实双显卡渲染器仍保留未验收记录。

[GitHub Actions](https://github.com/M4rkzzz/mojin-dashuai/actions/runs/33969242133) 的前端、Windows 和 API 检查全部通过。beta.10 latency2 → beta.11 从公开 FRP 入口实测下载 1,154,963 字节，498 个文件全部校验；安装包与完整更新 ZIP 的公网回读 SHA256 匹配。
