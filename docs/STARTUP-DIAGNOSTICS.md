# 启动无反应诊断包

基于 1.0.0 的独立支持包，首次构建为 `1.0.0-startup-debug.1`。不改正式自动更新入口，不包含 1.0.1 待办 #1 的加载内容提示实现。

玩家将整个 ZIP 解压到新目录，双击“启动诊断.cmd”，等待约 40 秒后，将桌面的 `Mojin-Startup-Diagnostics-*.zip` 发回。无需登录、下载游戏或安装 Java。若仍无界面，正常关闭本次主程序后运行“兼容模式诊断.cmd”，并提供第二份报告。“收集日志.cmd”重新导出最新一次运行。

普通诊断保留更新检查与原显示缓存，用于复现现有行为；兼容诊断跳过启动更新等待，使用 WPF 软件渲染、禁用 WebView2 GPU 加速及独立显示缓存。两者均阻止自动交接到未带诊断埋点的副本，缺少 WebView2 时提示而不自动安装。

## 覆盖范围

- 主程序启动前：Windows/进程架构、屏幕范围、WebView2 安装版本、已有同名进程、桌面快捷方式目标是否存在。
- .NET 宿主启动：宿主解析与运行时加载日志、启动返回值、标准输出和错误输出；相关 Windows 应用异常。
- 托管入口及界面：更新检查、主窗构建显示、设置读取、WebView2 环境和控制器、导航、页面 bootstrap。
- 后台心跳：UI 调度响应年龄、当前活动阶段和耗时，区分网络等待与同步卡顿。
- 异常：类型、HResult、无源文件路径的调用栈；不记录异常消息、账号对象或桥接参数。

报告按文件白名单导出并脱敏本机路径，不打包账号文件、设置、WebView2 用户资料、游戏目录或内存转储。兼容模式的新缓存也不进入报告。未开启诊断环境变量时，埋点不写日志、不改变正常启动行为。

## 构建及单独分发

```powershell
tools/build-startup-diagnostics.ps1 -BuildId 1.0.0-startup-debug.1
python -X utf8 tools/publish-startup-diagnostics.py artifacts/diagnostics/MojinDashuai-1.0.0-startup-debug.1-x64.zip
```

已有构建目录不能覆盖，后续使用新的诊断构建号。分发脚本只上传独立支持 ZIP，不签发或激活启动器/内容更新清单。

本地验证：Release 构建通过；普通、兼容两次真实启动均记录 `ui.bootstrap.completed`，收集器均生成 ZIP；日志脱敏及 UI 阻塞/网络等待区分检查通过。这不等于已复现远端玩家故障，需其报告定位。

## 2026-09-06：旧 Windows CET 启动失败

玩家返回 `Mojin-Startup-Diagnostics-20260906-165641-104969d9.zip`。系统为 Windows 10 21H2 / 19044.2728 x64，WebView2 152.0.4191.62 已安装；本次运行的是兼容诊断，进程约 0.7 秒发生异常，没有生成托管入口 `startup.jsonl`。

`stderr.txt` 明确报告：`Your Windows doesn't fully support CET. Please install all available Windows updates.` Windows 应用错误代码为 `0x80131506`；宿主日志已加载 `coreclr.dll`。由此定位为 .NET 运行时启动阶段的 CET 支持问题，尚未执行联网更新或 WPF/WebView2 初始化。用户路径已在报告中脱敏，原始报告不提交仓库。

按 [微软应用级 CET 兼容说明](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support)，用 `CETCompat=false` 构建专项诊断程序；此包仅对本应用选择不启用 CET 硬件栈保护，不修改 Windows 全局设置。诊断发布时正式 1.0 更新清单未变；玩家验证成功后，修复于 1.0.1 纳入正式安装包及自动更新包。

- 可用候选：`1.0.0-startup-debug.4`；基于 debug.1 的补丁 `1.0.0-startup-debug.4-patch`，123356 字节。
- 与原诊断包比较：500 个程序文件中仅启动 exe 有一个字节差异，PE 扩展 DLL 特征的 CET 位由 1 变 0，其他 DLL 特征及运行时文件未变。
- 原启动 exe SHA256：`027636c2936a809e75176d129634b7b0d9b06d03e9ab4b03070213dd73fa4b98`；补丁 exe：`bec2011fb8e491ed7a705ea4c47f2617ceb9fa6bf9f554191b0c936d80683193`。
- 本地正常启动与日志生成已通过；2026-09-06 玩家确认完整 `1.0.0-startup-debug.4` 包可以正常启动。正式版改动指引已列入 [1.0.1 待办 #4](NEXT-1.0.1.md#4-修复部分旧-windows-上双击启动器无反应)，已随 1.0.1 发布到正式更新入口（序号 20）。
- 构建注意：SDK 的 apphost 增量缓存不以 CetCompat 属性变化作为重建输入，构建工具须清除限定在本项目 obj 下的单个 apphost 缓存，并检查实际 PE 位；构建后也清理该缓存，防止后续正式构建复用特殊兼容标记。未通过实际位检查的中间候选不能分发。
