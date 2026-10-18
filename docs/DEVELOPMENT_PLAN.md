# vupMusicPlayer 开发方案

> 自用的 B 站切片集合播放器。Windows 桌面，轻量，视频/纯音频切换，循环与随机，本地曲库可由 Claude Code 管理。
> 版本：v0.1（2026-10-17），demo 实现状态见下一节

---

## 0. Demo 实现状态（2026-10-18）

**与原方案不同的地方**
- 运行时用 .NET 9（本机只有 .NET 9 SDK），以后升级 .NET 10 只需改目标框架。
- MCP 服务是单独的 `VupPlayer.Mcp.exe`，不是主程序加 `--mcp` 参数。WPF 程序处理 stdio 不方便，分开更简单。
- mpv 封装没用 MPVSharp，直接自写 P/Invoke（十几个函数），实测稳定。
- 取流默认走 B 站 WBI 接口（约 0.5 秒），yt-dlp（约 4 秒）作为兜底；原方案是反过来。
- 设置页先用菜单代替。
- 发布目录约 165 MB，其中 libmpv-2.dll 占 121 MB，超出原先「60 MB」的目标。

**已完成**
- M0 全部；M1 全部；M2 大部分（收藏夹、合集、UP 主投稿、订阅源手动同步、规则提取、歌单、标签、主播别名）；M3 大部分（托盘、SMTC 媒体键、单实例）；M4 的 MCP 服务与待确认界面。
- 额外：播放卡住的看门狗、每日自动备份。

**未完成 / 留到后面**
- 全局热键、迷你模式、订阅源定时自动同步。
- M4 的「用 `claude -p` 无头批量打标签」按钮。目前通过 MCP 在 Claude Code 里做同样的事。
- M5：音频本地缓存、把一个切片拆成多首的分段编辑器（现在只能给一条曲目设起止时间）、数据导出。

**实测数据**（隔离测试目录，发布版）

| 场景 | 私有内存 |
|---|---|
| 空闲（已加载曲库） | 约 95 MB |
| 纯音频播放 | 约 116 MB |
| 画面播放（480P） | 约 250 MB |

---

## 1. 目标与非目标

**目标**
- 从 B 站收藏夹、UP 主投稿、合集、单个 BV 号导入切片，建立本地曲库。
- 在线流式播放，可随时切换「画面 + 声音」和「纯音频」。
- 播放模式：顺序、列表循环、单曲循环、随机（洗牌）、曲库随机。
- 曲库按「原主播 / 切片 man（UP 主）/ 歌名 / 自定义标签」管理。
- 本地 MCP 服务，让 Claude Code 能查询和整理曲库、控制播放。
- 推荐先做随机，留好接口，算法以后自己写。
- 轻量：空闲内存目标 < 100 MB，安装包目标 < 60 MB（含 mpv 与 yt-dlp）。

**非目标（至少第一版不做）**
- 跨平台、移动端。
- 弹幕、评论、投币、点赞等社区功能。
- 公开发布。仓库保持私有，见第 9 节。

---

## 2. 技术选型

| 层 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 10（LTS）+ C# | Windows 原生，生态完整 |
| UI | WPF | 不带浏览器内核，内存小；托盘、SMTC 接入方便 |
| UI 风格（可选） | WPF-UI（Fluent） | 现成的 Win11 风格控件，不想要可去掉 |
| 播放内核 | libmpv | 硬解、DASH 音视频分离、HTTP 头、无缝切换都现成 |
| mpv 封装 | 先试 MPVSharp；不合适就自写 P/Invoke | 现有封装维护一般，mpv client API 只需十几个函数 |
| 取流 | yt-dlp（主）+ 自写 playurl 接口（备） | yt-dlp 活跃维护，B 站改版时更新它即可 |
| B 站接口 | 自写少量：扫码登录、收藏夹、投稿列表、视频信息 | 只写导入需要的部分 |
| WBI 签名 | LC6464.BiliBiliWbiSign 或自写（约 50 行） | |
| 存储 | SQLite（Microsoft.Data.Sqlite + Dapper），WAL 模式 | 单文件、可被 Claude 直接读、支持多进程 |
| MCP | 官方 C# SDK（ModelContextProtocol） | |
| 日志 | Serilog 写本地文件 | |

---

## 3. 总体架构

