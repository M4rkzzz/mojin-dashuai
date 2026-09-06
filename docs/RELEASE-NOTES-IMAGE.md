# 图片版更新日志

生成器使用现有品牌图标和游戏场景，文字由 HTML/CSS 排版，再通过本地 Chromium 导出。无需图片生成服务或在线接口。

## 生成 beta16

在仓库根目录运行：

```powershell
node tools/render-release-notes.mjs --data packs/changelogs/beta16.json
```

输出位于 `artifacts/changelogs/`：

- `beta16.png`：2160 × 3840 的高清竖版图片，适合保存或上传原图。
- `beta16.jpg`：1080 × 1920 的压缩竖版图片，适合群聊发送。
- `beta16.html`：包含图片素材的独立预览页。
- `beta16.json`：本次生成的尺寸和文件记录。

## 生成后续版本

复制 `packs/changelogs/beta16.json` 为新版本文件，修改版本、日期、标题和逐行更新内容，然后运行：

```powershell
node tools/render-release-notes.mjs --data packs/changelogs/beta17.json --out artifacts/changelogs/beta17
```

`changes` 是字符串数组，每项概括一个实际修复的问题，自动按序号逐行排列。同一个问题的实现细节不要拆成多条，不添加宣传口号、条目副标题或标签。例如 beta16 只保留“修复首次打开窗口过大、部分内容显示不全的问题。”一条。图片默认预留 20 行，以 9:16 竖版为基础，不足 20 行时留白，超过 20 行时自动加长图片。较长条目自动换行，换行后按实际占用行数计算；底部群号始终贴近图片底部。默认显示品牌、版本标题、日期、改动和底部群号，使用适合手机查看的较大字体及紧凑行距（1080 像素画布上标题 64px、正文 40px、群号 34px），不显示“四服”“Windows x64”等辅助小字。确有需要时可用 `notes` 添加补充说明；`community` 填写底部群号，保留显示；其他辅助说明不填写。`title` 为图片标题；`background`、`logo` 使用相对仓库根目录的本地图片路径；`accent` 设置六位十六进制主题色。排版在 `tools/templates/release-notes.html` 中维护。

`--scale 1` 默认导出 1080 × 1920 PNG，`--scale 2` 默认导出 2160 × 3840 PNG；JPEG 宽度始终为 1080，超过 20 行时高度同步增加。生成记录包含预留行数、实际行数和最终尺寸。所有相对路径均以仓库根目录为基准，脚本可从其他工作目录调用。输出文件同名时会覆盖，请为不同版本使用不同输出前缀。

首次准备环境：安装 Node.js 22，在 `ui` 目录运行 `npm ci` 和 `npx playwright install chromium`。Windows 默认使用微软雅黑；其他系统需要安装 Noto Sans CJK SC 等中文字体，避免中文显示为方框。生成后检查图片排版即可，不需要启动游戏或重新构建安装包。
