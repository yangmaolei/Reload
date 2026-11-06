# Reload（M1 + M2 + M3 + M4 + M4.5）

Windows 游戏存档管理与云备份工具（原名 GameSaveSync，v0.4 起更名 **Reload**，首次启动自动迁移旧数据目录）。
已交付 **M1（单机闭环）**、**M2（自动化与可靠性）**、**M3（多设备）**、**M4（识别率与体验）**、
**M4.5（界面打磨与个性化）**：手动备份/恢复 + 自动备份 + 跨机同步 + 平台自动发现 + Material Design 3 界面。

> 代码仓库：<https://github.com/yangmaolei/Reload>　·　分支 `main`　·　目标框架 .NET 8 / WPF / Windows x64

## M4.6 能做什么

- **联网查询存档路径**：手动添加游戏时点「联网查询存档路径」，按名字去 PCGamingWiki 查已知存档位置，
  自动以令牌形式（%USERPROFILE% 等）填入目录；失败静默降级不影响手动操作。
- **海报自动获取**：封面优先下载 Steam 竖版海报（600×900 立绘，裁成方形卡片最合适），失败回退横版头图、
  再回退首字母色块；游戏列表加载后后台自动拉取。
- **控件圆角化**：所有文本框/密码框由"上圆下方"改为四角全圆（含边框），按钮统一全胶囊；修复了按钮
  `Tag` 被占用导致圆角失效的问题。

## M4.5 能做什么

- **更名 Reload**：窗口/托盘/关于页全部换新；数据目录 `%LOCALAPPDATA%\Reload`，启动时自动从旧目录搬移；
  开机自启注册表值同步改名并清理旧值；发布产物为 `Reload.exe`（带自绘应用图标）。
- **主题自定义**：6 个预设主题色（青/靛蓝/翠绿/琥珀/玫红/石墨）× 亮/暗/跟随系统，设置页即时切换、持久化；
  亮暗两套完整 MD3 中性色板，跟随系统时读 Windows 深色模式。
- **设置增强**：触发时机（退出延迟/去抖/空闲间隔）可编辑保存；**全局排除规则**（每行一个通配符，
  对所有游戏生效，下次快照立即生效）；规则库文件路径与数据目录入口集中在"数据与设备"。
- **图标与清晰度**：应用 ico（7 尺寸自绘）接入 exe/窗口/托盘；20 个按钮补齐 Segoe Fluent 图标；
  封面 `HighQuality` 缩放消除 125% 缩放下重采样发糊；Snackbar 移到底部中央避免遮挡内容。

## M4 能做什么

- **平台自动发现**：Steam（多库 `libraryfolders.vdf` + `appmanifest_*.acf` + `userdata/<id>/<appid>/remote`）、
  Epic（`Manifests/*.item`）、GOG（`goggame-*.info`）、Ubisoft（`savegames/<userId>/<gameId>`）、
  Xbox / 微软商店（MSIX 游戏包 → `Packages/<PFN>/SystemAppData/wgs`；Xbox 原生库 `<盘符>\XboxGames`
  的 `GameSave\pgs` 云存档）。
- **路径规则库**：内置 11 条（My Games / Documents / Saved Games / Roaming / LocalAppData / Unity / Unreal /
  RPG Maker / Ren'Py / GameMaker / 安装目录）+ 引擎识别；数据目录 `save-paths.yaml` 可按 id 覆盖或追加，
  首次启动生成 `save-paths.sample.yaml`。
- **探测模式**：记录候选目录指纹 → 玩几分钟 → 对比差异，按新增/修改、存档扩展名、路径关键词打分，勾选即用。
- **封面**：Steam CDN 头图缓存到 `covers/`，无封面用 MD3 色块 + 首字母。
- **诊断页**：存储/队列/设备/远端、最近 200 条日志、连接测试、导出**已脱敏**诊断包（zip）。
- **便携模式**：exe 同级 `portable.flag` → 数据放 `<exe>/data`，不写注册表，界面禁用开机自启。
- **Material Design 3 界面**：完整色彩角色/字体阶梯/形状令牌；五型按钮、三型卡片、filled 输入框、导航 rail、
  chip、Switch、FAB；卡片墙 + 详情时间线 + 差异视图 + 四页导航 + 三步首次向导 + 探测窗口。