```
┌─────────────────────────── VupPlayer.App (WPF) ───────────────────────────┐
│  曲库/歌单/订阅源/队列 视图   播放条   视频区(可收起)   迷你模式   托盘   │
│  SMTC / 媒体键 / 全局热键                                                 │
│        │                        │                                         │
│        ▼                        ▼                                         │
│  ┌──────────────┐      ┌──────────────────┐      ┌──────────────────┐    │
│  │ Core         │◄────►│ Playback         │◄────►│ StreamResolver   │    │
│  │ 曲库/队列/   │      │ libmpv 控制      │      │ yt-dlp / playurl │    │
│  │ 播放模式/    │      │ 视频⇄纯音频      │      │ 直链缓存 + 预取  │    │
│  │ IRecommender │      └──────────────────┘      └──────────────────┘    │
│  └──────┬───────┘                                                         │
│         │            ┌──────────────────┐                                 │
│         │            │ Bilibili         │  扫码登录、收藏夹/投稿同步       │
│         │            └──────────────────┘                                 │
│         │            ┌──────────────────┐                                 │
│         │            │ 控制管道服务     │  命名管道 \\.\pipe\vupplayer     │
│         │            └────────▲─────────┘                                 │
└─────────┼─────────────────────┼───────────────────────────────────────────┘
          ▼                     │
   library.db (SQLite, WAL)     │
          ▲                     │
          │        ┌────────────┴──────────┐
          └────────┤ vupplayer.exe --mcp   │◄── stdio ── Claude Code
                   │ (MCP 服务进程)         │
                   └───────────────────────┘
```

**要点**
- MCP 服务是同一个 exe 的另一种启动方式，由 Claude Code 通过 stdio 拉起。
- 曲库读写：MCP 进程直接读写 SQLite（WAL 允许与主程序并发）。
- 播放控制：MCP 进程通过命名管道发给正在运行的主程序；主程序没开时，播放类工具返回「播放器未运行」。
- 主程序监听数据库变更（简单做法：MCP 写完后经管道发一个 `library-changed` 通知），刷新界面。

---

## 4. 解决方案结构

```
vupMusicPlayer/
├─ src/
│  ├─ VupPlayer.Core/          # 领域模型、仓储、队列、播放模式、推荐接口（无 UI 依赖）
│  ├─ VupPlayer.Bilibili/      # B 站接口客户端、登录、同步
│  ├─ VupPlayer.Playback/      # libmpv 封装、StreamResolver
│  ├─ VupPlayer.Mcp/           # MCP 工具定义、管道客户端
│  └─ VupPlayer.App/           # WPF 主程序（入口，含 --mcp 分支）
├─ tests/
│  ├─ VupPlayer.Core.Tests/
│  └─ VupPlayer.Bilibili.Tests/
├─ tools/                      # mpv-2.dll、yt-dlp.exe（不入库，脚本下载）
├─ scripts/
│  └─ fetch-deps.ps1           # 下载 libmpv 与 yt-dlp 到 tools/
└─ docs/
```

用户数据放在 `%APPDATA%\VupPlayer\`：`library.db`、`cookies.dat`（DPAPI 加密）、`settings.json`、`logs\`、`cache\`。

---

## 5. 数据模型（SQLite）

```sql
-- 视频（一个 BV 号）
CREATE TABLE videos (
  bvid        TEXT PRIMARY KEY,
  title       TEXT NOT NULL,
  up_mid      INTEGER NOT NULL,     -- 切片 man
  up_name     TEXT NOT NULL,
  cover_url   TEXT,
  description TEXT,
  pubdate     INTEGER,              -- unix 秒
  added_at    INTEGER NOT NULL,
  status      TEXT NOT NULL DEFAULT 'ok'   -- ok / unavailable（失效、下架）
);

-- 曲目：一个分P，或分P中的一段（一个切片里有多首歌时）
CREATE TABLE tracks (
  id           INTEGER PRIMARY KEY,
  bvid         TEXT NOT NULL REFERENCES videos(bvid),
  cid          INTEGER NOT NULL,
  page         INTEGER NOT NULL,
  part_title   TEXT,
  duration_sec INTEGER,
  start_sec    REAL,                -- 可空；分段时使用
  end_sec      REAL,
  song_title   TEXT,                -- 歌名
  streamer_id  INTEGER REFERENCES streamers(id),  -- 原主播
  favorite     INTEGER NOT NULL DEFAULT 0,
  hidden       INTEGER NOT NULL DEFAULT 0,        -- 不想再听
  note         TEXT
);
CREATE INDEX ix_tracks_bvid ON tracks(bvid);

