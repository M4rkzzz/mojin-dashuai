# 四服入服认证部署

本文件中的命令在 NAS 宿主通过 sudo 运行。脚本本身不设置定时任务；15:30 前只允许准备。最终顺序固定为：**API 激活 → 四服 observe 重启且健康 → 发布 1.0 更新与目录 → 真实正负连接验证 → 逐服 enforce**。

用户随后明确授权“二服现在就可以重启了”，取消此前 15:50 等待。dc2 的独立硬限制与暂存计划已同步为本次授权执行时刻 **15:43:23**，其他服仍为 15:30；顺序为一、三、四服后再部署二服。

## 准备

1. `activate-join-api.py --prepare` 生成私有四服 key，位置固定为 `/var/apps/mc-client-hub/staging/join-auth-1.0.0/server-keys.json`，权限 0600。
2. `activate-game-join.py --prepare /absolute/path/to/final-agent.jar`：固定该 JAR SHA256，读取四服当前启动脚本，核对本次只读审计的原始哈希，生成候选脚本、配置和 hold 记录。只写私有 staging，不写游戏生产目录、不改网络、不重启。
3. 候选初始模式是 `observe`，加载认证代理但暂不拦截无票据用户。每个候选脚本会经过 `bash -n`。
4. Java 8 IPC 修正版完成后，必须使用负责人确认的最终 JAR 重跑 `--prepare`。只有四服都未激活、生产原脚本全部未漂移时才能整体更换暂存 JAR 与清单哈希；任何一服已进入激活流程都会在改动第一个候选前拒绝。不要将早前 `2218bf2c27e5f340f9f45247ce6a4b63fbc12ad1c56cd60137437477108e1cdf` 候选当成最终包。

脚本只在实际启动游戏的 Java 命令插入两个参数，保留所有其他字节和参数，尤其内存。一、二服分别为 `run.sh`，三服为 `ServerStart.sh`（外层 `start.sh` 校验不修改），四服为 `start.sh`。不全局设置 JAVA_TOOL_OPTIONS，避免污染一服 Java 版本检测或三服的探测/安装器进程。

## 北京时间 2026-09-06 15:30 后

1. 先由 `activate-join-api.py --activate` 更新 Hub API、连接既有 edge 网络并配置真实游戏容器 IP 的 /32 白名单。
2. 逐服执行 `activate-game-join.py --activate m3e`，再对 `mb`、`vw` 执行相同命令；`dc2` 按用户最新即时授权部署。
3. 每服先核对 API 的真实 `state.json`、实时 API image/env、私钥、网络 IP 和容器内健康请求；备份启动脚本与原 `.join-auth`；RCON `save-all` / `stop`；精确等待该目录 Java 退出，关闭闲置 GSManager terminal，再通过 GSManager start。
4. 新 Java 必须带固定 agent 参数，PID 与启动 ticks 不变且 RCON 已就绪，才记录 `active / observe`。不会杀死 Java，也不会修改世界、模组、内存或其他服务器。
5. **四服全部健康并保持 observe 后**，发布负责人再激活 1.0.0 启动器安装包、自动更新清单和游戏目录。新目录的最低启动器版本须包含四服 r4 迁移及入服票据支持；发布内容必须与最终已测试的 agent JAR 一致。
6. 使用实际发布候选完成真实正向连接及无票据负向连接。正向验证须确认票据被核销且玩家正常进入游戏；负向验证须确认无票据确实在登录前被拒绝。observe 只记录而不拒绝，不能当作负向拦截已通过；受控负向测试需短暂切 enforce，完成后可恢复 observe，避免把尚未完成整体验收的服写成已正式强制认证。
7. 全部验收通过后逐服执行 `activate-game-join.py --mode vw enforce` 等命令开启正式拦截。也支持 `off`、`observe`。仅更改 mode 属性、保留其余配置及私钥，0600 和原 owner 保持，mtime 保证变化供 Java 热重载；不再重启。任一服未通过即保留该服 observe 并记录原因，不将 mode 写成 enforce 冒充完成。

