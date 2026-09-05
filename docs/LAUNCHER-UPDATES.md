# 启动器更新

beta.8 起，启动器从 `https://launcher-direct.boshan.uk:21708/v1/launcher` 读取 ECDSA P-256 签名清单。未开放时返回 404；当前已发布版本以 `packs/beta-release.json` 为准。更新文件从同一入口的 SHA256 对象路径下载，不附带账号凭据，也不在玩家侧切换其他来源。

每次打开在返回账号信息或显示可操作的登录页面前检查一次更新，准备完成后自动静默切换，无需运行安装程序。更新暂存至 `%LOCALAPPDATA%/Boshan/Launcher/updates`，核对发布序号和每个文件的大小、SHA256。新版位于独立目录，复用当前版本及缓存中未变化的文件，只下载新增或变化文件；旧程序、账号数据、游戏实例与 Java 均不覆盖。

新清单保留 ZIP，供 beta.7 及更早版本完成首次升级；本版起使用 `differential` 标记和逐文件来源。旧桌面快捷方式的启动接管先完成健康握手，再在返回账号信息前执行检查，避免旧引导器重复启动导致漏检。已由父进程完成检查的新进程通过随机标记跳过重复检查。

新进程在本地前端开始调用原生接口后报告就绪，原进程核对进程号、随机标记和目录后才切换。45 秒未就绪或新进程提前退出，保留当前版本，并记录失败候选以防循环启动。手动“检查更新”可重试；游戏、下载、登录或目录迁移时不能重启更新。

## 制作与发布

完成 UI 构建与 Windows x64 自包含发布后执行：

```powershell
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll bundle-launcher artifacts/MojinDashuai 0.1.2-beta.8 8 https://launcher-direct.boshan.uk:21708 artifacts/launcher-release
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll sign-launcher artifacts/launcher-release/launcher-release.json PRIVATE_KEY_PATH artifacts/launcher-release/launcher.signed.json
python tools/publish-launcher-update.py artifacts/launcher-release
```

默认上传兼容 ZIP，并根据签名清单校验、存放独立文件对象和检查公网下载，不切换更新接口。用隔离目录检查实际下载安装：

```powershell
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll check-launcher-update artifacts/launcher-release/launcher.signed.json src/Launcher.Desktop/launcher.json .local/launcher-update-download-check
```

整体验收后追加 `--activate`。脚本先执行玩家版本发布检查，再核对签名、大小和 SHA256，备份旧清单，原子切换 `public/launcher.signed.json`。构建内容改变时同时提高版本号和发布序号，不覆盖不可变对象。私钥保留在本地非公开目录。

更新比较完整语义版本号：`beta.10` 高于 `beta.2`，正式版本高于同一数字版本的 beta，`+` 后的构建标记不参与排序。当前版本和较旧版本不会重复下载或启动；较新 beta 不会因旧的活动清单而降回较旧 beta。

GitHub Release 工作流生成 ZIP、文件清单和 SHA256，创建草稿；签名和激活由本地命令完成。回退用更高的版本号和递增序号重新发布已验证代码构建，不降低已接受序号。

## 验证范围

- .NET 测试覆盖无效签名、旧序号、同序号替换、路径穿越、缺失/重复/损坏文件、旧版保留及失败回退。
- Windows 辅助进程实际启动并完成握手；提前退出的候选被拒绝，旧版仍可启动。没有使用游戏或账号目录。
- 无窗口浏览器检查进度、就绪、重启阻塞和错误后重试。
- API 0.1.5 已部署。既有公开版本的哈希及下载结果见 `packs/launcher-update-acceptance.json`；beta.8 的最终公开下载和激活结果另见 `packs/beta8-acceptance.json`，未回填项不视为通过。
- 实际 beta.3 → beta.4 WPF 启动接管通过：旧进程 47416 正常退出，新进程 39004 打开窗口并报告就绪，随后正常关闭；原账号及游戏数据保留。此项使用真实发行构建和实际更新目录，不是辅助进程替身。设置中的“重启更新”按钮尚未人工点击，前端状态与命令测试已通过。

2026-09-05 用户明确同意先开放内测。发布命令使用 `--activate --beta`，读取 `packs/beta-authorization.json` 与逐服 beta 验收记录；稳定版默认门槛保持不变。玩家下载使用 `/launcher/0.1.2-beta.5/MojinDashuai-windows-x64.zip` 命名路径，内容与签名清单的哈希对象相同，已验证大小与可用性。

0.1.2-beta.5 仅更新品牌图标。发布序号为 5，公网 ZIP 完整下载及 495 个文件校验通过；本机已准备新版并更新桌面快捷方式，没有打开窗口。真实 WPF 接管记录仍对应 beta.3 → beta.4，本次未重复声称通过人工窗口测试。

0.1.2-beta.6 的玩家入口改为 Setup 安装包；签名 ZIP 仍用于内部更新，序号为 6，公网完整下载及 498 个文件校验通过。选服页使用各整合包原图标，直接展示两条平级线路的实测延迟，每 30 秒更新，以灰/绿/黄/红信号表示测速状态。安装与卸载流程见 [Windows 安装包](INSTALLER.md)。

0.1.2-beta.7 的发布序号为 7，公网 ZIP 完整下载及 498 个文件校验通过。更新接管传递原程序安装目录，兼容旧版本通过用户安装记录回查，避免默认 content 落入更新缓存。原生辅助进程已验证目录传递；本次本机通过 Setup 静默更新，没有开启 WPF 窗口，也未重复声称真人更新按钮验收通过。

beta.8 的逐文件复用、损坏修复、中断保留、取消后旧版可用、旧 ZIP 兼容及辅助进程检查标记传播已有专项测试。真实发布文件回读、安装包生命周期及激活必须记录本版实际结果，不能套用 beta.7 的哈希或测试包。
