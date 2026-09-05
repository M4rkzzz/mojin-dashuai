# 启动器更新

启动器从 `https://launcher.boshan.uk/v1/launcher` 读取 ECDSA P-256 签名清单。未正式开放时返回 404。ZIP 通过独立下载源读取，不附带账号凭据。

更新暂存至 `%LOCALAPPDATA%/Boshan/Launcher/updates`，核对发布序号、ZIP 大小、SHA256 和每个解压文件。新版位于独立目录，旧程序、账号数据、游戏实例与 Java 均不覆盖。设置中可点击“重启更新”；下次打开旧快捷方式也会尝试准备好的版本。

新进程在本地前端开始调用原生接口后报告就绪，原进程核对进程号、随机标记和目录后才切换。45 秒未就绪或新进程提前退出，保留当前版本，并记录失败候选以防循环启动。手动“检查更新”可重试；游戏、下载、登录或目录迁移时不能重启更新。

## 制作与发布

完成 UI 构建与 Windows x64 自包含发布后执行：

```powershell
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll bundle-launcher artifacts/MojinDashuai 0.1.2-beta.2 2 http://103.40.14.100:21708 artifacts/launcher-release
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll sign-launcher artifacts/launcher-release/launcher-release.json PRIVATE_KEY_PATH artifacts/launcher-release/launcher.signed.json
python tools/publish-launcher-update.py artifacts/launcher-release
```

默认只上传不可变 ZIP 并检查公网下载，不切换更新接口。用隔离目录检查实际下载安装：

```powershell
dotnet src/Publisher/bin/Release/net10.0/Publisher.dll check-launcher-update artifacts/launcher-release/launcher.signed.json src/Launcher.Desktop/launcher.json .local/launcher-update-download-check
```

整体验收后追加 `--activate`。脚本先执行玩家版本发布检查，再核对签名、大小和 SHA256，备份旧清单，原子切换 `public/launcher.signed.json`。内容改变时增加序号，不覆盖不可变对象。私钥保留在本地非公开目录。

GitHub Release 工作流生成 ZIP、文件清单和 SHA256，创建草稿；签名和激活由本地命令完成。回退用新的递增序号重新发布已验证构建，不降低已接受序号。

## 验证范围

- .NET 测试覆盖无效签名、旧序号、同序号替换、路径穿越、缺失/重复/损坏文件、旧版保留及失败回退。
- Windows 辅助进程实际启动并完成握手；提前退出的候选被拒绝，旧版仍可启动。没有使用游戏或账号目录。
- 无窗口浏览器检查进度、就绪、重启阻塞和错误后重试。
- API 0.1.4 已部署；候选 ZIP 已上传，更新接口未激活。哈希及下载结果见 `packs/launcher-update-acceptance.json`。
- 实际启动器窗口之间的重启尚未由用户验收，辅助进程测试不替代该结果。