## M3 能做什么

- **设备注册与索引**：远端 `index.json` 记录每个游戏的最新快照与设备；`devices/<id>.json` 记录设备在线时间。
- **下行同步**：每 60 秒只轮询 `index.json`（`If-None-Match`，命中 304 几乎零成本）；发现本机没见过的快照才拉清单与对象，逐个校验 BLAKE3。
- **冲突不丢数据**：两台各改一次 → 云端两个快照都在，索引标 `conflict`，界面并排展示两侧（设备/时间/文件数/大小/相对共同祖先差异），可选一边或导出两侧 zip。
- **跨机重新定位**：令牌解析不到时标记「需重新定位」，由用户把存档根映射到本机目录（存 `games.path_overrides`），绝不猜路径。
- **服务商适配**：Generic 单次 PUT / Nextcloud 分块上传（MOVE 失败自动回退）/ RateLimited 串行限速。

> 需求方案与分阶段 Prompt 见 `../2026-10-30-20-30-55/游戏存档同步工具-方案与Prompt.md`
> 与 `../2026-10-30-20-30-55/分阶段Prompt-M1-M2-M3.md`。

## M2 能做什么

- **自动触发**：检测到游戏进程退出（默认延迟 2 s）→ 自动快照并上传；启动时打 `pre_launch` 保险点；非运行期间的文件变更走 `post_change`；游戏运行中每 15 分钟尝试 `idle` 快照（要求磁盘空闲、CPU < 30%、非全屏独占）。
- **三层变更检测**：`FileSystemWatcher`（64 KB）→ size + mtime + `FILE_ID_INFO` 指纹 → BLAKE3 内容比对；3 s 去抖 + 400 ms 复检 + 200/400/800/1600/3200 ms 退避，**内容没变不产生快照**。
- **崩溃安全的上传队列**：所有网络操作先写 `outbox` 表；指数退避（1s→…→5min ±20%）；启动重放 `pending`/`inflight`；同一对象只传一次。
- **GFS 保留策略**：最近 20 个 + 每日 1×7 + 每周 1×4；手动快照永不自动删，`pre_restore` 保 7 天；清理前给出"预计释放空间"确认。
- **常驻形态**：托盘图标、暂停 30min/1h/直到重启、开机自启、关窗最小化到托盘、退出前对待上传项二次确认、第二实例唤起已有窗口。

## M1 能做什么

- 手动添加游戏（名称 + 一个或多个存档目录 + 排除通配符），路径存成**令牌形式**（`%APPDATA%\Studio\Game`），不存绝对路径。
- 点「立即备份」：递归枚举 → 逐文件 **BLAKE3** → 写入本地 **CAS**（`objects/<hh>/<hash>`）→ 生成全量清单 → 上传 WebDAV。
- 快照时间线：时间 / 触发原因 / 文件数 / 大小 / 上传状态 / 备注。
- 点「恢复到此时点」：先算计划（新增 / 覆盖 / 删除 + 字节数）确认，**恢复前自动打 pre_restore 保险点**，然后逐文件「临时文件 → 校验哈希 → 原子 rename」。
- WebDAV 配置页：测试连接（MKCOL + 探针写入/读回/删除）、密码 DPAPI 加密存储、自签名证书指纹固定。
- 日志页：最近 200 条（Serilog 同步落盘到 `logs/`）。

## 工程结构

