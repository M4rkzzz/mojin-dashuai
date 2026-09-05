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

所有服同时生成 packwiz 源目录和独立的 `*-content.json` 供发布过程读取。这个文件是构建输入，**不是可直接签名发布的 Launcher.Core PackManifest**：原版资源、加载器库、启动配置、运行时展开路径、配置管理策略和验收记录仍须由原生发布流程补全。标准包不会绕过现有签名发布门槛。

Java 清单与内容关联但不嵌入标准压缩包。魔金大帅负责按所选服务器自动安装固定 Java；通过外部启动器导入标准包时，Java 配置能力取决于外部启动器。三服没有 Java 8 启动器、Relauncher 或回退入口。

## 原站与单文件备用源

原站下载失败时可使用 `https://launcher.boshan.uk/objects/<sha256>.jar`。同一对象也可经现有 FRP 下载，账号和文件请求分离。Cloudflare 的路径路由将 objects/distributions 交给只读 downloads 容器，其余 API 路由保持原样；没有重启游戏容器。

`publish-fallback-file.py` 一次只接收一个已在来源审计中固定的文件；验证大小、SHA1、SHA256，要求具体的分发依据和随附许可文字，以不可变哈希命名上传。公开端再核对完整内容及 Range 206，成功后才登记可用备用地址。文件损坏或源失败由原生 Downloader 尝试后续来源，所有请求不带账号授权头或 Cookie。

不能把“本地存在”写成“已获作者许可”，也不能悄悄替换修改过的老版模组。原站能下载的文件无需重新托管。GitHub、CurseForge CDN、Modrinth 各来源都必须匹配原来的文件哈希。

## 构建与检查

在仓库根目录执行：

```powershell
python tools/prepare-format-tools.py
python -m unittest discover -s tests -p 'test_*.py'
python tools/build-standard-packs.py
```

输入配置为 `packs/distributions.json`，输入来源审计为三个 `*-source-audit.json`。输出写入忽略目录 `artifacts/distributions/`：标准压缩包、内容清单、构建报告，以及待发布的 public/distributions 目录。该目录不会自动上传，不会切换正式 catalog。

来源未齐时默认阻止生成该服的候选包，并写明具体缺项。`--draft` 只用于本地审查，文件名带 draft；其中的预定备用地址可能尚未上传，不能交付玩家。缺少本地文件而无法计算必需哈希时，连不完整草稿也不会生成。

原站查找工具：`resolve-curseforge-sources.py` 通过公开 MCIM 缓存解析固定 ID；`detect-curseforge-files.py` 用指纹查找后再核对 SHA1/大小；`resolve-github-sources.py` 查相同版本发行资产；`verify-pack-downloads.py` 完整下载、校验并记录真正可用的地址。MCIM 不被转售、包装或反向代理成我们的 API。

只包含明确列入配置的整合包目录。玩家存档、地图、账号记录、FRP、截图、日志不进入包。服务器列表重新生成，两个线路使用同级名称；options.txt 重新生成最小初始化值。三服去掉已经停用的 induction_electrolyzer 注册项；旧 Forge 的 mods/1.12.2 下载缓存和 mods/memory_repo 历史提取缓存不作为 Cleanroom 包的输入。干净安装是否完整生成所需提取库仍属于最终游戏验收。

## 验证边界

`portable-installer-acceptance.json` 记录真实、固定版本的 packwiz 在隔离目录中读取生成格式、下载自建对象、复制配置、第二次安装保留玩家设置的无界面测试。它不代表 377 模组完成干净入服。

格式测试覆盖 Windows 路径、重复路径、带凭据地址、配置中的凭据、Java 版本限制、PCL/Modrinth 格式边界、packwiz 哈希和玩家文件保留。原生 Publisher 新增 `fetch CONTENT_FILE CACHE`，用同一个 Downloader 检验失效来源后切换自建源，缓存限制在 .local 下。

标准结构检查、来源检查、分发依据和实际游戏验收分开记录。所有构建报告的 releaseReady 仍为 false；没有伪造正式验收或发布 beta。
