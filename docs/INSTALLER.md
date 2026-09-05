# Windows 安装包

玩家使用 `MojinDashuai-Setup-<版本>-x64.exe`。安装程序为简体中文深色界面，使用统一「帅」图标，默认安装到当前用户的 `%LOCALAPPDATA%/Programs/魔金大帅`，不请求管理员权限。可更改启动器目录，自动创建开始菜单快捷方式，并默认选中桌面快捷方式。

第一次登录后确认游戏文件位置，默认使用实际启动器安装目录下的 `content`。已有用户确认过的位置保持不变；从更新缓存启动时也沿用原程序安装目录。安装包包括启动器和 .NET 运行环境，不包括三个整合包或 Java；WebView2 由启动器已有逻辑检测和准备。账号、已选游戏目录和按需下载方式保持原有行为。

再次运行较新安装包会沿用安装位置并覆盖程序文件。卸载只删除安装程序登记的文件、快捷方式和卸载项；游戏文件、账号数据及启动器更新缓存保留。启动器自身更新使用签名文件清单，只下载变化文件；beta.7 及更早版本首次升级仍使用兼容 ZIP。玩家首次下载使用 Setup 安装包。

## 构建与发布

使用 [Inno Setup](https://jrsoftware.org/isdl.php) 6.7.3。编译器下载地址和 SHA256 固定在 `tools/ensure-inno.ps1`，执行前检查 Windows 发布者签名。安装脚本为 `installer/MojinDashuai.iss`；简体中文翻译取自 [Inno Setup 官方翻译](https://jrsoftware.org/files/istrans/)，原有署名保留。

```powershell
$compiler = ./tools/ensure-inno.ps1
./tools/build-installer.ps1 -Version 0.1.2-beta.10 -Source artifacts/launcher-0.1.2-beta.10-release -Output artifacts/installer-0.1.2-beta.10 -Compiler $compiler
./tools/check-installer.ps1 -Version 0.1.2-beta.10 -Source artifacts/launcher-0.1.2-beta.10-release -Compiler $compiler
python tools/publish-installer.py artifacts/installer-0.1.2-beta.10
```

测试使用独立 AppId，实际静默安装、覆盖升级、卸载，检查中文及带空格路径、两个快捷方式、当前用户卸载注册和安装目录内外的玩家文件保留。测试不会打开启动器或游戏。GitHub 构建及草稿发布工作流均包含安装包构建和测试。

上传按哈希保存不可变文件，并从玩家下载地址完整读回校验。`installer.json`、`publication.json` 与 `packs/installer-acceptance.json` 保存本次证据。新版本必须使用新文件名，不覆盖已发布 EXE。

当前 beta 安装包没有 Windows 代码签名；不能将内部更新清单的 ECDSA 签名当成 EXE 的 Authenticode 签名。干净 Windows 首次使用依然未验收，沿用用户已批准的 beta 延后记录。

2026-09-05：[GitHub 验证 33961230776](https://github.com/M4rkzzz/mojin-dashuai/actions/runs/33961230776) 的 UI、API 和 Windows 检查全部通过，包含实际静默安装、覆盖升级、卸载、快捷方式、中文及空格路径、玩家文件保留。此前失败来自测试脚本的 WScript 接口无法读取当前 ANSI 代码页以外的快捷方式文件名；改用 `IShellLinkW` 后在本机及 GitHub Windows 均通过。此修正仅涉及测试，已发布 beta.6 安装包及其哈希不变。

beta.7 最终载荷已完成真实静默安装、beta.6 覆盖升级及卸载测试；[GitHub 验证](https://github.com/M4rkzzz/mojin-dashuai/actions/runs/33962904778) 的 UI、API、Windows 全部通过。本机已静默安装到 `D:/魔金大帅`，495 个非调试文件与发布清单一致，已有 `D:/魔金大帅/content` 设置保留，没有打开窗口。中文和带空格 content 路径的配套 Java 8/17/25、加载器和本地库专项检查见 `packs/content-path-acceptance.json`。

0.1.2-beta.10 最终载荷已完成中文及空格路径下静默安装、从 beta.7 升级和卸载，玩家文件保留；[本轮 CI](https://github.com/M4rkzzz/mojin-dashuai/actions/runs/33967597197) 同样通过。本机正在运行的旧启动器未替换，玩家重新打开后按更新流程升级。
