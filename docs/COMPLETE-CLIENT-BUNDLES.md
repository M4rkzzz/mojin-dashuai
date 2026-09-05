# 完整客户端候选包

`tools/build-complete-client.py` 从一份可信的 native manifest 和按 SHA256 固定的对象生成可审阅候选。它不签名、不上传、不改 catalog、不启动游戏，不读取玩家实例目录。Python 3.11 以上，无第三方依赖；与 `tools/pack_distribution.py` 一起使用。

首装包包含清单中可重分发的全部 `files`：模组、Minecraft、依赖库、资源、默认配置等，以及 `runtime.archive` 原始 ZIP。仅显式声明 `officialOnly:true` 的文件留给启动器从固定官方 CDN 自动下载；报告逐项列出这些例外及字节数，不能把含例外的包描述为“所有 JAR 都在 ZIP 内”。后续更新保留每个文件的原路径、SHA256、大小、来源及 policy，继续逐文件差异更新。

## 格式

候选 manifest 的 `bundles` 被替换为一个条目：

```json
{
  "archive": {
    "path": "complete-client.zip",
    "size": 123,
    "sha256": "<生成的 ZIP 的 SHA256>",
    "sources": ["https://launcher-direct.boshan.uk:21708/objects/sha256/<ZIP 的 SHA256>"],
    "policy": "managed",
    "distributionBasis": "Complete native client assembled from pinned manifest objects"
  },
  "prefix": "",
  "complete": true
}
```

ZIP 条目必须恰好为所有非 `officialOnly` 文件的原始相对路径，加固定的 `__runtime/runtime.zip`。不额外包含 overrides 目录、原配置小包、候选 manifest、报告、签名或账户数据。`files` 不能占用 `__runtime` 目录。运行时 ZIP 按 `runtime.archive` 校验后交给启动器运行时安装逻辑。构建器不会重写其中的 Java 内容。

`officialOnly` 默认 false。显式 true 的 `sources` 必须只有一条匿名 HTTPS 固定链接，其域名必须为 `cdn.modrinth.com`、`mediafilez.forgecdn.net` 或 `edge.forgecdn.net`。该条目保留在 manifest，不能进入 ZIP；运行时不能声明此例外。工具只验证例外元数据，不下载或声称已经验证其远端字节。

## 本地生成候选

输出必须为尚不存在的新目录；版本必须不同于输入版本，序号必须严格高于输入序号。下面仅示范一服候选，值应由维护者明确指定：

```powershell
python -X utf8 tools/build-complete-client.py `
  --manifest artifacts/native/m3e-manifest.json `
  --output artifacts/complete-client/m3e-candidate-2 `
  --version m3e-complete-candidate.2 --sequence 2 `
  --object-root .local/source-cache `
  --object-root .local/runtimes `
  --object-root .local/engines `
  --object-root artifacts/distributions `
  --inventory .local/single-origin-inventory.json `
  --download-object-base https://launcher-direct.boshan.uk:21708/objects/sha256 `
  --public-object-base https://launcher-direct.boshan.uk:21708/objects/sha256
```

`--object-root` 可重复。工具仅探测根目录下精确哈希文件名，以及 `sha256/`、`objects/`、`objects/sha256/` 的相同精确名称；支持无后缀、`.bin`、`.jar`、`.zip` 和 `.mrpack`。它不递归扫描。单独指定 `.local/engines` 不会发现其中的任意文件；此类保留源需通过 inventory 的精确路径引用。

可选 `--inventory` 接受 `audit-single-origin.py` 的输出，用 `objects[].localMatches` 和 `remotePaths` 定位同一 SHA256。仅使用显式允许根目录之内的文件或精确 ZIP 条目，重新计算哈希，不信任旧的 `sha256Verified` 字段。未命中的 inventory 条目不会读取，根目录外的路径不会打开。不要把整个用户目录、`.local` 或管理员账户目录当作对象根。

默认不下载。只有传入 `--download-object-base` 才会从 `BASE/{sha256}` 下载缺少对象，校验大小和 SHA256；不请求原站列表，不添加账户令牌。临时下载留在本次输出的临时目录内，完成或失败后移除。`--public-object-base` 仅填写候选 ZIP 的未来公开 URL，不会上传。

## 在已有公共对象的 124 服务器上生成

优先把两份 Python 脚本和经过确认的 native 输入放到独立工作目录，在 124 的已有公共对象旁生成候选，可避免将约数 GB 原始对象下载到管理员电脑再上传。以下命令只是维护操作模板，不由本次修改执行：

```bash
python3 -X utf8 build-complete-client.py \
  --manifest /work/input/m3e-manifest.json \
  --output /work/candidates/m3e-complete-2 \
  --version m3e-complete-candidate.2 --sequence 2 \
  --object-root /vol1/mc-client-hub/public/objects/sha256 \
  --public-object-base https://launcher-direct.boshan.uk:21708/objects/sha256
