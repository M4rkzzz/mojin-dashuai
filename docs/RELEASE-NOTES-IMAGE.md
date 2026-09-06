# 图片版更新日志

生成器使用现有品牌图标和游戏场景，文字由 HTML/CSS 排版，再通过本地 Chromium 导出。无需图片生成服务或在线接口。

## 生成 beta16

在仓库根目录运行：

```powershell
node tools/render-release-notes.mjs --data packs/changelogs/beta16.json
```

输出位于 `artifacts/changelogs/`：

- `beta16.png`：2160 像素宽的高清图片，适合保存或上传原图。
- `beta16.jpg`：1080 像素宽的压缩图片，适合群聊发送。
- `beta16.html`：包含图片素材的独立预览页。
- `beta16.json`：本次生成的尺寸和文件记录。

## 生成后续版本

复制 `packs/changelogs/beta16.json` 为新版本文件，修改版本、日期、标题和逐行更新内容，然后运行：

```powershell
node tools/render-release-notes.mjs --data packs/changelogs/beta17.json --out artifacts/changelogs/beta17
```

`changes` 是字符串数组，每项直接填写一条改动，自动按序号逐行排列。文案平铺直叙，不添加宣传口号、条目副标题或标签。图片高度随内容自动增长，较长条目自动换行。可选 `notes` 数组用于更新方式、下载体积等补充说明，也按行显示。`title` 为图片标题；`background`、`logo` 使用相对仓库根目录的本地图片路径；`accent` 设置六位十六进制主题色。排版在 `tools/templates/release-notes.html` 中维护。

`--scale 1` 导出 1080 像素宽 PNG，默认 `--scale 2` 导出 2160 像素宽 PNG；JPEG 始终保持 1080 像素宽。所有相对路径均以仓库根目录为基准，脚本可从其他工作目录调用。输出文件同名时会覆盖，请为不同版本使用不同输出前缀。

首次准备环境：安装 Node.js 22，在 `ui` 目录运行 `npm ci` 和 `npx playwright install chromium`。Windows 默认使用微软雅黑；其他系统需要安装 Noto Sans CJK SC 等中文字体，避免中文显示为方框。生成后检查图片排版即可，不需要启动游戏或重新构建安装包。
