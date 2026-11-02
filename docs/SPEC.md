# Aurora（暂定名）— 需求与技术方案

> 版本：v0.1（2026-09-28）
> 说明：本文档是我（AI）根据你的决策 + 实际机器环境调研整理的，用来对齐目标。你随时可以改。

---

## 1. 一句话目标

做一个 **Windows 桌面软件**，用来替代 Unity Mod Manager（UMM），为《冰与火之舞》(A Dance of Fire and Ice, ADOFAI) 提供
**在线浏览/下载/安装/卸载/更新 mod** 的能力，并且界面现代化、全中文。

---

## 2. 需求清单（已确认）

### 2.1 核心
| 编号 | 需求 | 说明 |
|---|---|---|
| R1 | 独立软件 | 不是 UMM 的插件，是一个自己的 exe |
| R2 | 完全取代 UMM | 自动接管机器上已有的 UMM 安装；用户以后只开这一个软件 |
| R3 | 在线 mod 站 | 对接 `adofaitools.top`，内置 Aurora 专用公共 API key（用户也可填自己的） |
| R4 | 在线操作 | 浏览、搜索、下载、安装、卸载、更新 |
| R5 | 更新来源（双通道） | 资源站 + GitHub 都查，**取版本更高的那个**；GitHub 连不上时自动只用资源站；每个 mod 也可手动绑定 GitHub 仓库 |
| R6 | 本地 zip 安装 | 支持用户手动选一个本地 zip 安装（资源站可能不全） |
| R7 | 收藏 | 收藏 mod，单独查看 |
| R8 | 更新提醒 | **打开软件时**检查一次并提示 |
| R9 | 现代 UI | 替代 UMM 老旧的 WinForms 界面 |
| R10 | 中文界面 | 仅中文 |
| R11 | 游戏版本兼容提示 | 读取 mod 要求的游戏版本，提示可能不兼容 |

### 2.2 明确**不做**
- ❌ 一键更新全部（最新版不一定稳定，且 mod 常绑定游戏版本，交给用户自己判断）
- ❌ 支持其它游戏（只服务 ADOFAI）
- ❌ 后台常驻定时检查更新（只在打开时检查）

---

## 3. 实际环境事实（已在你机器上核实）

| 项 | 值 |
|---|---|
| 游戏安装路径 | `D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice` |
| Steam AppID | `977950` |
| 另一份残缺副本 | `C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice`（只有一个 Mods 文件夹，忽略） |
| 已装 UMM | **0.32.5（newman55 官方，MIT）** |
| UMM 注入方式 | DoorstopProxy：游戏根目录 `winhttp.dll` + `doorstop_config.ini` |
| 注入目标 | `A Dance of Fire and Ice_Data\Managed\UnityModManager\UnityModManager.dll` |
| loader 目录内容 | `UnityModManager.dll`、`0Harmony.dll`、`dnlib.dll`、`UnityModManager.xml`、`Config.xml`、`Params.xml`、`Log.txt` |
| mod 存放 | `<游戏目录>\Mods\<ModId>\`，里面有 `Info.json` |
| mod 安装本质 | 把 UMM 格式 zip 解压到 `Mods\<ModId>\`；卸载 = 删文件夹 |
| 现有在线仓库 | `https://ummrepo.ych.yqloss.net/`（YCH），**目前报 TLS 错误，实际是坏的** |

### Info.json 关键字段（决定功能可行性）
```jsonc
{
  "Id": "AdofaiTweaks",
  "DisplayName": "ADOFAI Tweaks",
  "Author": "PizzaLovers007 & CrackThrough",
  "Version": "2.9.3",
  "ManagerVersion": "0.22.14.0",     // 需要的 UMM 最低版本
  "GameVersion": "...",              // 适配的游戏版本  ← 用于兼容提示
  "Requirements": [ ... ],           // 依赖            ← 用于依赖提示
  "LoadAfter": [ ... ],              // 加载顺序
  "AssemblyName": "AdofaiTweaks.dll",
  "EntryMethod": "AdofaiTweaks.Startup.Load",
  "HomePage": "https://github.com/PizzaLovers007/AdofaiTweaks",
  "Repository": "https://raw.githubusercontent.com/PizzaLovers007/AdofaiTweaks/master/Repository.json"
}
```

