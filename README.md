# VUP 切片播放器（VupPlayer）

自用的 B 站 VUP 切片集合播放器。Windows 桌面，画面 / 纯音频一键切换，五种播放模式，本地曲库可以交给 Claude Code 管理。

> 仅供个人使用，请勿公开分发。B 站在 2026 年已向多个第三方开源项目发出律师函。

## 快速开始

```powershell
# 1. 下载运行时依赖（libmpv-2.dll、yt-dlp.exe）到 tools\
pwsh scripts/fetch-deps.ps1

# 2. 打包到 dist\VupPlayer（需要 .NET 9 SDK）
pwsh scripts/build.ps1

# 3. 运行
dist\VupPlayer\VupPlayer.exe
```

开发时也可以直接 `dotnet run --project src/VupPlayer.App`。

## 使用

- **导入**：在顶部输入框粘贴 BV 号、视频链接、收藏夹、合集或 UP 主空间链接，回车。可以一次粘贴多条（换行或空格分隔）。
- **登录**：右上角「扫码登录 B 站」。登录后可以导入自己的收藏夹（含私密），画质最高到 1080P；未登录只有 360P 左右。cookie 用 Windows DPAPI 加密保存。
- **播放**：双击曲目，或「播放全部」。曲库按主播、切片 man、标签、歌单、订阅源分组。
- **画面 / 纯音频**：播放条上的画面按钮，或按 `V`。纯音频模式只请求音频流，不下载视频。
- **播放模式**：顺序、列表循环、单曲循环、洗牌（一轮内不重复）、随机推荐（避开最近播放）。
- **整理**：右键曲目可以编辑歌名、主播、标签、只播放其中一段，加入歌单，喜欢，隐藏。导入时会按标题规则自动猜主播和歌名。
- **订阅源**：导入过的收藏夹、合集会出现在左侧「订阅源」，右键「立即同步」只导入新视频。
- **迷你模式**：播放条右侧的按钮或 Ctrl+M，切到置顶小窗。拖动空白处移动，滚轮调音量，双击封面或按 Esc 回到主界面，位置会被记住。迷你窗口不显示画面：在画面模式下进入会先切成纯音频，回到主界面时自动恢复；在迷你模式里要求看画面会自动回到主界面。上次退出时在迷你模式，下次启动直接进入。
- **后台**：关闭窗口会最小化到托盘；支持键盘媒体键和 Windows 媒体浮窗。迷你模式下单击托盘图标显示 / 隐藏小窗。

| 快捷键 | 作用 |
|---|---|
| 空格 | 播放 / 暂停 |
| V | 切换画面 / 纯音频 |
| ← / → | 后退 / 前进 5 秒 |
| Ctrl + ← / → | 上一首 / 下一首 |
| Ctrl + ↑ / ↓ | 音量 |
| Ctrl + M | 进入迷你模式（迷你窗口里按 Esc 或 Ctrl + M 返回） |
| Enter | 播放选中曲目 |
| Delete | 从当前歌单移除 |

## 接入 Claude Code

```powershell
claude mcp add vupplayer --scope user -- "<安装目录>\VupPlayer.Mcp.exe"
```

设置菜单里的「复制 Claude Code 注册命令」会生成带正确路径的命令。注册后可以在 Claude Code 里直接说：

- 「把待整理的切片按标题和简介补全歌名和主播，不确定的先提交待确认」
- 「把阿梓的歌里我听得最多的 20 首建一个歌单，然后洗牌播放」
- 「找出重复的切片」「这周我听了什么」
- 「切到纯音频，下一首」

曲库类工具直接读写 SQLite，播放器不开也能用；播放控制类工具需要播放器在运行。Claude 不确定的修改会进入「待确认的修改」，在播放器里右键采纳或拒绝。没有删除曲目的工具，不想听的曲目只会被隐藏。

## 数据位置

`%APPDATA%\VupPlayer\`：

| 文件 | 内容 |
|---|---|
| `library.db` | 曲库（SQLite，WAL 模式），可以直接用 Claude 或任何 SQLite 工具查看 |
| `cookies.dat` | B 站登录态（DPAPI 加密） |
| `settings.json` | 设置 |
| `backups\` | 每天自动备份一次曲库，保留 10 份 |
| `logs\` | 运行日志 |

## 开发

```
src/
  VupPlayer.Core       领域模型、SQLite 曲库、队列与播放模式、推荐器接口、标题规则、控制管道协议
  VupPlayer.Bilibili   B 站接口（WBI 签名、扫码登录、取流、收藏夹、合集）、yt-dlp 兜底、导入器
  VupPlayer.Playback   libmpv P/Invoke 封装
  VupPlayer.App        WPF 主程序
  VupPlayer.Mcp        MCP 服务（stdio）
tests/VupPlayer.Core.Tests
docs/DEVELOPMENT_PLAN.md
```

- 测试：`dotnet test`
- 自己写推荐算法：实现 `IRecommender`（`src/VupPlayer.Core/Recommend/Recommenders.cs`），在 `RecommenderRegistry` 里注册。可以读 `play_events` 表里的播放、完播、跳过记录。
- 开发模式：设置环境变量 `VUPPLAYER_HOME` 指向测试数据目录，`VUPPLAYER_OFFSCREEN=1` 让窗口开在屏幕外并开放调试管道命令。