CREATE TABLE streamers (            -- 原主播（VUP）
  id      INTEGER PRIMARY KEY,
  name    TEXT NOT NULL UNIQUE,
  aliases TEXT,                     -- JSON 数组
  mid     INTEGER                   -- 主播本人的 B 站 uid，可空
);

CREATE TABLE tags (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL DEFAULT 'custom'   -- genre / language / event / custom
);
CREATE TABLE track_tags (
  track_id INTEGER REFERENCES tracks(id) ON DELETE CASCADE,
  tag_id   INTEGER REFERENCES tags(id)   ON DELETE CASCADE,
  PRIMARY KEY (track_id, tag_id)
);

-- 待确认的元数据建议（Claude 或规则产生）
CREATE TABLE suggestions (
  id         INTEGER PRIMARY KEY,
  track_id   INTEGER REFERENCES tracks(id) ON DELETE CASCADE,
  field      TEXT NOT NULL,         -- song_title / streamer / tag
  value      TEXT NOT NULL,
  source     TEXT NOT NULL,         -- claude / rule
  confidence REAL,
  created_at INTEGER NOT NULL,
  state      TEXT NOT NULL DEFAULT 'pending'   -- pending / accepted / rejected
);

CREATE TABLE playlists (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, created_at INTEGER NOT NULL
);
CREATE TABLE playlist_items (
  playlist_id INTEGER REFERENCES playlists(id) ON DELETE CASCADE,
  track_id    INTEGER REFERENCES tracks(id)    ON DELETE CASCADE,
  position    INTEGER NOT NULL,
  PRIMARY KEY (playlist_id, position)
);

-- 订阅源：收藏夹 / UP 主投稿 / 合集
CREATE TABLE sources (
  id          INTEGER PRIMARY KEY,
  kind        TEXT NOT NULL,        -- fav / space / season
  remote_id   TEXT NOT NULL,
  title       TEXT,
  auto_sync   INTEGER NOT NULL DEFAULT 1,
  last_synced INTEGER,
  UNIQUE(kind, remote_id)
);
CREATE TABLE source_videos (
  source_id INTEGER REFERENCES sources(id) ON DELETE CASCADE,
  bvid      TEXT    REFERENCES videos(bvid),
  PRIMARY KEY (source_id, bvid)
);

-- 播放事件：推荐算法的原料
CREATE TABLE play_events (
  id          INTEGER PRIMARY KEY,
  track_id    INTEGER REFERENCES tracks(id) ON DELETE CASCADE,
  started_at  INTEGER NOT NULL,
  played_sec  REAL NOT NULL,
  completed   INTEGER NOT NULL,     -- 播完 90% 以上
  skipped     INTEGER NOT NULL,     -- 30 秒内手动切走
  mode        TEXT,                 -- video / audio
  context     TEXT                  -- 从哪个歌单/模式播放
);

CREATE TABLE schema_version (version INTEGER NOT NULL);
```

迁移：启动时按 `schema_version` 顺序执行内置的 SQL 脚本。

---

## 6. 关键模块设计

### 6.1 取流（StreamResolver）

```csharp
public interface IStreamResolver {
    Task<ResolvedStream> ResolveAsync(TrackRef track, StreamMode mode, CancellationToken ct);
}
public record ResolvedStream(
    string? VideoUrl, string AudioUrl,
    IReadOnlyDictionary<string,string> Headers,   // Referer、User-Agent
    DateTimeOffset ExpiresAt);
```

- **YtDlpResolver（主）**：运行 `yt-dlp -J --cookies <临时cookies.txt> https://www.bilibili.com/video/{bvid}?p={page}`，从 JSON 里挑最佳音频流和合适的视频流。
- **BiliPlayUrlResolver（备）**：直接调用 playurl 接口（WBI 签名），速度快，yt-dlp 失败时回退。
- **缓存与预取**：直链按 `(bvid, cid)` 缓存到过期前 10 分钟；当前曲目播到一半时预解析下一首，消除切歌等待。
- 解析结果直接交给 mpv：视频流作为主文件，音频流用 `audio-files` 挂载；纯音频模式只加载音频流。请求头用 mpv 的 `http-header-fields`。

### 6.2 播放（Playback）

- mpv 渲染到 WPF 里托管的一个 Win32 子窗口（`HwndHost`，设置 mpv 的 `wid`）。
- 关键属性：`vid`、`aid`、`pause`、`time-pos`、`volume`、`ab-loop-a/b`；监听 `end-file`、`playback-time` 事件。
- **视频 ⇄ 纯音频切换**：
  - 视频 → 纯音频：记下 `time-pos`，用纯音频直链重新加载并 `start=` 到原位置，同时收起视频区。约 0.5 秒的间断，换来真正不下载视频流。
  - 纯音频 → 视频：同理用视频直链重载。
  - 快速模式（可选设置）：只设 `vid=no`，无缝但仍下载视频流。