```
GameSaveSync.sln
├─ src/GameSaveSync.Core            核心引擎（net8.0，无 WPF / 无 Win32 API，可单测）
│   ├─ Abstractions/                IFileSystem、IHashService、IHasher
│   ├─ Configuration/AppPaths       数据目录布局（objects / manifests / logs / db）
│   ├─ Detection/                   指纹缓存、稳定性门（400ms 复检 + 退避）
│   ├─ Covers/                      封面缓存（Steam CDN + 本机缓存）
│   ├─ Diagnostics/                 IAppLogger（环形缓冲）、IClock、诊断包（脱敏导出）
│   ├─ Locator/                     平台发现（VDF/JSON/注册表）、规则库（YAML）、引擎识别、探测模式
│   ├─ Hashing/Blake3HashService    BLAKE3，64 位小写十六进制
│   ├─ Models/                      Game / SaveRoot / Snapshot / SnapshotManifest / 枚举
│   ├─ Paths/                       令牌展开与反向令牌化、Glob 匹配、存档根解析、路径覆盖
│   ├─ Probe/                       探测模式（指纹 → 运行后对比 → 打分）
│   ├─ Remote/                      index.json 模型与合并状态机、索引服务、轮询器、导入器、冲突视图
│   ├─ Retention/                   GFS 保留策略（预览 + 执行）
│   ├─ Scheduling/                  进程/文件事件抽象、匹配规则、SnapshotScheduler（触发器编排）
│   ├─ Snapshots/                   SnapshotService（快照）、清单序列化与本地副本
│   ├─ Storage/                     SQLite（WAL）+ 仓储 + CAS 对象库 + outbox/指纹仓储
│   ├─ Restore/                     RestorePlan / RestoreService（两阶段 + 原子替换）
│   └─ Sync/                        IRemoteStore、outbox、OutboxProcessor、UploadService
├─ src/GameSaveSync.Infrastructure  PhysicalFileSystem（\\?\ 长路径）、DPAPI、进程轮询+WMI、目录监视、资源探针、开机自启、Steam/UWP 注册表、封面下载
├─ src/GameSaveSync.WebDav          WebDavClient（MKCOL 逐级/临时名+MOVE/证书固定/限流）、WebDavRemoteStore、Providers（Generic/Nextcloud/RateLimited）
├─ src/GameSaveSync.App             WPF + MVVM + DI + 单实例 + 托盘 + Serilog；Themes/ 为 MD3 令牌与控件，
│                                   Views/Pages 为卡片墙/诊断/设置，WizardWindow/ProbeWindow 为向导与探测
└─ tests/GameSaveSync.Tests         xUnit：145 个用例，含进程内 WebDAV 服务器（ETag/412/PROPFIND/分块）、真机监视与 31 条验收
```

## 构建与测试

本机原先只有 .NET 运行时、没有 SDK，因此 SDK 装在用户目录（不需要管理员）：

```
C:\Users\yang\.dotnet-sdk\sdk8\dotnet.exe
```

```powershell
# 构建 + 测试
& "C:\Users\yang\.dotnet-sdk\sdk8\dotnet.exe" build .\GameSaveSync.sln -c Debug
& "C:\Users\yang\.dotnet-sdk\sdk8\dotnet.exe" test  .\GameSaveSync.sln

# 自包含单文件发布（win-x64，约 70 MB）
& "C:\Users\yang\.dotnet-sdk\sdk8\dotnet.exe" publish .\src\GameSaveSync.App\GameSaveSync.App.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o .\publish\win-x64
```

启动：`.\publish\win-x64\GameSaveSync.exe`（或 `dotnet run --project .\src\GameSaveSync.App`）。
数据目录：`%LOCALAPPDATA%\GameSaveSync`（`gamesavesync.db`、`objects/`、`manifests/`、`logs/`、`covers/`）；
便携模式：exe 同级放 `portable.flag`，数据改到 `<exe>\data`。

## 本机布局