```

输出放在公共目录之外，不修改现有对象或正式清单。原始公共对象已经齐备时无需 `--inventory` 或下载选项。服务器应有容纳新 ZIP 和元数据的空间；启用缺项下载时另需下载对象的临时空间。

## 禁止重分发的文件

维护者可以传入 `--deny-redistribution 'mods/tombstone*.jar' --deny-redistribution 'mods/ftb-chunks*.jar'`，或提供策略文件：

```json
{
  "schema": 1,
  "blocked": [
    {"pathPattern": "mods/tombstone*.jar", "reason": "禁止公共镜像重分发；仅允许固定官方 CDN 下载"},
    {"pathPattern": "mods/ftb-chunks*.jar", "reason": "仅从已固定的官方 CDN 获取"},
    {"sha256": "<禁止重分发文件的完整 SHA256>", "reason": "维护者确认的许可限制"}
  ]
}
```

路径模式不区分大小写；SHA256 规则能识别改名后的同一文件。匹配到普通文件会使整次构建失败，只输出阻塞报告，不会删掉文件后生成缩减包。只有输入 manifest 已显式为该项设置 `officialOnly:true` 且官方地址符合约束，才允许把它作为报告中的官方例外。构建器本身不修改此字段、不添加新模组、不推断许可证，也不扩大对旧 FTB 清单的审计范围。二服新增 Corail 或 FTB Chunks 应先完成该文件版本的许可/来源确认，再生成候选。

## 结果与验证

成功输出 `complete-client.zip`、`manifest.candidate.json`、`bundle.json`、`report.json`。报告记录输入 SHA256、版本/序号、ZIP SHA256/大小、文件数、官方例外、来源计数和逐条校验结果；不记录本地账户信息、源目录枚举或密钥。ZIP 使用固定时间戳和条目顺序，可重复生成。

构建时逐条验证源大小和 SHA256，写完后再次遍历 ZIP，核验清单覆盖率、每条大小/哈希、无多余文件。拒绝路径穿越、Windows 设备名/别名、大小写重复、文件与目录冲突、链接来源和保留的玩家状态路径；文本配置中发现疑似非空凭据会阻塞且不回显内容。`options.txt`、`servers.dat` 只允许清单标记为 `seed` 的已固定默认文件。它不是任意玩家目录的清洗/匿名化工具；输入仍必须来自可信的发布准备流程。

候选保留输入的 `files` 与 `runtime` 内容，清空旧的 `validationEvidence`，避免沿用旧版验收。输入签名不由此工具验证；签名封包应先用发布工具验签取得 native payload。构建通过不代表游戏、官方例外下载、干净 Windows 或发布验收通过。候选之后仍需独立验收及既有签名/发布流程。

```powershell
python -X utf8 -m unittest discover -s tests -p test_complete_client_builder.py -v
```

测试全部使用临时合成数据；覆盖完整 ZIP 与 Runtime、稳定输出、不覆盖旧清单、缺项/损坏/路径攻击/私人文件阻塞、源范围、按哈希下载、重分发禁令和显式官方例外。不会下载真实模组、签名、上传或打开窗口。