- 分段曲目：用 mpv 的 `start` / `end` 选项。
- 音量标准化（可选）：mpv `af=loudnorm` 或 `dynaudnorm`，切片音量差别通常很大，值得做。

### 6.3 队列与播放模式（Core）

- 「歌单」是持久化集合，「队列」是当前要播的序列。从歌单/筛选结果点播放 = 用它替换队列。
- 模式：
  - `Sequential`：播完即停。
  - `RepeatAll`：到末尾回到开头。
  - `RepeatOne`：单曲循环。
  - `Shuffle`：对队列做 Fisher-Yates 洗牌，播完一轮再洗，保证一轮内不重复；上一首可回退。
  - `LibraryRandom`：不依赖队列，每次由推荐器从整个曲库（或当前筛选）挑下一首。
- 推荐接口：

```csharp
public interface IRecommender {
    string Name { get; }
    Task<long?> NextAsync(RecommendContext ctx, CancellationToken ct);
}
public record RecommendContext(
    IReadOnlyList<long> CandidateTrackIds,   // 当前筛选范围
    IReadOnlyList<long> RecentHistory,       // 最近播放
    long? CurrentTrackId);
```

  - 第一版 `RandomRecommender`：在候选集中均匀随机，排除最近 N 首（默认 N = min(20, 候选数/2)）和 `hidden` 曲目。
  - 你以后写的算法实现这个接口即可，可读 `play_events` 表；设置里可切换推荐器。

### 6.4 B 站接入（Bilibili）

- **扫码登录**：生成二维码 → 轮询状态 → 保存 cookie（SESSDATA、bili_jct、DedeUserID 等），用 Windows DPAPI 加密存到 `cookies.dat`。取流时临时生成 Netscape 格式 cookies.txt 给 yt-dlp，用完删除。
- **同步**：
  - 我的收藏夹列表 → 选择要订阅的收藏夹 → 拉取全部视频。
  - UP 主投稿（切片 man 追更）、合集/系列。
  - 视频详情：拿到分P列表，每个分P生成一条 track。
  - 增量：按 `last_synced` 只取新的；源里消失的视频不删除，只标记；失效视频标 `unavailable`。
- **限速**：所有请求经一个全局限流器（例如每秒 2 次，带随机抖动），遇到风控错误码指数退避。
- **接口参考**：bilibili-API-collect 的社区 fork、biu 与电梓的源码。

### 6.5 规则提取（无需 Claude 的基础打标）

切片标题常见格式例如「【主播名】歌名 / 翻唱」「主播名 - 歌名 2026.10.01直播」。导入时先跑一组可配置的正则，结果写进 `suggestions`（source = rule），置信度高的自动采纳。这样不开 Claude 也有基本可用的元数据。

### 6.6 Claude Code 集成（Mcp）

**注册**

```
claude mcp add vupplayer -- "C:\path\to\VupPlayer.App.exe" --mcp
```

**工具清单**

| 类别 | 工具 | 说明 |
|---|---|---|
| 查询 | `search_tracks` | 按关键词、主播、UP 主、标签、是否收藏、是否未打标签筛选，分页 |
| | `get_track` | 单条详情，含标签、播放统计 |
| | `list_streamers` / `list_tags` / `list_playlists` / `list_sources` | |
| | `get_play_stats` | 按时间范围统计播放次数、完播率、跳过率 |
| 整理 | `update_tracks` | 批量设置歌名、主播、标签、收藏、隐藏 |
| | `propose_metadata` | 写入 `suggestions`，等用户在界面确认 |
| | `merge_streamers` | 合并别名重复的主播 |
| | `find_duplicates` | 同一首歌的多个切片 |
| 歌单 | `create_playlist` / `add_to_playlist` / `remove_from_playlist` / `reorder_playlist` | |
| 播放 | `now_playing` / `play` / `enqueue` / `control`（暂停、下一首、模式、视频/音频） | 需要主程序运行 |
| 同步 | `sync_source` | 触发某个订阅源同步 |

**安全约束**
- 删除类操作只做软删除（`hidden`），不提供物理删除工具。
- 批量修改超过 50 条时，工具返回预览并要求再调用一次带 `confirm: true` 的请求。

**无头批量打标签（可选，M4 后期）**
- 界面上「用 Claude 整理未打标签曲目」按钮，后台执行：

