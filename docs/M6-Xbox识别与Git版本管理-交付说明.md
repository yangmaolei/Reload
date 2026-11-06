# M6 交付说明：Xbox 游戏识别 + Git 版本管理

日期：2026-11-06
范围：① 修复并扩展 Xbox / 微软商店游戏识别；② 接入 Git 版本管理

---

## 一、Xbox 游戏识别

### 1.1 排查结论：为什么只认得出 Steam

原实现里 Xbox 相关能力只有一个 `UwpLocator`，它有**两处致命缺陷**，导致 Xbox 游戏**一个都识别不出来**：

| # | 缺陷 | 后果 |
|---|---|---|
| A | `WindowsLocatorEnvironment.GetUwpPackages()` 从注册表读到的键名是**完整包名**（`Name_Version_Arch__PublisherId`），却被当成**包族名**存进 `UwpPackage.PackageFamilyName` | `UwpLocator` 拼出的 `%LOCALAPPDATA%\Packages\<完整包名>\SystemAppData\wgs` **永远不存在**，存档目录 100% 落空 |
| B | `ShouldSkip()` 把所有 `Microsoft.*` 前缀的包全部跳过 | Xbox 第一方游戏（Forza 的 `Microsoft.ForteBaseGame`）被误杀 |

> 顺带验证：修复前本机 `%LOCALAPPDATA%\Packages` 下有 5 个游戏包
> （Forza Horizon 6 / Hollow Knight: Silksong / My Friend Peppa Pig / Buckshot Roulette / Denshattack!），
> 旧逻辑一个都命中不了。

此外还有两个"设计上就没覆盖 Xbox"的空白：

