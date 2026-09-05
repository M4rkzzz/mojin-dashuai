# 四服虚空行者客户端发行

实例 `vw`，显示名“四服 · 虚空行者”，内容版本 `1.1.9.1-boshan-r1`、序号 1。Minecraft 1.7.10、Forge 10.13.4.1614、Windows x64 Temurin 8u504；默认内存 4096 MiB、最低 3072 MiB。河北 `gt.mc.boshan.uk` 与宿迁 `gt.bk.mc.boshan.uk` 是平级线路，DNS/SRV 依据 `packs/routes/vw-dns.json`。

服务端任务已冻结 `../_voidwayfarer4-deploy/client-overrides` 的 984 个文件，合计 121,432,215 字节。对应 ZIP 为 `VoidWayfarer-1.1.9.1-boshan-client-r1.zip`，82,533,359 字节，SHA256 `26bccd5a5dac7040df5aa75bc25e81b36c935bbdb00601280f71a3394cadd81e`。逐项校验依据同级 `client-manifest.json`，服务端与原客户端真实握手、树岛渲染等证据见 `../docs/handoff/四服虚空行者部署-2026-09-05.md`。

统一发行保留全部冻结文件原字节，包括原作者 license.TXT/changelog.TXT、falsepattern 五个 JAR 与四个校验 sidecar、客户端 GTNHLib 0.11.16 和 NEIIntegration 1.5.0。`boshan-islands`、`mcqq-chat-capture` 属于纯服务端，不在客户端中。未采集世界、测试日志、账号或玩家数据。

仅增加四项已经在一服使用的统一端适配：

- `mods/mojin-autoconnect-1.7.10-0.1.0.jar`
- `mods/CustomSkinLoader_ForgeLegacy-14.17.jar`
- `mods/CompatibilityLayerForCustomSkinLoader-ALPHA-11.jar`
- `CustomSkinLoader/CustomSkinLoader.json`

冻结源没有玩家地图模组；WorldEditCuiFe 是选区辅助。源中没有独立整合包图标。ServerUtilities 2.2.34 与 BetterQuesting 3.8.14 保持原样，未新增现代 FTB 模组。

## 构建与下载

使用 `python -X utf8 tools/prepare-vw-native.py --output artifacts/vw-r1-draft-NEW`。准备器核对固定输入 ZIP、完整目录、所有每文件 SHA256 和 sidecar；复用一服 725 个纯引擎文件，并将合并 Forge JSON 改为独立 `vw-1.7.10-forge-10.13.4.1614` 版本 ID，Windows 依赖下载描述符均固定到统一对象源。四服脚本、任务定义、GregTech/AE2 关键配置采用 managed 策略，个人默认设置使用 seed 策略。

准备器输出新原生输入、标准 mrpack、对象 ZIP 与报告。标准 mrpack 是其他启动器的导入产物；统一客户端首装直接下载完整客户端 ZIP。完整构建使用 `build-complete-client.py --initial-release`，初次版本和序号均保持上述值；默认离线读取对象。后续版本依旧逐文件差异更新。

本次标准包 `artifacts/vw-r1-draft-2/vw-1.1.9.1-boshan-r1.mrpack` 为 3,016,785 字节，SHA256 `cbe19ee83392cce48fc088bf90237ed21b398d05b432faa5394ef748c38c4563`。

完整包含 1,714 个清单文件与 `__runtime/runtime.zip`，共 1,715 个条目。大小 283,043,676 字节，SHA256 `f2e8a86487c546ab35de717d93473c2504edf1b447868fc1ce4ec84e9c1b3fbf`。124 私有路径：`/vol1/mc-client-hub/staging/complete-client-20260905/vw-r1-sequence-1/complete-client.zip`。

所有清单文件、Java 和完整包都使用 `https://launcher-direct.boshan.uk:21708/objects/sha256/<SHA256>`。本版没有 officialOnly 例外，没有客户端依赖下载指向作者源。完整包与缺少的对象已按内容哈希发布，回执 `.local/complete-publication/24e41ebfb1c94f50977d5adecef31b9e/receipt.txt`；该操作未签名、未激活 catalog、未触碰游戏服务。

## 验收边界

`packs/vw-source-audit.json` 记录来源和公开对象验证，`packs/acceptance/vw-1.json` 记录实际完成与延期项。发布准备清单位于 `artifacts/vw-release-preparation/vw-manifest.candidate.json`；它仅相较构包器产物增加验收文件引用。

部署任务先使用原冻结内容、Java 8u292、3584 MiB 通过验收。统一端随后使用完整包、Java 8u504、4096 MiB 和四项适配真实入服：Forge 握手成功、自动分配独立空岛、持久化初始 9 区块、打开物品栏，最后正常退出。服务端只读确认 TPS 20。记录见 `.local/vw-r1-install-qa/vw-saved-play.log` 和 `packs/acceptance/vw-1.json`。皮肤、任务和机器界面没有在本次游戏中逐项目测；干净 Windows 首装继续保留未验收记录。
