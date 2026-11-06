# Reload

**Windows 游戏存档备份与多设备同步工具。**

Reload 会自动找出你电脑上各个游戏的存档目录，把它们做成**不可变的内容寻址快照**并备份到本地
CAS（内容寻址存储），再通过 **WebDAV** 同步到你自己控制的云盘（Nextcloud / ownCloud、坚果云、
群晖、Alist 等）。换一台电脑时，存档会跟着走——即使那台机器上游戏装在别的位置。

- 平台：Windows x64
- 技术栈：.NET 8 / WPF / Material Design 3
- 当前版本：**1.1.0**
- 许可：[MIT](LICENSE)

> 项目原名 `GameSaveSync`，v0.4 起更名为 **Reload**。首次启动会自动把旧数据目录
> `%LOCALAPPDATA%\GameSaveSync` 迁移到 `%LOCALAPPDATA%\Reload`，无需手动操作。

---

## 目录

- [核心特性](#核心特性)
- [安装](#安装)
- [快速上手](#快速上手)
- [配置说明](#配置说明)
- [使用示例](#使用示例)
- [数据与目录布局](#数据与目录布局)
- [远端（WebDAV）布局](#远端webdav-布局)
- [常见问题](#常见问题)
- [从源码构建](#从源码构建)
- [工程结构](#工程结构)
- [贡献](#贡献)
- [许可证](#许可证)

---

## 核心特性

### 自动发现游戏与存档目录

内置 5 个平台定位器，扫描本机游戏库并推导存档位置（另有「手动添加」）：

| 平台 | 扫描依据 | 存档位置来源 |
|---|---|---|
| **Steam** | `libraryfolders.vdf`（多库）+ `appmanifest_*.acf` | 定位器直接给出 `userdata/<id>/<appid>/remote` |
| **Epic** | `Manifests/*.item` | 定位器给安装目录，存档路径由规则库推导 |
| **GOG** | `goggame-*.info` | 定位器给安装目录，存档路径由规则库推导 |
| **Ubisoft** | `savegames/<userId>/<gameId>` | 定位器直接给出 `Ubisoft Game Launcher\savegames\<gameId>` |
| **Xbox / 微软商店** | MSIX(GDK) 游戏包清单 + `XboxGames` 原生库 | 定位器直接给出 `Packages/<PFN>/SystemAppData/wgs`、`XboxGames\GameSave\pgs` |
| **手动** | 你自己填 | 你自己填 |

共 6 种来源（5 个平台 + 手动）。

存档目录来自三条来源，每一条都可解释：

1. **平台目录** —— 定位器直接读出的路径（上面那张表）。
2. **路径规则库** —— 内置 11 条规则（`My Games` / `Documents` / `Saved Games` / `Roaming` /
   `LocalAppData` / Unity / Unreal / RPG Maker / Ren'Py / GameMaker / 安装目录），
   可用 YAML 文件按 id 覆盖或追加。
3. **引擎雷达** —— 识别 Unity / Unreal / RPG Maker / Ren'Py / GameMaker 后套用对应的存档布局。

目录必须**真实存在且含存档类文件**才会被采用；拿不准就让你用探测模式或手动选，工具不会瞎猜。

### 探测模式：让游戏自己告诉你存档在哪

当规则库对某个游戏失效时：

1. 点「开始探测」，记录候选目录的文件指纹（只看时间戳和大小，不读内容）。
2. 去游戏里玩几分钟。
3. 再点「对比」，工具按新增/修改、存档文件扩展名、路径关键词打分，列出最像存档的目录。

探测全程只读元数据，不修改任何存档。

### 自动备份：按游戏行为触发

| 触发原因 | 时机 |
|---|---|
| `pre_launch` | 检测到游戏进程启动，先打一个保险点 |
| `exit` | 游戏进程退出后延迟 2 秒（留给引擎 flush 写完） |
| `idle` | 游戏运行中每 15 分钟一次（要求磁盘空闲、CPU < 30%、非全屏独占） |
| `post_change` | 非运行期间检测到文件变更 |
| `pre_restore` | 恢复前自动创建，可回到恢复前状态 |
| `manual` | 你手动点「立即备份」 |

变更检测分三层：`FileSystemWatcher`（64 KB 缓冲）→ size + mtime + 文件 ID 指纹预过滤 →
BLAKE3 内容比对。配合 3 秒去抖与 400 毫秒复检，**内容没变就不会产生快照**。

### 崩溃安全的上传队列

所有网络操作先写入本地 `outbox` 表再执行：

- 指数退避重试（1 秒 → … → 5 分钟，±20% 抖动）
- 启动时自动重放 `pending` / `inflight` 状态的任务
- 同一对象按哈希命名，重复执行幂等
- 进程在任何时刻被强杀，下次启动都能接着传

### 多设备同步与冲突处理

- 每 60 秒轮询一次远端 `index.json`（带 `If-None-Match`，命中 304 几乎零成本）
- 索引更新用 **ETag 乐观锁**，冲突时返回 412 并合并重试
- 两台设备各改一次 → **两个快照都完整保留**，索引标记为 `conflict`，
  界面并排展示两侧（设备 / 时间 / 文件数 / 大小 / 相对共同祖先的差异），可选一边为准
- 索引损坏时自动扫 `games/*/snapshots/*` 重建
- **跨机重新定位**：令牌在本机解析不到时标记「需重新定位」，
  由你把存档根映射到本机目录——工具绝不猜路径

### 恢复：两阶段 + 原子替换

点「恢复到此时点」时先给出计划（新增 / 覆盖 / 删除 + 字节数）让你确认，然后：

1. 自动打一个 `pre_restore` 快照；
2. 逐文件写入临时文件 → 校验哈希 → 原子替换；
3. 被占用的文件重试 3 次，仍失败则跳过并在结果里汇总。

### 保留策略（GFS）

默认：最近 20 个 + 每日 1×7 + 每周 1×4。手动创建的快照永不自动删除。
清理前会先给出「预计释放空间」让你确认。

### 其他

- **主页面海报卡片墙**：「我的游戏」页每张卡片以海报为主视觉。Steam 游戏可切换
  「竖屏长图」（`library_600x900`，2:3 大图）与「方形头图」（`header.jpg`，宽扁图），
  海报上浮着左右箭头与分页圆点，切换带淡入淡出过渡。
  图片 `Stretch="Uniform"` 等比缩放，只固定宽度、高度按原图比例自动算 ——
  既不变形也不留缝，更不会溢出卡片。选中的形态按游戏记在本机，下次打开仍是这个；
  没有海报时回退成方形首字母色块占位。
- **存档路径数据库（双源回退）**：内置两个公开数据源，按顺序回退：
  1. **Ludusavi Manifest**（主源）—— 5 万多个游戏的存档路径清单，
     托管在 GitHub Raw 上、不走 Cloudflare 挑战页，能用 Steam AppID 精确命中；
  2. **PCGamingWiki**（副源）—— 直接读页面里的存档路径模板。

  清单约 17 MB，首次使用点「刷新存档路径」时下载一次并缓存到本地，
  之后按 ETag 增量更新（服务端没变就直接用本地缓存）。
- **一键刷新存档路径**：详情页点「刷新存档路径」可重新抓取外部存档路径数据并
  覆盖已有的存档目录。刷新期间按钮禁用（防重复请求）并显示进度；
  成功后提示刷到的目录数，**任何一个源失败会自动换下一个源重试**，
  全部失败时**保留原有存档目录**并列出每个源的失败原因，绝不会清空你的配置。
- **诊断页**：存储占用、队列状态、设备、远端、最近 200 条日志、连接测试，
  以及导出**已脱敏**的诊断包（zip）。
- **便携模式**：exe 同级放 `portable.flag`，数据写到 `<exe>\data`，不写注册表。
- **主题**：6 种预设主题色（青 / 靛蓝 / 翠绿 / 琥珀 / 玫红 / 石墨）× 亮色 / 暗色 / 跟随系统。
- **托盘常驻**：关窗最小化到托盘，自动备份继续在后台跑；支持暂停 30 分钟 / 1 小时 / 直到重启。
- **单实例**：重复启动会唤起已有窗口。

---

## 安装

### 方式一：下载 Release 产物（推荐）

到 [Releases 页面](https://github.com/yangmaolei/Reload/releases) 下载 `Reload-1.1.0-win-x64.zip`：

1. 解压到任意目录；
2. 运行 `Reload.exe`；
3. 首次启动会打开三步向导：连接云端 → 选择游戏 → 确认。

> 该产物是 **win-x64 自包含单文件**（约 74 MB），无需另外安装 .NET 运行时。

#### ⚠️ 首次运行会弹出「Windows 已保护你的电脑」

这是 **Microsoft Defender SmartScreen** 的拦截提示，**不是病毒，也不是程序出错**。
原因是本项目**没有购买代码签名证书**，Windows 无法验证发布者身份
（提示里会显示「发布者：发布者未知」），而 SmartScreen 对「未知发布者 + 低下载量」的程序
默认会拦一道。

**处理办法（选一个）：**

1. **点「仍要运行」** —— 最直接。SmartScreen 记住这个文件后不会再提示。
2. **点「更多信息」→「仍要运行」** —— 如果窗口里有这个选项，走它更规范。
3. **用命令行解除下载阻止标记**（解压后执行一次即可，之后双击就不再提示）：

   ```powershell
   Unblock-File -Path .\Reload.exe
   ```

4. **添加排除项**（如果你打算长期用这台机器跑自动备份）：

   ```powershell
   Add-MpPreference -ExclusionPath "C:\你的解压目录"
   ```

> **怎么确认文件是干净的**：Release 页同时提供 `SHA256SUMS.txt`，
> 用 `Get-FileHash .\Reload.exe -Algorithm SHA256` 比对哈希值即可。
> 这比"看发布者是不是未知"可靠得多——即使文件被替换过，哈希也会对不上。

### 方式二：便携模式

把 `portable.flag` 放在 `Reload.exe` 同级目录，数据会存到 `<exe>\data`，
不写入注册表，界面中「开机自启」会自动禁用。适合放在移动硬盘上。

### 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809 及以上 / Windows 11 |
| 架构 | x64 |
| 磁盘 | 快照与对象占用的空间取决于你的存档总量（对象按内容去重） |
| 网络 | 仅同步时需要；本地备份可完全离线使用 |

---

## 快速上手

### 1. 配置 WebDAV

首次启动的向导第一步，或之后在「设置 → WebDAV 远端」里配置：

| 字段 | 说明 |
|---|---|
| **服务地址** | 完整 URL，例如 `https://dav.example.com/remote.php/dav/files/user` |
| **远程根目录** | 默认 `Reload`，会在该目录下建子目录存放快照 |
| **用户名 / 应用密码** | 建议用 WebDAV **应用密码**而非主账号密码 |
| **自签名证书 SHA-256 指纹** | 可选。只在自签名时填，且**只按指纹放行**，不会全局跳过证书校验 |

点「测试连接」会实际做一次 MKCOL + 写入探针 + 读回 + 删除的往返。

> **凭据安全**：密码用 Windows **DPAPI** 加密后存在本机 SQLite，任何日志里都不会出现明文。

### 2. 添加游戏

三种方式：

- **扫描游戏**（推荐）—— 自动列出本机已安装的游戏，勾选即可；
- **手动添加** —— 填游戏名 + 一个或多个存档目录；
- **探测模式** —— 规则库失效时的兜底。

### 3. 备份与恢复

- 游戏卡片上的「立即备份」手动触发一次快照；
- 平时由自动触发器接管（游戏退出、空闲等）；
- 快照时间线里点任意一条 → 「恢复到此时点」。

### 4. 日常运行

关窗即最小化到托盘，自动备份继续在后台跑。托盘菜单可查看状态、临时暂停、退出。
「开机自启」开启后，登录 Windows 即自动在托盘运行。

---

## 配置说明

所有配置都存在数据目录的 SQLite 里（`settings` 表），界面改写后立即生效。

### WebDAV

- **服务地址**：必须包含到用户目录的完整路径。
- **远程根目录**：默认 `Reload`。该目录不存在时会自动逐级创建（MKCOL）。
- **证书指纹**：留空表示走系统信任链；填了则只信任该指纹。
- **服务商适配**：自动选择
  - `generic` —— 单次 PUT
  - `nextcloud-chunked` —— 大文件分块上传（MKCOL + 分片 PUT + MOVE）
  - `rate-limited` —— 串行 + 限速（坚果云这类有频率限制的服务）

  任一步服务端不支持（如不支持 MOVE）都会自动退化为单次 PUT，结果一致，只是更费带宽。

### 自动化触发时机

| 参数 | 范围 | 默认 |
|---|---|---|
| 退出延迟 | 1–30 秒 | 2 秒 |
| 去抖 | 1–60 秒 | 3 秒 |
| 空闲间隔 | 5–60 分钟 | 15 分钟 |

保存后重启应用生效。

### 全局排除规则

每行一个通配符，对**所有游戏**生效，与单个游戏的排除规则叠加。以 `#` 开头的行是注释。

```
*.log
cache/*
*.bak
```

> 规则示例（仅作说明，实际请按自己的存档结构写）：
> `*.tmp`、`*.log`、`cache/*`、`crash/*`

### 外观

- **主题色**：青 / 靛蓝 / 翠绿 / 琥珀 / 玫红 / 石墨
- **外观模式**：亮色 / 暗色 / 跟随系统（跟随系统时读取 Windows 深色模式）

切换立即生效并持久化。

### 自定义路径规则

设置页「数据与设备 → 自定义存档规则文件」会显示规则文件的完整路径。
数据目录下的 `save-paths.sample.yaml` 是样例，复制成 `save-paths.yaml` 即可生效。

```yaml
version: 1
rules:
  # 某发行商把存档放在 Documents\MyCompany\<游戏名>
  - id: my-company
    description: MyCompany 的固定布局
    namePattern: "*"
    roots:
      - %DOCUMENTS%\MyCompany\{name}

  # 只对名字以 "Old" 开头的游戏生效
  - id: old-games
    description: 老游戏把存档放在安装目录
    namePattern: "Old*"
    roots:
      - {installDir}\saves
      - {installDir}\data
```

**可用占位符**

| 占位符 | 含义 |
|---|---|
| `%USERPROFILE%` | 用户目录 |
| `%APPDATA%` | 漫游应用数据 |
| `%LOCALAPPDATA%` | 本地应用数据 |
| `%DOCUMENTS%` | 文档 |
| `%SAVEDGAMES%` | 已保存的游戏 |
| `%STEAMUSERDATA%` | Steam `userdata` 目录（检测到时可用） |
| `%XBOXLIBRARY%` | Xbox 原生游戏库根目录（检测到时可用） |
| `{name}` | 游戏名 |
| `{installDir}` | 安装目录 |
| `{installDirName}` | 安装目录名 |

`roots` 里的路径**必须真实存在且含存档类文件**才会被采用。
按 `id` 匹配可覆盖同名内置规则，其他 id 则是追加。

### 存档目录的存储形式

存档路径不存绝对路径，而是存成**令牌 + 相对路径**（例如 `%APPDATA%\Studio\Game`），
所以换机器后能自动展开。解析不到就在界面上提示「重新定位」，
由你把令牌映射到本机目录（存在 `games.path_overrides`）。

---

## 使用示例

### 示例 1：Steam 游戏自动备份

Steam 游戏不需要手动配置：

1. 「扫描游戏」→ 勾选目标游戏 → 确认；
2. 工具从 `appmanifest_*.acf` 读出 AppId，定位到 `userdata/<id>/<appid>/remote`；
3. 点「立即备份」生成第一个快照；
4. 之后每次启动/退出该游戏都会自动快照并上传。

### 示例 2：让存档跟着换电脑走

1. 在 A 电脑配置好 WebDAV 并完成一次备份；
2. 在 B 电脑上装同一个游戏，用**同一组** WebDAV 凭据登录；
3. B 电脑每 60 秒轮询一次 `index.json`，发现没见过的新快照后自动下载并校验；
4. 若 B 电脑的存档目录和 A 不同 → 提示「重新定位」，把令牌映射到 B 的实际路径即可。

### 示例 3：两台设备都改过存档

1. A、B 都对同一个游戏做了备份，云端索引把这个游戏标记为 `conflict`；
2. 界面上「处理冲突」并排展示两侧快照；
3. 选一边为准，或把两侧都导出成 zip 自行比对。

> 冲突处理**不会删除任何一边**，事后仍可从历史快照里恢复。

### 示例 4：某个游戏规则库失效

1. 添加游戏时目录列表为空；
2. 点「探测存档目录」→ 「开始探测」；
3. 去游戏里玩几分钟 → 回到工具点「对比」；
4. 勾选得分最高的目录 → 完成。

### 示例 5：离线环境使用

不配置 WebDAV 也能用：本地快照、CAS 对象库、恢复、保留策略全部正常工作，
只是不会上传。托盘里仍可查看队列状态（始终为空）。

---

## 数据与目录布局

### 本地

默认在 `%LOCALAPPDATA%\Reload\`：

```
%LOCALAPPDATA%\Reload\
  gamesavesync.db          SQLite（WAL）：games / snapshots / snapshot_files / objects / outbox / devices / settings
  objects\<hh>\<hash>      内容寻址对象，写入后永不修改（hh = 哈希前两位）
  objects\tmp\*.part       暂存区，崩溃残留启动时自动清理
  manifests\<gameId>\<ts>-<sid>.json   清单的可读副本（权威源在 DB）
  logs\reload-*.log        Serilog 滚动日志
  covers\steam-<appid>-p.jpg 竖屏长图缓存（library_600x900）
  covers\steam-<appid>-s.jpg 方形头图缓存（header.jpg）
  manifest\ludusavi-manifest.yaml   存档路径数据库缓存（约 17 MB）
  save-paths.sample.yaml   自定义路径规则样例（复制成 save-paths.yaml 生效）
```

便携模式（exe 同级有 `portable.flag`）下，以上全部落在 `<exe>\data\`。

### 远端（WebDAV）

在配置的「远程根目录」之下：

```
<remote-root>/
  vault.json                                  版本 / 设备 / 加密开关
  objects/<hh>/<hash>                         不可变对象，重复 PUT 幂等
  games/<gameId>/snapshots/<ts>-<sid>.json    快照清单（临时名 PUT + MOVE）
  index.json                                  全局索引（乐观锁 + 设备注册）
  devices/<id>.json                           设备在线时间
```

---

## 常见问题

**Q：启动时弹出「Windows 已保护你的电脑」，是病毒吗？**
不是。这是 SmartScreen 因为程序**没有代码签名**而拦截的默认提示。
点「仍要运行」即可，或解压后执行一次 `Unblock-File -Path .\Reload.exe`。
想确认文件未被篡改，用 Release 页的 `SHA256SUMS.txt` 比对哈希。
详见[安装章节](#方式一下载-release-产物推荐)。

**Q：自动扫描没找到我的游戏？**
扫描只覆盖 Steam、Epic、GOG、Ubisoft、Xbox/微软商店这 5 个平台（另有手动添加）。
其它平台请用「手动添加」或「探测模式」。Xbox 侧要求游戏以 MSIX(GDK) 包安装，
或在 `C:\XboxGames` 这类原生库里存在。

**Q：Xbox 游戏的存档识别不准？**
云存档归属按「AUMID 包族名 → 应用 ID → Title ID」三级匹配。
主游戏包会被识别，DLC 附属包和安装目录已消失的残留包会被排除。
主机端本地存档只有在能按 Title ID 对上时才会挂载。

**Q：连不上 WebDAV？**
先点「测试连接」。常见原因：
- 服务地址填到了站点根目录而不是用户目录（Nextcloud 需要到 `remote.php/dav/files/<user>`）；
- 用了主账号密码而不是应用密码；
- 服务端不支持 `PROPFIND` 或 `MOVE`——工具会自动退化，但部分服务端需要手动开启。

**Q：为什么我的游戏只有一张海报，不能切换？**
只有 Steam 游戏有 CDN 主视觉（两种形态）。其它平台只有首字母色块占位。
另外某一种形态可能确实下不到（Steam 上有些游戏没提供头图），此时只剩一张可切。

**Q：点「刷新存档路径」提示失败，原来的存档目录还在吗？**
在。刷新失败（网络问题、数据源不可达、数据源里没有该游戏记录）时
**只提示错误，绝不清空你已有的存档目录**。提示里会写明每个数据源的失败原因。

**Q：刷新存档路径的数据从哪来？**
两个公开数据源按顺序回退：
1. **Ludusavi Manifest**（主源）—— 从 PCGamingWiki 编译出的公开清单，
   5 万多个游戏，走 GitHub Raw，不受 Cloudflare 挑战页影响，
   还能用 Steam AppID 精确命中（避开同名游戏）。
2. **PCGamingWiki**（副源）—— 直接读 wiki 页面里的存档路径模板。

任一源失败会自动换下一个；全部失败才报错。清单约 17 MB，第一次刷新时下载并缓存到
`%LOCALAPPDATA%\Reload\manifest\`，之后按 ETag 增量更新。

**Q：日志在哪？**
`%LOCALAPPDATA%\Reload\logs\` 下的滚动文件。应用内「诊断」页也能看最近 200 条，
并能导出已脱敏的诊断包。

**Q：删掉应用会丢存档吗？**
数据目录是独立的。删除程序不会删除 `gamesavesync.db` 和 `objects\`。
但**卸载前请确认远端已有完整快照**——本地快照不构成备份。

**Q：能加密存档吗？**
当前版本**没有**端到端加密。远端看到的是明文存档内容。
凭据（WebDAV 密码）用 DPAPI 加密存本地，传输安全取决于你用的 HTTPS。

**Q：多台设备会不会互相覆盖？**
不会。所有快照都保留，云端索引标记冲突而不是覆盖，冲突由你决定用哪一边。

**Q：占用空间会一直涨吗？**
快照按内容寻址去重，相同文件只存一份。GFS 策略默认保留最近 20 + 每日 7 + 每周 4，
在「清理旧快照」里可以预览并清理。

**Q：能多用户/多电脑共享同一个远端吗？**
可以。远端按 `RemoteRoot` 分目录，每台设备注册自己的 `devices/<id>.json`。
建议给不同用途配置不同的 `RemoteRoot`。

**Q：程序改名了，老数据还在吗？**
在。首次启动会自动把 `%LOCALAPPDATA%\GameSaveSync` 迁移到 `%LOCALAPPDATA%\Reload`，
开机自启的注册表项也会同步改名并清理旧值。

---

## 从源码构建

### 前置条件

- .NET 8 SDK（或更新的 SDK，能 target net8.0）
- Windows x64

### 构建与测试

```powershell
dotnet build .\GameSaveSync.sln -c Debug
dotnet test  .\GameSaveSync.sln
```

### 发布单文件产物

```powershell
dotnet publish .\src\GameSaveSync.App\GameSaveSync.App.csproj `
    -c Release -r win-x64 `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o .\publish\win-x64
```

产物：`publish\win-x64\Reload.exe`（自包含单文件，约 74 MB）。

> **发布前请先退出正在运行的 Reload.exe**，否则单文件打包器无法覆盖被占用的 exe。

### 调试钩子

设置环境变量 `RELOAD_OPEN_DIALOG=wizard` 可在启动时直接打开「扫描游戏」向导，
便于 UI 自动化测试。

---

## 工程结构

```
GameSaveSync.sln
├─ src/GameSaveSync.Core            核心引擎（net8.0，无 WPF / 无 Win32 API，可独立单测）
│   ├─ Abstractions/                IFileSystem、IHashService、IClock、IAppLogger
│   ├─ Configuration/               AppPaths —— 数据目录布局
│   ├─ Covers/                      封面缓存（Steam CDN + 本机缓存）
│   ├─ Detection/                   指纹缓存、稳定性门（400ms 复检 + 退避）
│   ├─ Diagnostics/                 环形缓冲日志、诊断包导出（脱敏）
│   ├─ Hashing/                     BLAKE3，64 位小写十六进制
│   ├─ Locator/                     平台发现、YAML 规则库、引擎识别、Xbox 定位器
│   ├─ Models/                      Game / SaveRoot / Snapshot / SnapshotManifest / 枚举
│   ├─ Online/                      存档路径数据源（Ludusavi 清单 / PCGamingWiki）
│   ├─ Paths/                       令牌展开与反向令牌化、Glob 匹配、存档根解析、路径覆盖
│   ├─ Probe/                       探测模式（指纹 → 运行后对比 → 打分）
│   ├─ Remote/                      index.json 模型与合并状态机、索引服务、轮询器、导入器
│   ├─ Restore/                     RestorePlan / RestoreService（两阶段 + 原子替换）
│   ├─ Retention/                   GFS 保留策略（预览 + 执行）
│   ├─ Scheduling/                  进程/文件事件抽象、SnapshotScheduler（触发器编排）
│   ├─ Security/                    ICredentialProtector
│   ├─ Snapshots/                   SnapshotService、清单序列化
│   ├─ Storage/                     SQLite（WAL）+ 仓储 + CAS 对象库 + outbox
│   ├─ Sync/                        IRemoteStore、OutboxProcessor、UploadService
│   └─ Theming/                     MD3 调色板
├─ src/GameSaveSync.Infrastructure  PhysicalFileSystem（\\?\ 长路径）、DPAPI、进程轮询+WMI、
│                                   目录监视、资源探针、开机自启、Steam/UWP 注册表、封面下载
├─ src/GameSaveSync.WebDav          WebDavClient（MKCOL 逐级 / 临时名 + MOVE / 证书固定 / 限流）、
│                                   WebDavRemoteStore、Providers（generic / nextcloud-chunked / rate-limited）
├─ src/GameSaveSync.App             WPF + MVVM + DI + 单实例 + 托盘 + Serilog
│                                   Themes/ 为 MD3 令牌与控件，Views/Pages 为卡片墙 / 诊断 / 设置，
│                                   WizardWindow / ProbeWindow 为向导与探测
└─ tests/GameSaveSync.Tests         xUnit：320 个用例，含进程内 WebDAV 服务器
                                    （ETag / 412 / PROPFIND / 分块）、真机监视与验收测试
```

### 设计约束

这些约束贯穿整个代码库，是这个工具能「放心交给后台自动跑」的基础：

1. **绝不用 mtime 判断内容是否变化** —— 一律 BLAKE3；mtime/size 只做稳定性判定与预过滤。
2. **绝不直接信事件** —— 读文件前后各 stat 一次，不稳定就重读（最多 3 次），仍不稳定则跳过。
3. **绝不产生半截对象** —— 先写 `objects/tmp/*.part` → 算哈希 → rename 进 CAS。
4. **绝不产生空快照** —— 目录被删/清空则标记「需重新定位」并报错。
5. **恢复前必打 `pre_restore` 快照**；目标文件先写临时文件 → 校验哈希 → 原子替换。
6. **路径全链路 `\\?\`**，不跟随 junction/symlink。
7. **凭据只用 DPAPI 加密存本地**，日志中不出现明文；自签名证书只按指纹放行。
8. **网络操作一律先入 outbox 再执行**，进程在任意点被杀都不丢待办。
9. **快照全局并发度 1**，后台路径线程优先级 `IDLE`，绝不抢游戏 IO。
10. **上行顺序对象 → 清单 → 索引**，前置依赖未就绪就让该项延后。
11. **任何合并分支都不删除快照**。
12. **不猜路径** —— 令牌解析不到就标记「需重新定位」，等用户显式映射。
13. **便携模式不写注册表**，系统集成显式禁用。

---

## 贡献

欢迎提交 Issue 和 Pull Request。

**提 Issue 前请先准备诊断包**：「诊断」页 → 「导出诊断包」，
生成的 zip 已自动脱敏（凭据、令牌、具体路径都会替换掉），可以直接附在 Issue 里。

**提交 PR 前请确认：**

```powershell
dotnet build .\GameSaveSync.sln -c Debug   # 编译零警告（TreatWarningsAsErrors 已开启）
dotnet test  .\GameSaveSync.sln              # 全部用例通过
```

**约定：**

- 遵循现有代码风格，保持 `Core` 层不依赖 WPF / Win32（否则无法独立单测）。
- 新功能尽量补测试；`tests/GameSaveSync.Tests/` 下按功能分组命名。
- 不要提交 `publish/`、`bin/`、`obj/` 等构建产物（`.gitignore` 已排除）。
- 提交信息用中文说明改动动机与影响范围。

---

## 许可证

本项目采用 **[MIT 许可证](LICENSE)**。

```
Copyright (c) 2026 Reload contributors
```

简而言之：你可以自由使用、修改、分发和商用本软件（包括闭源商用），
唯一的义务是在副本中保留版权声明和许可证声明。
本软件按「现状」提供，不附带任何明示或默示的担保。

**第三方组件**：本项目使用了 .NET 8（MIT）、CommunityToolkit.Mvvm（MIT）、
Microsoft.Extensions.DependencyInjection（MIT）、Serilog（Apache-2.0）、
BLAKE3 参考实现（CC0 / Apache-2.0 双许可）以及 Microsoft.Data.Sqlite（MIT）。
各组件的许可证详见其官方仓库。
