# 三、四服定时掉落物清理

仅在服务端安装官方原版 JAR，不要求玩家更新，不更换服务端核心。

| 服 | 模组 | 官方文件 | 配置 |
|---|---|---|---|
| 三服 Cleanroom 1.12.2 | Lag'B'Gon Revived 1.1.0 | https://www.curseforge.com/minecraft/mc-mods/lagbgon-revived/files/4691812 | lagbgonrevived.cfg |
| 四服 Forge 1.7.10 | Legacy ClearLag 1.1 | https://www.curseforge.com/minecraft/mc-mods/legacy-clearlag/files/6424369 | clearlag.cfg |

三服配置 `EntityInterval=9`，加上模组固定一分钟预告，约十分钟一轮。空的非物品实体白名单配合 `ToggleEntityBlacklist=false`，禁止删除生物、宠物、载具和其他非物品实体。关闭繁殖限制、每区块生物限制及低 TPS 自动卸载区块。仅清理已实际 tick 至少一分钟、没有实体自定义名称的掉落物。`EffectsOnSP=true` 是为了绕过上游把在线人数不超过一人当作单人的判断；这个 JAR 只安装在服务端。

四服配置十分钟一轮，提前五分钟和一分钟提醒，关闭数量阈值触发的额外清理。上游只处理 `EntityItem`，不处理生物、方块和容器内容；它不提供物品年龄或价值过滤，届时地面上的新掉落物也会清除。两个模组的提醒目前使用上游英文。

不能保证刚死亡或刚丢下的物品在四服清理时受到保护。重要物品应保存在容器内；不要把方块、宠物或其他实体加进清理名单。

## 验证与部署

在不复制正式世界的整合包测试副本中检查加载、定时预告、清理动作和正常退出。四服确认定时清除一个测试掉落物；三服确认预告和定时清理运行，未满足实际 tick 年龄的物品保留。对其他实体不作处理由上游源码/字节码及配置核对确认，未把测试夹具中失败的生物/箱子断言算作通过。

生产部署逐服执行：RCON 确认为零、保存世界、再次确认零人后正常停止；备份旧配置，只加入对应 JAR 和配置，保留原启动脚本、认证和活动组件，然后检查 RCON 恢复。记录见 `packs/network-optimization/entity-cleanup.json`。

备份目录：NAS `/vol1/mc-client-hub/staging/entity-cleanup-20260907/{mb,vw}`。回退时同样先等待无人并正常停服，仅移走新增清理 JAR、恢复旧配置；不修改世界、背包、地图或其他模组。