- **Xbox 原生游戏库**（Gaming Services 装的 PC 版，主机游戏在 PC 上运行）位于 `<盘符>\XboxGames\`，
  云存档在 `<库根>\GameSave\pgs\`，旧实现完全没有这个来源。
- **Xbox 主机端游戏在 PC 上的存档**（`wgs` 容器）与**PC 端原生游戏**（`pgs` 云存档）是两套不同的存放机制。

### 1.2 实现思路

新增 `Core/Locator/XboxLocator.cs`，覆盖两类来源，并解决"存档归属"问题：

**来源 ①：MSIX(GDK) 游戏包**（Xbox 应用 / Game Pass / 微软商店 / 主机游戏跑在 PC 上）

判定"这是不是一个游戏"用三个**硬信号**（普通商店应用一个都不会有）：

| 信号 | 来源 | 例 |
|---|---|---|
| `Executable="GameLaunchHelper.exe"` | `AppxManifest.xml` 的 `Application` 节点 | 微软商店游戏的统一启动器 |
| `ms-xbl-<TitleId>` 协议扩展 | 同上，`Extensions/uap:Protocol` | `ms-xbl-636f5860`（Silksong 的 Xbox Title ID） |
| `MicrosoftGame.Config` | 包根目录 | GDK 原生游戏（Forza） |

同时**排除**两类假阳性：

- **附属包（DLC/扩展包）**：清单 `Dependencies` 里声明了对主游戏包的依赖（本体游戏只依赖 VCLibs 等运行时框架）。
- **已卸载的残留包**：安装目录已不存在（注册表里还留着条目，侧载包卸载后常见）。

存档目录：`<LocalAppData>\Packages\<PFN>\SystemAppData\wgs`（云存档容器）+ `<PFN>\LocalState`（部分游戏）。

**来源 ②：Xbox 原生游戏库**（`<盘符>\XboxGames\<游戏名>\`）

- 遍历固定盘找 `XboxGames` 目录（Xbox 应用允许把游戏装到别的盘）。
- 云存档在 `<库根>\GameSave\pgs\<xuid>_<titleId>\` 与 `<库根>\GameSave\wgs\<titleId>\`。

**云存档归属（本方案最关键的一环）**

`pgs` 目录里的元数据 JSON 记录了存档属于谁，靠三级匹配依次归属：

1. **AUMID 的包族名**（`Microsoft.ForteBaseGame_8wekyb3d8bbwe`）→ 精确命中包；
2. **AUMID 的应用 ID**（`Forzahorizon6`）→ 命中清单的 `Application/@Id`；
3. **Title ID**（`636F5860`）→ 命中 `GameSave\wgs\<titleId>` 容器目录名。

另外，**原生安装但没有 MSIX 包**的游戏，用 AUMID 的应用 ID 与游戏名/目录名归一化后匹配。

> 元数据 JSON 的文件名不固定（目录叫 `u_<xuid>_<titleId>`，文件却叫 `<xuid>_<titleId>.json`），
> 所以实现是"扫目录里所有 json，取第一个带 `Context` 的"，不依赖文件名约定。

### 1.3 涉及的关键文件

| 文件 | 改动 |
|---|---|
| `src/GameSaveSync.Core/Locator/XboxLocator.cs` | **新增**。Xbox/微软商店游戏定位器（MSIX 包 + 原生库 + 云存档归属） |
| `src/GameSaveSync.Core/Locator/DiscoveryModels.cs` | `UwpPackage` 扩展 `PackageFullName` / `IsGame`；`ILocatorEnvironment` 新增 `GetXboxLibraryRoots()`；`PlatformLabel` 增加 "Xbox" |
| `src/GameSaveSync.Core/Locator/GameDiscoveryService.cs` | `DiscoveryOptions.IncludeUwp` → `IncludeXbox`；扫描流程改调 `XboxLocator` |
| `src/GameSaveSync.Core/Locator/PlatformLocators.cs` | 删除永远返回空的 `UwpLocator`（游戏包统一走 Xbox 通道） |
| `src/GameSaveSync.Core/Locator/SavePathRules.cs` | 平台定位器给出的存档目录即使**暂时为空**也保留（游戏首次存档前即可登记） |
| `src/GameSaveSync.Core/Models/Enums.cs` | `GameSource` 新增 `Xbox = 6`（追加在末尾，不影响存量数据） |
| `src/GameSaveSync.Infrastructure/WindowsLocatorEnvironment.cs` | 包名派生、包族名/完整包名分离、清单取真实显示名、HKCU+HKLM 两处包仓库、`GetXboxLibraryRoots()`、`%XBOXLIBRARY%` 令牌 |
| `src/GameSaveSync.App/ViewModels/GameListItemViewModel.cs` | 卡片平台标签增加 "Xbox" |
| `src/GameSaveSync.App/ViewModels/WizardViewModel.cs` | 扫描状态文案：`Steam / Epic / GOG / Ubisoft / Xbox…` |
| `src/GameSaveSync.App/Views/Pages/GamesPage.xaml` | 空状态提示文案同步 |
| `tests/GameSaveSync.Tests/M6XboxTests.cs` | **新增** 22 条用例 |
| `tests/GameSaveSync.Tests/M4LocatorTests.cs` | 原 UWP 用例改为 Xbox 通道 |
| `README.md` | 平台发现说明同步 |

### 1.4 验证方式

**单元测试（22 条新增）**

```bash
cd C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\GameSaveSync
"C:\Users\yang\.dotnet-sdk\sdk8\dotnet.exe" test GameSaveSync.sln --filter "FullyQualifiedName~M6XboxTests"
```

覆盖：包名派生（4 组）、MSIX 游戏识别、普通商店应用排除、Xbox 运行时组件排除、
GDK 游戏识别、空存档目录保留、pgs 按包族名归属、pgs 按应用 ID 归属、wgs 按 Title ID 归属、
原生库游戏识别、`GameSave` 目录排除、与 MSIX 包去重、原生独占游戏拿到自己的云存档、
附属包排除、未安装残留包排除、发现服务开关两条。

**真机验证（本机实测）**

用真实 `WindowsLocatorEnvironment` 跑一遍定位器：

```
Xbox 库根：C:\XboxGames
识别到 5 个 Xbox 游戏：
  - Buckshot Roulette      [Xbox] engine=Unity   root=…\Packages\CRITICALREFLEX.BuckshotRoulette_20ymd9g5r2c54\SystemAppData\wgs
  - Forza Horizon 6        [Xbox] engine=Unknown root=…\Packages\Microsoft.ForteBaseGame_8wekyb3d8bbwe\SystemAppData\wgs
                                                     root=C:\XboxGames\GameSave\pgs\u_2535467423605544_16D460
  - My Friend Peppa Pig    [Xbox] engine=Unity   root=…\Packages\OutrightGames.MyFriendPeppaPig_bnrbc155f0j86\SystemAppData\wgs
  - Denshattack!           [Xbox] engine=Unreal  root=…\Packages\SoldOutSalesMarketing.Denshattack_7sns490bw3vda\SystemAppData\wgs
  - Hollow Knight: Silksong[Xbox] engine=Unity   root=…\Packages\TeamCherry.HollowKnightSilksong_y4jvztpgccj42\SystemAppData\wgs
