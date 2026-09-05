# 四服接入预留（未上线）

实例 ID 预留为 `vw`，整合包 Void Wayfarer 1.1.9.1。四服生产部署由独立任务维护，客户端目录由统一客户端任务维护。beta.13 仍仅展示已上线三服；本记录不代表四服已通过验收或可以发布。

- Minecraft 1.7.10 / Forge 10.13.4.1614，独立 Temurin 8u504 x64，预计内网端口 25504。
- 用户选择全新世界：flat，generatorOptions `2;0;1;`，使用 FTBI 建岛。
- 客户端以最终服务端实际配方覆盖为准；源包存在四个脚本差异、GTNHLib 和 NEIIntegration 版本差异，待服务端任务交付完整匹配目录。
- 平级线路拟用 `gt.mc.boshan.uk` 与 `gt.bk.mc.boshan.uk`（用户最终更正）。已配置河北 `47.92.219.134:51458`、宿迁 `103.40.14.100:58378` 的 A/SRV；两线 priority 0 / weight 1，DNS only。稳定实例 ID 仍为 `vw`。
- 待交接：客户端文件路径与 SHA256、精确 Java/加载器版本、匹配覆盖目录、建议内存、实际入服和核心界面结果、冻结客户端验收报告。

接入时沿用签名清单、完整客户端 ZIP 与固定 Java、文件差异更新、个人地图/设置保护及平级线路显示。只在验收交接完成后启用玩家选项。

## 岛屿功能交接更新（仍在验收）

所有玩家使用同一个虚空主世界，首次加入分配独立岛屿。服务端 `boshan-islands` 辅助模组提供 2000 格间距、持久化分配、FTB 队伍、3×3 初始领地与邀请共岛；该模组为 server-only，不直接加入客户端发行文件。

原包的 ServerUtilities 2.2.34（FTB Utilities / Library / Aurora 移植）和 BetterQuesting 保留。测试覆盖目录为 `D:/project/MCserver/_voidwayfarer4-deploy/client-overrides`，等待冻结文件清单与哈希后再采集；不采集测试日志、存档或玩家数据。

用户已收到为 127.0.0.1:25504 创建河北/宿迁两条 TCP 隧道的通知，两条公网入口已由用户提供并完成 DNS/SRV 配置，1.1.1.1 公共 DNS 解析一致，见 `packs/routes/vw-dns.json`。beta.13 已按用户要求暂停发布，将与四服一起交付；现有三服版开发安装包带 HOLD 标记，必须重建后才能发布。
