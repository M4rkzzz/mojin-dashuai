# 四服虚空行者接入交接

四服已完成服务端验收和统一客户端真实入服，随 beta13 发布。详细内容来源见 [VW-DISTRIBUTION.md](VW-DISTRIBUTION.md)。

- 固定实例 `vw`，Void Wayfarer 1.1.9.1-boshan-r1，Minecraft 1.7.10 / Forge 10.13.4.1614 / Temurin 8u504 x64，默认 4096 MiB。
- 河北阿里云 `gt.mc.boshan.uk` 与宿迁三网 `gt.bk.mc.boshan.uk` 平级，按 Minecraft SRV 解析。
- 最终源客户端 984 文件 / 121432215 字节，源 ZIP SHA256：`26bccd5a5dac7040df5aa75bc25e81b36c935bbdb00601280f71a3394cadd81e`。完整保留 falsepattern 的 5 个 JAR 与 4 个校验旁文件。
- 额外接入与一服相同的自动入服和皮肤适配，共 4 文件；服务端 boshan-islands、mcqq-capture 不进入客户端。
- 首次完整下载含固定 Java，283043676 字节；文件更新继续使用同一 FRP 下载服务。
- 空缓存首次安装通过，984 份冻结内容逐文件一致；统一端真实登录、自动建岛、初始 9 区块认领和正常退出已通过。实际打开了物品栏，任务/机器/皮肤未单独进行本次视觉确认。
- 四服服务器任务只读确认了岛屿与领地持久化，TPS 20；已产生真实玩家岛，禁止当成测试世界清理。
- 大厅新增格雷科技空岛美术，原整合包主菜单保留。

验收记录：[vw-1.json](../packs/acceptance/vw-1.json)。干净 Windows 首装仍按已批准的内测范围保留未验收记录。