```

同机上 20 个 `FH6-*` DLC 附属包与 1 个残留包均被正确排除。

**界面验证**：应用内「游戏」页 → 「扫描游戏」→ 向导第二步应列出上述 5 个 Xbox 游戏，
平台标签显示 "Xbox"，存档目录已自动填好。

**回归**：全量 `320/320` 全绿（修复前 298，新增 22），编译 0 warning。

---

## 二、Git 版本管理

### 2.1 实现思路

在**项目根目录**（`GameSaveSync/`，含 `.sln` 的那一层）初始化仓库，而不是外层的工作目录——
外层只有截图和临时产物，入库无意义。

- 分支：`main`（`git init -b main`）。
- 身份：仓库级 `user.name=yang` / `user.email=yang@localhost`。
  本机没有全局 Git 身份，所以写在仓库配置里（`git config user.name …`），不影响其他仓库；
  **需要换成你自己的名字时执行** `git config user.name "你的名字"` / `git config user.email "你的邮箱"`。
- `core.autocrlf=false` + `.gitattributes`：仓库内一律 LF，避免跨平台协作时整文件变更。

### 2.2 .gitignore 覆盖范围

| 类别 | 规则 |
|---|---|
| 构建产物 | `[Bb]in/`、`[Oo]bj/`、`publish/`、`artifacts/`、`*.dll`、`*.pdb`、`*.exe`、`*.nupkg` |
| 依赖目录 | `packages/`、NuGet 包缓存（`.nupkg`）、`obj/project.assets.json` 随 obj 一起忽略 |
| IDE | `.vs/`、`.vscode/`、`.idea/`、`*.suo`、`*.user`、`*.sln.docstates` |
| 测试产物 | `TestResults/`、`*.trx`、`*.coverage`、`coverage*.xml` |
| 运行期数据 | `/data/`（便携模式）、`*.db`（含 `-wal`/`-shm`）、`/covers/`、`/logs/`、`/diagnostics/`、`*.log` |
| 密钥 | `*.pfx`、`*.p12`、`*.pem`、`*.key`、`.env`、`secrets.json` |
| 大体积资源 | `*.png`、`*.jpg`、`*.mp4`（`src/**/Assets/**` 与 `docs/**` 用 `!` 单独保留） |
| 其它 | `*.bak`、`*.tmp`、`Thumbs.db`、`desktop.ini` |

`docs/test-run.log` 用 `!docs/*.log` 保留（它是交付物，不是运行期日志）。

> ⚠ **踩过的坑（已修复，提交 `3c8223b`）**
> 首版 `.gitignore` 写的是不锚定的 `diagnostics/` 与 `covers/`。Windows 上 git 默认
> `core.ignorecase=true`，目录名匹配**不区分大小写**，于是
> `src/GameSaveSync.Core/Diagnostics/` 和 `src/GameSaveSync.Core/Covers/`
> 两个**源码目录**被整目录忽略，`CoverService.cs`、`Logging.cs`、`DiagnosticsService.cs`
> 三个文件没有入库——**本地构建照常通过，但从仓库克隆后构建直接失败（59 个错误，缺 `IClock`/`IAppLogger`）**。
>
> 修复：所有运行期数据目录改用 `/` 锚定到仓库根（`/diagnostics/`、`/covers/`…），
> 并补回被误删的 `*.db` / `*.sqlite` 规则。
>
> **教训：`.gitignore` 必须用"克隆后构建"来验证，光看 `git status` 是不够的**
> （`git status` 只会安静地少列文件，不会报错）。

### 2.3 首次提交

```
733fb40  feat(xbox): 支持 Xbox / 微软商店游戏识别，并接入 Git 版本管理
f81f31f  docs(m6): 补充 Xbox 识别与 Git 版本管理交付说明，更新向导副标题文案
3c8223b  fix(git): 修正 .gitignore 误伤源码目录
```

- 跟踪文件 **170** 个（源码 + 文档 + 解决方案 + 配置文件）。
- `.git` 目录仅 **约 1 MB**（对比工作区 389 MB）——`publish/`（141 MB）、各项目 `bin/obj`（248 MB）全部排除。
- 工作区状态干净（`git status` 无输出）。

### 2.4 涉及的文件

| 文件 | 说明 |
|---|---|
| `.gitignore` | 新增，忽略规则（见上表） |
| `.gitattributes` | 新增，统一 LF 换行 |
| `.git/config` | 仓库级身份、分支名 `main`、`core.autocrlf=false` |

### 2.5 验证方式

```bash
cd C:\Users\yang\WorkBuddy\2026-10-30-20-41-20\GameSaveSync

git log --oneline          # 首次提交
git status                 # 工作区应干净
git ls-files | wc -l       # 170
git status --ignored --short | grep '^!!'   # publish/、各 bin/obj 应在此列
```

自检已做：手工在 `bin/`、`publish/`、`data/`、`logs/`、根目录造 `secret.pfx`、`screenshot.png`，
`git status` 全部无输出（正确忽略），随后已清理。

**最有价值的一项验证——克隆后重建**（务必做，`git status` 发现不了漏跟踪的文件）：

```bash
git clone <repo> /tmp/reload-clone-check
cd /tmp/reload-clone-check
dotnet build GameSaveSync.sln      # 期望：0 warning 0 error
dotnet test  GameSaveSync.sln      # 期望：320/320 通过
```

已实测通过（这正是发现 `.gitignore` 误伤 `Diagnostics/`、`Covers/` 两个源码目录的手段）。

---

## 三、已知限制

1. **Xbox 主机的本地存档不完整**：`GameSave\wgs` 只在能按 Title ID 对上时才会挂到游戏上；
   跨设备同步仍以 MSIX 包的 `wgs` 容器为准（Xbox 官方云同步本身就是这一套）。
2. **`GameSource.Uwp` 仍在枚举里但不再产出**：为兼容存量数据库保留，值不变。
3. **游戏名来自清单/注册表**：个别包显示名是 `ms-resource:` 资源引用时会退回包族名（较难看但不影响存档定位）。
4. **Xbox 库根只扫固定盘与可移动盘**：网络盘上的 `XboxGames` 不覆盖（Xbox 应用本身也不支持）。
5. **引擎识别对 GDK 原生游戏无效**：Forza 这类原生游戏会显示"未知引擎"，规则库对它不生效（其存档在 wgs，不依赖引擎规则）。
6. **`.gitignore` 未忽略 `docs/*.zip`**：诊断包导出若放在 `docs/` 下需手动确认。
