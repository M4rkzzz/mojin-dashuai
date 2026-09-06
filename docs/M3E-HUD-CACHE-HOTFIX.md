# 一服 HUD 缓存修复

内容版本：`8.0.4-2-mojin.3`，清单序号 3。统一启动器本体仍为 1.0.1。

针对 Angelica 2.0.0-alpha19 与 Xaero Minimap 21.10.41 的兼容问题，分发版 `config/angelica-modules.cfg` 关闭两项实验功能：

```properties
B:enableHudCaching=false
B:enableHudCachingEventTransformer=false
```

模板保存在 `packs/overrides/m3e/config/angelica-modules.cfg`，相对原始分发配置只修改上述两个值。`packs/distributions.json` 的 `configOverrides` 同时固定后续重建的配置来源和更新策略，避免重新打包时带回旧值。

此文件原先为 `seed`，已有文件不会随更新替换。本次改为 `managed`，使老玩家也能收到修复。完整配置文件按分发版本维护；玩家自行修改的其他 Angelica 模块选项会恢复为分发默认值，更新器将原文件备份到实例的 `.hub/transactions/<事务编号>/backup/config/angelica-modules.cfg`，修改过的原文件另保存在 `.hub/disabled/<事务编号>/config/angelica-modules.cfg`。其他配置文件、游戏设置、Xaero 地图和标点不受此补丁影响。

首装完整包已重建，后续更新只需下载 6,839 字节的该配置文件。模组 JAR、Java 和加载器保持原版本；无需重启服务器或升级统一启动器本体。玩家退出游戏后，在一服详情页更新内容并重新进入。

已验证完整包 4,781 个条目的哈希及清单一致性、实际序号 2→3 增量更新、旧配置备份、设置与地图文件保留，以及再次检查没有待更新文件。游戏入服验收沿用未变化的序号 2 二进制，不声称本次重新复现过崩溃或启动游戏验收。

[Angelica #542](https://github.com/GTNewHorizons/Angelica/issues/542) 记录了旧版本同类 HUD 缓存崩溃及关闭后恢复的情况；本次 alpha19 的具体处理来自维护者提供的故障定位。