> 💡 重要发现：`Repository` 字段本身就是"从 GitHub 更新"的标准机制。例如 AdofaiTweaks、CreplayMod 都已自带 GitHub 的 `Repository.json`。我们直接复用这个，比重新发明更可靠。

### GitHub 更新设计（两层）
1. 优先读 mod 自带的 `Repository`（一个 `Repository.json`，格式 `{"Releases":[{"Id","Version","DownloadUrl"}]}`）
2. 没有 `Repository` 时，退回 **GitHub Releases API**：取最新 Release → 自动挑最像 mod 的 zip（识别不准时允许用户手动选，并记住选择）
3. **raw 不通时改走 API**：`raw.githubusercontent.com` 在国内常被 DNS 污染，但 `api.github.com` 通常能通。
   所以当 `Repository.json` 取不到时，只要链接是 GitHub 域名，就自动改用 Releases API 再试一次。

### 更新来源优先级（R5 的核心）
资源站与 GitHub 是**互补**关系，不是二选一：GitHub 上作者首发，资源站国内快、但可能滞后。

```
每个已安装 mod：
  ① 查资源站  → 版本 A（国内稳定，可能滞后）
  ② 查 GitHub → 版本 B（作者首发，国内可能连不上）
  ③ 取版本更高的那个；版本相同优先资源站（下载更快，也不给 GitHub 添流量）
```

| 网络情况 | 表现 |
|---|---|
| GitHub 不可达 | **先做一次很短的连通性探测**（8 秒），不通就跳过所有 GitHub 检查（避免"N 个 mod × 超时"），只用资源站结果，界面只显示一条轻提示「GitHub 未连通」 |
| GitHub 可达 | 能看到作者刚发布的最新版，来源标注为 `GitHub` |
| 两边版本不同 | 状态行同时给出另一来源的版本，例如「可更新 → 2.6.0（GitHub）　·　资源站 2.5.0」 |

**下载兜底**：若主来源是 GitHub 但下载失败（代理挂了 / 下载域名被墙），自动改用资源站那一版安装，并明确告知用户，
不会让人卡在"有更新却装不上"。

**限流保护**：GitHub 未授权接口只有 60 次/小时，因此检查结果缓存 10 分钟（失败缓存 2 分钟）；
设置里也提供「同时查询 GitHub」开关，关掉后只查资源站，更快更安静。

---

## 4. 技术方案

