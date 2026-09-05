# 三服标准分发

基线：2026-09-05。输入版本来自玩家提供的客户端和二服整合包清单，不能按名称替换成其他版本。初始启动器不包含这些内容或 Java。

## PCL 的实际导入流程

核对了 [PCL 官方 ModModpack.vb](https://github.com/Meloong-Git/PCL/blob/8f7686457443e790670ee22157d98eee8ca2e20c/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModModpack.vb)，固定提交 `8f7686457443e790670ee22157d98eee8ca2e20c`。只参考格式和行为，没有复制其实现。

- 根据压缩包根目录的 `manifest.json`、`modrinth.index.json`、`mmc-pack.json` 等文件识别格式。
- CurseForge 文件用 projectID/fileID 查元数据；Modrinth 文件从清单中的下载地址获取。版本由清单固定。
- 分别安装 Minecraft、加载器和必需文件，然后复制 overrides；Modrinth 还支持 client-overrides。
- CurseForge 的 ZIP 资源通过内部文件识别为资源包、光影或地图，不能全部塞进 mods。
- PCL 的 MMC 导入分支只处理已知组件，并不导入 Cleanroom 的自定义 patches。官方 Cleanroom 模板虽然沿用 `net.minecraftforge` UID，其实际版本是 `0.5.17-alpha`，不能将其当作 Forge 版本交给 PCL 安装。

## 分发形式

| 服 | 标准包 | 加载器与 Java |
|---|---|---|
| 魔法金属 | Modrinth `.mrpack` | Forge 10.13.4.1614，Java 8 |
| 亡者世界 | Modrinth `.mrpack` | Forge 47.4.0，Java 17 |
| 肉丸工艺 | 官方 Cleanroom MultiMC/Prism 实例 ZIP + packwiz 内容清单 | Cleanroom 0.5.17-alpha，Java 25 |

一、二服使用 [Modrinth 标准](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)，PCL 的源码支持这种输入形式，但尚未通过真实 PCL 导入和入服验收。自建 HTTPS 下载地址是该格式允许的实现方式，不代表包可直接上传 Modrinth 平台（平台有额外域名限制）。

三服采用 [Cleanroom 官方实例](https://cleanroommc.com/wiki/end-user-guide/installation/install-client) 的补丁和 [packwiz 官方推荐的 MultiMC 分发方式](https://packwiz.infra.link/tutorials/installing/packwiz-installer/)。保留补丁，Java 兼容范围收紧为 25，固定 bootstrap 0.0.3 和 installer 0.5.14，关闭安装工具自动升级和图形弹窗。PCL 不属于该包支持的导入器。

所有服同时生成 packwiz 源目录和独立的 `*-content.json` 供发布过程读取。这个文件是构建输入，**不是可直接签名发布的 Launcher.Core PackManifest**。原生准备工具现已合并原版资源、加载器库、启动配置、运行时展开路径和配置策略，输出到 `artifacts/native/`；仍须完成真实游戏验收才能签名发布。

Java 清单与内容关联但不嵌入标准压缩包。魔金大帅负责按所选服务器自动安装固定 Java；通过外部启动器导入标准包时，Java 配置能力取决于外部启动器。三服没有 Java 8 启动器、Relauncher 或回退入口。

## 原站与单文件备用源

原站下载失败时可使用 `https://launcher.boshan.uk/objects/<sha256>.jar`。同一对象也可经现有 FRP 下载，账号和文件请求分离。Cloudflare 的路径路由将 objects/distributions 交给只读 downloads 容器，其余 API 路由保持原样；没有重启游戏容器。

按用户要求，没有可用原站的文件自动使用所提供客户端中的原文件，经 `build-standard-packs.py --publish-missing` 批量上传自建源。发布时核对大小和 SHA256，服务器落盘校验后只做公开地址的可用性检查，不再逐个重复完整下载。自动保留包内现有声明，没有声明也不会阻塞上传。来源记录如实标记为用户提供的客户端基线。

`publish-fallback-file.py INSTANCE PATH --file LOCAL_FILE --publish` 也可单独补一个文件，`--basis` 和 `--notice` 仅用于补充已有信息，不是必填项。原站能下载的文件无需重新托管；所有来源使用同一个固定文件哈希，不按名称替换模组。原生 Downloader 在文件损坏或来源失败后尝试后续来源，请求不带账号授权头或 Cookie。

## 构建与检查

在仓库根目录执行：

```powershell
python tools/prepare-format-tools.py
python tools/build-standard-packs.py --publish-missing
```

输入配置为 `packs/distributions.json`，输入文件清单为三个 `*-source-audit.json`。命令先补齐单文件自建源，再生成标准压缩包、内容清单、报告及 public/distributions 目录，写入忽略目录 `artifacts/distributions/`。该步骤只发布缺少来源的单文件；整包和 packwiz 目录通过下列独立发布命令上传，不会切换正式 catalog。每次构建自动刷新 `packs/standard-distribution-status.json`，避免已补齐的文件仍显示缺失。

```powershell
python tools/prepare-engine-profiles.py
python tools/build-game-integration.py
python tools/prepare-native-content.py
python tools/publish-content-files.py --standard --native
python tools/check-native-install.py
```

原生依赖来自 CmlLib 实际文件提取器；一服的合并 MSE 配置消除被后续 Forge 依赖覆盖的同名旧库。Java 下载与发布单独固定版本，玩家安装时由原生运行时管理器自动解压。首次安装使用压缩 overrides 批量填充校验缓存，后续更新继续按变化文件下载。发布路径不可变，已有同路径不同内容会失败；改变标准包内容时递增版本。2026-09-05 已成功公开 35,719 个标准分发文件和 266 个原生依赖文件，见 `packs/content-publication.json`；正式 catalog 尚未切换。

安装检查使用隔离 `.local` 目录和已校验源缓存，实际执行原生安装器与启动参数构造，但不冒充干净网络下载、干净 Windows 或真实入服验收。`Publisher play-check MANIFEST ROOT ROUTE_DOMAIN` 可使用独立测试名启动游戏，输出进程记录和日志；用户观察游戏界面，工具不执行电脑 UI 操作。

`play-check` 始终使用 `Mojin_QA`，应在用户观察前明确告知，不能用它核对旧角色。需要验证已登录玩家时，使用 `NativeAccountSmoke --play-saved-account MANIFEST ROOT ROUTE EXPECTED_GAME_NAME`；它从原生加密会话恢复登录，严格核对游戏名，不允许通过改昵称冒充当前账号，也不迁移服务端存档。输出日志会遮盖已知游戏令牌。

一服原生清单额外包含启动器自有的微型连接组件，在游戏初始化完成后再自动入服，解决直接传入 `--server` 引发的首次握手超时。标准外部启动器导入包仍使用原有内容，不依赖该连接组件，见 `src/GameIntegration/README.md`。

不带 `--publish-missing` 可仅做本地构建，缺少可用地址时会提示补传。`--draft` 保留给本地审查，不作为玩家包。实际缺少原文件或文件损坏才需要补齐，缺少发布出处不会卡住流程。

原站查找工具：`resolve-curseforge-sources.py` 通过公开 MCIM 缓存解析固定 ID；`detect-curseforge-files.py` 用指纹查找后再核对 SHA1/大小；`resolve-github-sources.py` 查相同版本发行资产；`verify-pack-downloads.py` 完整下载、校验并记录真正可用的地址。MCIM 不被转售、包装或反向代理成我们的 API。

只包含明确列入配置的整合包目录。玩家存档、地图、账号记录、FRP、截图、日志不进入包。服务器列表重新生成，两个线路使用同级名称；options.txt 重新生成最小初始化值。三服去掉已经停用的 induction_electrolyzer 注册项；旧 Forge 的 mods/1.12.2 下载缓存和 mods/memory_repo 历史提取缓存不作为 Cleanroom 包的输入。干净安装是否完整生成所需提取库仍属于最终游戏验收。

## 验证边界

`portable-installer-acceptance.json` 记录真实、固定版本的 packwiz 在隔离目录中读取生成格式、下载自建对象、复制配置、第二次安装保留玩家设置的无界面测试。它不代表 377 模组完成干净入服。

格式测试覆盖 Windows 路径、重复路径、带凭据地址、配置中的凭据、Java 版本限制、PCL/Modrinth 格式边界、packwiz 哈希和玩家文件保留。原生 Publisher 新增 `fetch CONTENT_FILE CACHE`，用同一个 Downloader 检验失效来源后切换自建源，缓存限制在 .local 下。

一服 117 个文件已具备下载源，其中 99 个原站、18 个自建；二服 317 个、三服 377 个都有可用原站。三服另有一个已验证的自建备用文件。三个标准包均已生成，当前没有缺少下载地址的模组。

构建报告的 releaseReady 仍为 false，原因是实际游戏和干净环境验收未完成，与发布出处无关。