`active` 表示代理已随服务器启动；`mode=observe` 不能当成拦截已启用。脚本不替代真实票据/无票据连接验收。

## 15:30 后游戏部署操作摘要

前提：负责人已确认最终 agent SHA256，并用该文件重新生成四服 hold 计划；API 激活命令成功返回 `active`。操作人只接管以下逐服步骤，不重新发布 API、不替负责人提前激活目录。

```sh
python3 /tmp/mojin-activate-game-join.py --status
python3 /tmp/mojin-activate-game-join.py --activate m3e
python3 /tmp/mojin-activate-game-join.py --activate mb
python3 /tmp/mojin-activate-game-join.py --activate vw
# 二服已获用户即时授权，独立限制为北京时间 15:43:23
python3 /tmp/mojin-activate-game-join.py --activate dc2
python3 /tmp/mojin-activate-game-join.py --status
```

上一个服明确返回 `phase=active / mode=observe` 才继续下一个服；每条激活命令内部已经包含正常保存、停服、启动和健康等待，不需要另发 stop 或重复 start。出现失败先读该服状态，停止推进下一服；需要恢复时单独执行 `--rollback <id>`。

四服完成后向发布负责人交回四服 PID/启动 ticks、最终 agent SHA256、observe 状态和健康结果。待负责人完成 1.0 发布及正负连接验收，再按其确认逐服执行 `--mode <id> enforce`。本步骤不创建定时任务，也不在等待期间重启游戏。

## 状态与回滚

二服已使用服务端修正版完成实际入服与生产票据核销，维护已解除。当前四服均 healthy/enforce：一、四服为 d870 代理，二、三服为 acd591 服务端专用修正版。最终热切换和四服明确无票据拒绝均已通过，游戏 PID 未变。首次备份和旧候选保留；不要误用下列历史替换命令重复重启已验收目标。

单服替换服务端 agent 使用以下独立命令，避免全体 prepare 迫使一、四服重复重启：

```sh
python3 /tmp/mojin-activate-game-join.py --prepare-server dc2 /absolute/fixed-server-agent.jar
python3 /tmp/mojin-activate-game-join.py --activate-server dc2
python3 /tmp/mojin-activate-game-join.py --prepare-server mb /absolute/fixed-server-agent.jar
python3 /tmp/mojin-activate-game-join.py --activate-server mb
```

`--prepare-server` 只写目标服的 pending 候选与计划，当前生产、当前候选和首次 backup 均保持；支持健康 active 或已恢复原脚本的 maintenance/rolled-back 状态。`--activate-server` 再次核对当前代的精确进程、脚本和配置哈希，正常保存停止目标服；维护中的二服直接从停止状态安装并启动。首次 backup 永不覆盖，旧候选移入 generations 记录，只有目标服写入新 agent，初始仍为 observe。其他三个服不重启，客户端 agent 不受这些命令影响。

- `activate-game-join.py --status` 输出无私钥状态，包括当前 mode、PID/启动 ticks 与阶段。
- 任何阶段中断会保留 staging 状态；不会静默覆盖独立修改的原脚本，也不会启动第二份 Java。
- `activate-game-join.py --rollback vw` 等单服命令：通过 RCON 正常停止，恢复精确原脚本与原 `.join-auth`，再由 GSManager 正常启动。失败候选 `.join-auth` 改名保留供诊断，原备份不删除。其他服回滚不需要等 15:30；二服所有重启包括回滚遵循其单独的最新授权时间。
- API 由独立脚本管理；本脚本不会擅自连接/断开 Docker 网络或更新 API。

本地测试命令：`python -X utf8 -m unittest discover -s tests -p test_activate_game_join.py -v`。12 项通过，包括独立的二服时间限制、排除 Java 版本探测进程、排除闲置交互 shell（保留真实启动脚本互斥）、单服准备不改当前代和首次备份、源代漂移拒绝、维护态单服启动；仅使用临时目录和模拟 Docker/RCON 调用，不构成生产重启或入服验收。