```
%LOCALAPPDATA%\GameSaveSync\
  gamesavesync.db           SQLite（WAL），games / snapshots / snapshot_files / objects / outbox / devices / settings
  objects\<hh>\<hash>       内容寻址对象，只写一次，写入后永不修改
  objects\tmp\*.part        暂存区（崩溃残留启动时清理）
  manifests\<gameId>\<ts>-<sid>.json   清单的人眼可读副本（权威源是 DB）
  logs\gamesavesync-*.log   Serilog 滚动文件
  covers\steam-<appid>.jpg  封面缓存
  save-paths.sample.yaml    自定义路径规则样例（复制成 save-paths.yaml 生效）
```

远端（WebDAV，`RemoteRoot` 之下）：

```
vault.json                                  版本/设备/加密开关（M1 已创建）
objects/<hh>/<hash>                          不可变，重复 PUT 幂等
games/<gameId>/snapshots/<ts>-<sid>.json     清单（临时名 PUT + MOVE）
index.json                                   全局索引（乐观锁 + 设备注册）
```

## 不可协商的设计约束（已落地）

1. **绝不用 mtime 判断内容是否变化**：一律 BLAKE3；mtime/size 只做稳定性判定与 L2 预过滤。
2. **绝不直接信事件**：读文件前后各 stat 一次，size/mtime 变了就重读（最多 3 次），仍不稳定则跳过该文件。
3. **绝不产生半截对象**：先写 `objects/tmp/*.part` → 算哈希 → rename 进 CAS；进程被杀最多留 tmp。
4. **绝不产生空快照**：目录被删/清空 → 标记「需重新定位」并报错，不写空清单。
5. **恢复前必打 pre_restore 快照**；目标文件先写临时文件 → 校验哈希 → `MoveFileEx(REPLACE_EXISTING)` 原子替换。
6. **只读/隐藏/系统属性**：替换前清除，替换后按「目标原有属性 ∪ 快照属性」复原。
7. **路径全链路 `\\?\`**，不跟随 junction/symlink，保留原始大小写、比较用 lower-case 归一化。
8. **凭据只用 DPAPI 加密存 SQLite**，任何日志不出现明文；自签名证书只按指纹放行，不做全局跳过。
9. **网络操作一律先入 outbox 再执行**，进程在任意点被杀都不丢待办；对象按 hash 命名，重复执行幂等。
10. **快照全局并发度 1**，后台路径线程优先级 `IDLE`、1 MB 块、块间 50 ms，绝不抢游戏 IO。
11. **上行顺序对象 → 清单 → 索引**，前置依赖未就绪就让出队项延后（绝不让索引指向不存在的东西）。
12. **任何合并分支都不删除快照**；冲突只影响"默认推荐哪个版本"。
13. **不猜路径**：令牌解析不到就标记"需重新定位"，等用户显式映射。
14. **识别结果必须可解释**：自动发现的每个存档根都来自"平台目录 / 规则模板 / 引擎雷达"，且目录必须存在并含存档类文件；不确定就让用户探测或手动选。
15. **便携模式不写注册表**：数据在程序目录，开机自启等系统集成显式禁用。

## 文档

- `docs/M1-交付说明.md`：M1 启动方式、8 条验收实测、M2 预留清单、偏差。
- `docs/M2-交付说明.md`：M2 六块实现、8 条验收实测（含 outbox 状态机/GFS 规则）、需要真机补测的两项。
- `docs/M3-交付说明.md`：M3 协议实现、冲突合并状态机、7 条验收实测、seq 语义修正说明。
- `docs/M4-交付说明.md`：M4 六块实现、MD3 设计令牌与页面、6 条验收实测、与规格的 6 处偏差、真机补测清单。
- `docs/M4.5-交付说明.md`：更名 Reload 的迁移细节、主题系统架构（DynamicResource 即时换肤）、设置增强、图标体系。
- `docs/自测清单.md`：手工自测步骤（中文/emoji/长路径/只读/大文件/断网/强杀 + M2/M3/M4 自动化项）。
- `docs/test-run.log`：一次完整测试运行的原始输出（128 个用例）。