```
claude -p "<提示词 + 曲目标题/简介 JSON>" --output-format json
```

- 结果解析后写入 `suggestions`（source = claude），在界面里逐条或批量确认。
- 每批 50 条左右，避免单次过长。

### 6.7 系统集成（App）

- 托盘：左键显示/隐藏，右键菜单含播放控制和模式切换；关闭窗口默认最小化到托盘。
- SMTC：通过 `Windows.Media.Playback` 的 `SystemMediaTransportControls` 显示封面、标题，响应系统媒体键与锁屏控制。
- 全局热键（可配置）：播放/暂停、上一首、下一首、切换视频/音频。
- 迷你模式：置顶小窗，只有封面、标题、控制按钮。
- 单实例：第二次启动时把参数（比如 BV 链接）经管道交给已运行实例。

---

## 7. 里程碑

| 里程碑 | 内容 | 验收标准 | 估时 |
|---|---|---|---|
| **M0 技术验证** | WPF 窗口嵌 mpv 播放一个 BV；yt-dlp 解析；纯音频切换；扫码登录；测内存 | 能播、能切、能登录；空闲内存 < 100 MB；确认 yt-dlp 对收藏夹/分P/合集的支持 | 1–2 天 |
| **M1 核心播放** | 数据库与迁移；手动添加 BV；曲库列表；队列与五种模式；`RandomRecommender`；播放事件记录；视频区收起 | 添加 20 个切片，五种模式行为正确，播放记录入库；Core 单元测试通过 | 4–6 天 |
| **M2 导入与同步** | 收藏夹/UP 主/合集订阅与增量同步；分P；规则提取；歌单；标签与主播管理界面；失效处理 | 同步一个 200+ 视频的收藏夹无风控报错；重复同步不产生重复数据 | 4–6 天 |
| **M3 系统集成** | 托盘、SMTC、媒体键、全局热键、迷你模式、单实例、设置页 | 最小化到托盘后用键盘媒体键能完整控制 | 2–3 天 |
| **M4 Claude 集成** | MCP 服务与全部工具；命名管道控制；建议确认界面；无头批量打标签 | 在 Claude Code 里完成「给未打标签的切片补全主播和歌名」和「按主播建歌单并播放」 | 3–5 天 |
| **M5 增强** | 音频本地缓存；音量标准化；直链预取；分段曲目编辑；推荐器切换入口；数据导出 JSON | 断网时能播已缓存曲目；切歌无明显等待 | 按需 |

估时按一个人加 Claude 辅助计算，只作参考。

---

## 8. 测试策略

- **Core**：队列与模式、随机器的不重复约束、迁移脚本，全部单元测试。
- **Bilibili**：接口解析用录制的 JSON 夹具做离线测试；另有一组手动触发的在线冒烟测试，不进日常测试。
- **Playback**：手动测试清单（视频/音频切换、分P、直链过期后重新解析、断网恢复）。
- **MCP**：用 MCP Inspector 或直接在 Claude Code 里跑一遍工具清单。

---

## 9. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| B 站接口变更或风控 | 无法取流或同步 | 取流以 yt-dlp 为主，自写接口为备；全局限流；错误码与日志清晰 |
| B 站法务（2026 年已向 bilibili-API-collect、downkyi、BiliTools 发函） | 公开仓库可能被要求下架 | 仓库保持私有、只自用；不分发、不做下载器功能宣传 |
| yt-dlp 每次启动 1–3 秒 | 切歌等待 | 直链缓存 + 预取下一首；必要时用备用解析器 |
| libmpv C# 封装不成熟 | 开发受阻 | 只依赖十几个 client API 函数，必要时自写 P/Invoke |
| 直链约 2 小时过期 | 长时间暂停后无法继续 | 播放出错时自动重新解析并从原位置续播 |
| Claude 批量误改元数据 | 曲库被污染 | 建议表 + 人工确认；批量阈值确认；只软删除；定期自动备份 db |
| Cookie 泄露 | 账号风险 | DPAPI 加密；临时 cookies.txt 用完即删；日志脱敏 |

---

## 10. 待实测确认（M0 中完成）

- yt-dlp 对收藏夹链接、合集链接、分P 的支持程度。
- MPVSharp 在 WPF `HwndHost` 下是否稳定，否则改自写 P/Invoke。
- 纯音频重载方式的间断时长是否可接受。
- 登录态下 yt-dlp 能拿到的最高音质（Hi-Res / FLAC 是否需要大会员）。