### 4.1 技术栈
- **语言/框架**：C# / **.NET（WPF 桌面）**，Windows 11 Fluent 风格
- **UI 库**：WPF-UI（Fluent 主题，Mica 材质、现代控件）
- **MVVM**：CommunityToolkit.Mvvm
- **HTTP**：`HttpClient`（天然支持现代 TLS，顺便修复 UMM 的 TLS 报错）
- **JSON**：`System.Text.Json`
- **压缩**：`System.IO.Compression`
- **本地数据**：`%AppData%\AdofaiModManager\`（配置、收藏、API key、GitHub 绑定）

### 4.2 "完全取代 UMM" 怎么实现
不重写 loader，而是**复用 UMM 官方 MIT 的现成组件**：
- 打包官方 0.32.5 的 `UnityModManager.dll`(net35) + `0Harmony.dll` + `dnlib.dll` + `winhttp_x64.dll/x86.dll` + `doorstop_config.ini` 模板
- 部署 = 把 loader 复制到 `_Data\Managed\UnityModManager\`，把 `winhttp.dll` + `doorstop_config.ini` 放到游戏根目录
- 卸载 = 还原以上文件（并处理 UMM 曾经"Assembly 方式"留下的 `.original_` 备份，做干净还原）
- 首次运行：检测已有 UMM 安装并"接管"，不重复注入

### 4.3 主要页面（初稿）
1. **首页/在线 mod**：搜索 + 分类/排序 + mod 卡片（图标、名称、作者、版本、下载量）
2. **mod 详情**：介绍、更新日志、依赖、兼容性、下载/安装/更新按钮、绑定 GitHub、收藏
3. **已安装**：启用/禁用、卸载、更新、打开文件夹、版本回退
4. **收藏**
5. **设置**：游戏路径、API key、GitHub Token（可选，提升限额）、更新检查、关于/开源许可

---

## 5. 许可证与发布

| 组件 | 许可证 | 我们的义务 |
|---|---|---|
| UMM 0.32.5 | MIT（© 2018 newman55） | 保留版权 + 许可文本，注明"基于 UnityModManager" |
| dnlib / Harmony | MIT | 保留声明 |
| Ionic.Zip (DotNetZip) | MS-PL | 保留声明 |
| UnityDoorstop (winhttp) | MIT | 保留声明 |
| 本软件 | 由你决定（建议 MIT 或 GPL） | — |

- 发布前建议**换一个自己的名字**，避免与官方 UMM 混淆
- ⚠️ **必须提前拿到 `adofaitools.top` 站长对"第三方客户端调用接口 + 公开分发"的许可**，否则接口随时可能被封
- ✅ 已创建 **Aurora 专用公共 key**，随开源客户端内置；该 key 公开、可在后台撤销，用户也可填写自己的 key 覆盖（0.1.12 起）

---

## 6. 分阶段计划

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 环境搭建（.NET SDK 10.0.401 + Git 2.55） | ✅ 完成 |
| P1 | 项目骨架 + 现代 UI 外壳 + 游戏路径自动识别 | ✅ 完成 |
| P2 | mod 本地管理（列出已装 mod、本地 zip 安装、卸载、启用/禁用） | ✅ 完成 |
| P3 | loader 接管（检测/部署/卸载 UMM loader）+ 内核独立更新通道 + 回滚 | ✅ 完成 |
| P4 | GitHub 更新通道（Repository.json + Releases API + 手动绑定） | ✅ 完成 |
| P5 | 资源站对接（API key、浏览、搜索、下载安装、更新提醒） | ✅ 完成（更新提醒在 P6） |
| P6 | 收藏、兼容性提示、打磨、打包安装器、许可页 | ✅ 完成 |
| P6.5 | 首次使用向导（游戏目录 / 加载器 / 资源站，每步可跳过） | ✅ 完成 |
| P7 | Aurora 自身更新检查（GitHub Releases + 资源站工具库双通道） | ✅ 完成 |
| P8 | 发布 v0.1（GitHub 仓库 + Release，zip 与安装包两种形式） | ✅ 完成 |

### v0.1.1 计划（接入资源站）

**目标**：站长把 Aurora 上架到资源站后，让「资源站通道」真正可用、可靠。

配套文档：[`docs/SITE-LISTING.md`](SITE-LISTING.md)（给站长的对接说明，含命名与字段约定）。

要做的：

1. **显示名识别放宽**（站点 slug 是自动生成的 `tool-xxxxxxxx`，只能靠名字）
   - 现在：显示名包含 `Aurora`，或去空格后正好等于 `Aurora`
   - 增加：显示名**以 `Aurora` 开头**（如 `Aurora 模组管理器`）
2. **同时搜索 Mod 库**：万一站长把它放在 `Mod 资源库` 而不是工具库，也能识别
   （`/api/mods` 与 `/api/tools` 的字段结构一致，逻辑可复用）
3. **手动指定兜底**：设置里允许用户手动填写「Aurora 在资源站上的名字」，
   自动识别失败时仍可用（应对站长改名、或第三方镜像站）
4. **接入后实测**：确认 `资源站最新版本 > 当前版本` 时能正常提示，
   并在资源站版本更高时优先使用资源站的下载入口
5. 顺带：把「检查更新」的结果文案写清楚来源（`GitHub` / `资源站`）

不在这个版本做：自动下载安装、静默后台常驻检查。

### 修复记录（未发布，将随 0.1.1 一起）

2026-09-30 的真实使用暴露了三个问题，均已修复并真机验证：

| # | 现象 | 根因 | 修复 |
|---|---|---|---|
| 1 | 资源站上的 mod 更新了，客户端却没提示 | 站点侧匹配用**精确显示名**做键；站长把名字从 `AccurateJudgementBar` 改成 `AccurateJudgementBar (3.4.0 and 3.3.1)` 后立即失效 | 增加**规范化匹配**（去掉括号说明、空格、标点后比对）＋ slug ＋ 安装映射多键登记 |
| 2 | 更新后出现两个同名 mod（游戏里其实只生效一个） | 早期用 UMM / 手动解压装的 mod，**文件夹名 ≠ Info.json 的 Id**（实测 `out`、`Creplay v2.15`）；旧逻辑只删"与 Id 同名"的文件夹 | 安装前删除**所有 Id 相同**的文件夹，并保留旧 `Settings.xml` |
| 3 | 升级后卡片显示"v2.4.1 可更新到 2.4.1" | 更新结果**按 Id** 存，重复文件夹的检查结果互相覆盖 | 结果改按**文件夹名**存（`InstalledMod.UpdateKey`） |

另外：已安装页现在会检测"重复安装"并在卡片上给出提示，方便用户清理。

> 教训：凡是拿"站点数据"去匹配"本地数据"的地方，都不要依赖站长/作者不会改名；
> 安装映射（站点 id ↔ UMM Id）是最可靠的锚点，规范化匹配是兜底。

### 待办（不排期）

- **Mod 与游戏版本兼容性** —— 详见 [`docs/COMPATIBILITY.md`](COMPATIBILITY.md)（设计讨论稿）
  - 实测结论：`Info.json.GameVersion` 在 11 个已安装 mod 中声明数为 **0**，现有兼容判断实际上从未生效
  - 推荐主力：**读 UMM 运行日志**（`…_Data\Managed\UnityModManager\Log.txt`）——事实级信号、零维护，
    还能顺带发现重复安装与加载失败；其次是作者在 Release 说明里的声明（Creplay 就写得很规范）
  - 待与朋友、站长商议后再决定范围（含是否推动站点增加结构化「适配游戏版本」字段）
- GitHub 仓库的介绍（About / README 首屏文案）后续要再打磨一次
- 给 Aurora 自身也做一个「一键下载并更新」的自动更新（需要处理管理员权限与自我替换）

### 一键自动更新（v0.1.2 起）

发现新版本后，用户点「立即更新」即可自动下载并替换，完成后自动重启 —— 不再需要手动下载解压。

| 形态 | 做法 |
|---|---|
| **绿色版** | 下载压缩包 → 校验包内 `update.json` 的版本 → 解压到临时目录 → 生成一个 cmd 脚本，**等 Aurora 退出后**用 robocopy 覆盖程序目录并重启 |
| **安装版** | 下载安装包（GitHub 直接给 exe；资源站给合并包，从中取出 Setup.exe）→ **打开安装程序交给用户自己装**（不做静默安装）。带 `/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`，安装器会在需要时自动关闭 Aurora、装完自动重新打开 |

**发布包策略**

| 渠道 | 文件 | 说明 |
|---|---|---|
| GitHub Releases | 绿色版 zip + 安装包 exe（两个独立文件） | 各取所需，体积最小 |
| 资源站（工具库） | **一个** `Aurora-x.y.z-all.zip` | 站点一个版本只能挂一个文件，所以打成合并包 |

合并包结构（绿色版文件夹 + 安装包 + `update.json` + 说明）：

```
Aurora-0.1.2-all.zip
├── Aurora/               ← 绿色版：整个文件夹拖到想要的位置即可（也可覆盖更新）
├── Aurora-Setup-0.1.2.exe
├── update.json                       ← 版本号 + 绿色版文件夹名（客户端据此确认与定位）
└── 说明.txt
```

> 绿色版文件夹与压缩包都用产品全名，是为了让用户从资源站下载后能**直接拖拽**到目标位置。

**版本号写法不一致怎么办（2026-10-04 与站长/作者们讨论的结论）**

站点的 `latestVersion` 是**作者手动指定**的"最新版本"（`PATCH /creator/mods/{id}/latest-version`，
说明为"指定资源详情页默认展示和下载的版本"），所以客户端**不必硬比版本号大小**：

1. 两边写法一致 → 逐段比较；
2. 写法不一致（如本地 `26w40c` vs 站点 `26.5.1`）→ **身份判断**：拉一次站点详情（有 30 分钟缓存），
   把本地版本与站点版本列表逐个规范化比对：
   - 认得出且不是最新 → 可更新；
   - 认得出且正是最新 → 已是最新；
3. 连认都认不出 → 只比第一个数字段（宁可少报也不误报），并显示「版本号规则不同」。

**这对作者的建议**：把 mod 的 `Info.json` 版本号与资源站上填的版本号**保持一致**，
客户端就能 100% 准确判断（否则只能显示"规则不同、无法自动比较"）。

**版本号怎么显示（2026-10-04 用户反馈后统一）**

- **不擅自加 `v`**：版本号一律按作者写的原样显示。mod 的版本来自 `Info.json`，
  资源站侧来自 `latestVersion.versionId`，两者都**原样输出**
  （以前客户端统一拼成 `v26.5.1`，但作者普遍不写 `v`，所以显示成什么样由作者决定）。
- 显示口径：「已安装」列表显示 `Info.json` 的版本（就是实际装的那一版）；
  「在线 Mod」列表显示资源站上的版本；更新提示里给的是"远端版本"和来源。
- **对比口径统一走 `Services/VersionScheme`**：写法一致 → 逐段比较；写法不同 → 只比第一个数字段，
  能确定更新才提示。这样**在线列表的「可更新」角标也不会再误报**
  （`UpdateCenter` 里那套会发网络请求的"身份判断"只在「已安装」页的检查更新里用，
  避免列表里对每个 mod 都发一次详情请求）。
- 顺带修了「在线 Mod」列表"可更新置顶"一直没生效的问题（原来在 `OrderBy` 里顺手算状态，
  排序时用的还是上一轮的值）：现在先算状态、再排序。

**踩过的坑（都已修，写在这里避免以后再犯）**

1. **cmd 的代码页**：cmd 按系统 OEM 代码页读取批处理文件。脚本里带中文路径（如 `D:\umm优化\…`）时，
   必须用同一个代码页写入（`Encoding.GetEncoding(OEMCodePage)`，并先 `RegisterProvider(CodePagesEncodingProvider)`），
   否则 cmd 读到乱码路径、复制到错误位置。
2. **cmd 的换行**：必须是 CRLF。C# 原始字符串是 LF，直接写出去会让 `goto`/label 失效、脚本"跑空"。
3. **zip 的路径分隔符**：PowerShell 5.1 的 `Compress-Archive` 与 `ZipFile.CreateFromDirectory`
   都会用**反斜杠**（不符合 zip 规范）。打包脚本改为手动写条目、强制 `/`；解压侧也统一把 `\` 归一成 `/`。
4. **.NET Core 默认不带旧代码页**（936 等），要用先注册 `CodePagesEncodingProvider`。

**开发测试钩子**：设环境变量 `AMM_UPDATE_TEST_PACKAGE=<本地包路径>` 可让「检查更新」把该包当成新版本，
用于在不发版的情况下验证整条自动更新链路（版本号仍按包内 `update.json` 与当前版本比较，不会死循环）。

### Aurora 自身更新（P7 设计决策）
和 mod 一样走**双通道**，取版本号更高者：

| 通道 | 来源 | 说明 |
|---|---|---|
| ① GitHub Releases | `github.com/lishangi688/adofai-aurora` | 作者首发；国内可能连不上，失败即忽略 |
| ② 资源站「工具库」 | 站长已同意把 Aurora 放到资源站上 | 国内稳定；在 `/api/tools` 里按**显示名**认出自己（站点 slug 是自动生成的 `tool-xxxxxxxx`，不可用） |

- `设置 → 关于`：显示 `Aurora 版本 v0.1` + 「检查更新」按钮；有新版弹窗询问是否打开下载页。
- 启动时（若开启「打开软件时自动检查更新」）**静默检查**：有新版本才在窗口顶部显示一条提示条，
  带「打开发布页」和**「忽略此版本」**（记在 `SkippedAppVersion`，同一版本不再重复提示）。
- **不做自动替换程序**：安装版在 `Program Files` 下需要管理员权限，交给用户手动更新更稳妥；
  提示里会说明"安装版直接覆盖安装，绿色版解压替换"。

### 发布形式（决策）

- **安装包（默认）**：单个 `Aurora-Setup-x.y.z.exe`（Inno Setup 制作）。
  - 安装向导第一步可选：**为所有用户安装**（默认，`Program Files`，需要管理员）/ **仅为我安装**（用户目录，免管理员），两种都支持**自定义路径**。
  - 自带开始菜单快捷方式、可选桌面快捷方式、卸载程序、许可协议页。
- **绿色版**：`Aurora-vx.y.z-win-x64.zip`，解压即用（给不想安装的人）。
- 一键打包：`installer/build-installer.ps1`（发布 → 绿色版 zip → 安装包）。
- ⚠️ 因为默认可能装到 `Program Files`（只读），**程序不向安装目录写任何文件**：
  配置/收藏/更新源在 `%AppData%\AdofaiModManager`，**日志在 `%LocalAppData%\AdofaiModManager\logs`**。
- 安装包里的中文界面使用社区翻译 `ChineseSimplified.isl`（随安装器分发）。

> P1/P2 完成后已具备：现代 Fluent 界面、导航、设置页（游戏目录自动/手动、API 地址与 key）、
> 已安装页（扫描 `Mods`、启用/禁用走 `Params.xml`、本地 zip 安装、卸载、打开文件夹）。

### 关于「内核」与「外壳」（P3 设计决策）

| | 说明 | 更新方式 |
|---|---|---|
| 外壳 | 本软件（界面 / 在线站 / GitHub 更新 / mod 管理） | 自身版本更新：打开时检查 GitHub 新版本，提示并打开发布页 |
| 内核 | UMM loader（`UnityModManager.dll` + doorstop），负责游戏内注入与加载 mod | **独立更新**：内置稳定内核兜底 + 自建清单更新通道 + 支持回滚 |

- 内核更新清单（`manifest.json`）曾计划由我们自建托管；**现已移除**（设置项与「检查内核更新」按钮一并删除），
  内核更新改为两条路：① 从资源站「工具库」获取最新 UMM；② 用户导入本地下载的 UMM 压缩包。
- 升级内核前**自动备份当前内核**，支持一键回滚（应对游戏更新后内核出问题的场景）。
- 软件内置一个已验证的稳定内核，保证离线可用、随时可"安装/修复"。

#### 注入方式（决策）
- **只实现 DoorstopProxy**（放 `winhttp.dll` + `doorstop_config.ini`，由 UnityDoorstop 调用加载器），这是 UMM 官方推荐方式。
- **不实现 Assembly 方式**（用 dnlib 直接修改游戏本体托管 DLL）。原因：需要移植官方 `Console/Main.cs` 中数百行补丁逻辑，会改动游戏本体、有破坏风险，且 UMM 官方也说明该方式在游戏更新后会失效。
- 界面上「UMM 环境」页展示当前注入方式，不提供方式选择。

> P5 之前的所有工作都**不依赖**资源站，可以现在就开工。

---

## 7. 未决事项

1. ✅ 资源站 API 已实测通过，详见 [`docs/API.md`](API.md)（base URL：`https://www.adofaitools.top`，鉴权：`Authorization: Bearer`）
2. ⬜ 站长对第三方调用/公开分发的授权
3. ⬜ 软件正式名字（暂定 Aurora）
4. ⬜ 本软件自己的开源许可证与是否公开源码
5. ⬜ 分发方式（NSIS 安装包 / 绿色版 zip）
6. ⬜ 是否处理 BepInEx 遗留（游戏目录里有个空的 `BepInEx\plugins\CheryTools`）

---

## 8. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| 资源站接口没有文档 | 无法对接 | 用 key 实测接口 + 参考 ADOFAI-Tools 源码反推 |
| GitHub Release 命名不统一 | 自动识别挑错文件 | 允许手动指定并记住 |
| UMM 曾用 Assembly 方式**改过**游戏托管 DLL | 卸载不干净可能崩游戏 | 严格备份/还原，先只做 Doorstop 接管 |
| 游戏更新导致 mod 失效 | 用户困惑 | 兼容性提示 + 保留旧版本回退 |
| 公开发布未授权调用接口 | 被站长封禁 | 发布前取得授权 |
