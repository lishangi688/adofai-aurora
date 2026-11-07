# 代码审查：v0.1.12.2（main @ `d7b0ca4`）

> 审查时间：2026-11-08
> 范围：`src/AdofaiModManager` 全部 C# 源码（服务层、来源适配器、页面），结合 README、`docs/releases/` 各版本更新日志、PR #1～PR #4 的讨论记录。
>
> 说明：下文的问题编号统一写作 F1～F17，以免和 GitHub 上的 PR / Issue 编号（#1、#2 …）混淆。
> 起因：0.1.12.1 被反馈「打开 Mod 文件夹」跳到「文档」，0.1.12.2 的修复没有找对原因，最后由 PR #4 修好。这说明有些问题光看现象很难定位，需要再系统地过一遍代码。

本文档只记录问题和建议，**这个 PR 不改动任何程序代码**，修复会按优先级另外提交。

## 结论速览

| 编号 | 级别 | 问题 | 位置 |
|---|---|---|---|
| F1 | 🔴 高 | 更新中断后，下次扫描会删掉旧版本的唯一备份（v0.1.10 的覆盖安装改动造成的回归） | `ModService.cs` |
| F2 | 🔴 高 | 游戏目录里混用 `/` 和 `\` 的根源在 Steam 定位，PR #4 只修了 Explorer 这一处 | `SteamGameLocator.cs` |
| F3 | 🔴 高 | Aurora 自更新和 UMM 内核都按名字模糊匹配资源站条目，而且下载后不做任何校验 | `AppUpdateService.cs` / `KernelBootstrapper.cs` |
| F4 | 🟠 中 | 部署加载器时会覆盖 BepInEx 的 `winhttp.dll` / `doorstop_config.ini`，而且没有备份 | `LoaderService.cs` |
| F5 | 🟠 中 | Markdown 的 `<scheme:...>` 自动链接没有限制协议，点击后会直接交给 ShellExecute | `MarkdownViewer.xaml.cs` |
| F6 | 🟠 中 | 接口缓存的键不含站点地址，默认站点和自定义站点的列表、详情会串 | `AdofaiToolsClient.cs` |
| F7 | 🟠 中 | 「更新」不校验装进来的 mod Id，名字匹配错时会装成另一个 mod | `InstalledModsPage.xaml.cs` / `ModService.cs` |
| F8 | 🟠 中 | `Params.xml` 损坏时会被静默重建并覆盖，写入也不是原子的 | `UmmParamsService.cs` |
| F9 | 🟠 中 | 部署内核时先复制文件、后检查注入组件，内核包不完整时会留下新旧混杂的加载器 | `LoaderService.cs` |
| F10 | 🟡 低 | PR #4 合并时列的几条 follow-up 还没做 | `PathToExplorer.cs` 等 |
| F11 | 🟡 低 | 收藏 / 安装映射 / 更新源文件损坏时会被静默清空 | `FavoritesStore.cs` 等 |
| F12 | 🟡 低 | 同一分钟内导出两次诊断包，第二次必定失败 | `SettingsPage.xaml.cs` |
| F13 | 🟡 低 | 安装流程没有全局互斥，目前只是碰巧被 UI 线程串行化 | 各页面 |
| F14 | 🟡 低 | 远端数据里的 URL 会被当作本地路径尝试（`File.Exists(url)`） | `KernelService.cs` / `AppSelfUpdater.cs` |
| F15 | 🟡 低 | 绿色版更新脚本忽略 robocopy 失败，照常启动新版本 | `AppSelfUpdater.cs` |
| F16 | ⚪ 流程 | 没有 CI，PR #2 的安装测试工程因为 `.gitignore` 从来没进过仓库 | `.gitignore` |
| F17 | ⚪ 文档 | 0.1.12.2 更新日志写的根因不准确；PR #4 的修复还没发版 | `docs/releases/v0.1.12.2.md` |

其中 F1、F2 已经用真实源码写了小程序复现（见下文），F12 也做了验证。其余各项是逐行阅读代码得出的结论。

---

## 🔴 F1. 更新中断后会删掉旧版本的唯一备份

**位置**：`Services/ModService.cs:222-251`（步骤 ③），`Services/ModService.cs:448-454`（`RecoverOldBackup`）

**来龙去脉**

- v0.1.9（`fedc130`，PR #2 收尾）加了「`.aurora-old-*` 不能直接删」的保护。当时步骤 ③ 是**把完整的新版本目录改名就位**，这是原子操作，所以只要 `Mods/<Id>` 存在，它一定是完整的，备份可以安全删除。
- v0.1.10（`2ff90f3`「mod 更新改为覆盖安装」）把步骤 ③ 改成：先 `Directory.CreateDirectory(targetDir)`，再把旧版本 `OverlayDirectory` **逐个文件复制**过去，最后用新包覆盖。从这一版开始，`Mods/<Id>` 存在**已经不能说明它是完整的**。
- 但 `RecoverOldBackup` 的判断没有跟着改：

  ```csharp
  if (Directory.Exists(target))
  {
      // 已经有正常版本了，这份备份没用了
      TryDeleteDirectory(backupDir);
      return;
  }
  ```

**后果**：如果在复制过程中被强杀、断电，或者用户在 0.1.12.1 的确认框里选了「是」，下次打开「已安装」页或者启动时做更新检查（两处都会调用 `Scan()` → `CleanupTempFolders()`），就会把**这个 mod 唯一完整的副本删掉**，只留下复制了一半的目录。半截目录里如果还没有 `Info.json`，这个 mod 连列表里都不会出现，用户的 `Settings.xml` 也一并丢失。

失败回滚的路径也有同样的问题：第 248 行的 `TryDeleteDirectory(targetDir)` 如果因为文件被占用只删掉一部分，第 249 行 `Restore` 往回挪就会失败，备份留在原地，下次扫描时同样会被删掉。

**复现**（直接编译 `ModService.cs` 等真实源码，模拟复制到一半时的磁盘状态）：

```text
before scan: backup exists = True
after scan:  backup exists = False
Foo folder files: Foo.dll          ← 只剩复制了一半的目录，没有 Info.json
mods listed: 0                     ← mod 从列表里消失了
```

**建议**：把「合并旧内容 + 覆盖新包」整个放到暂存目录里完成，最后仍然用**一次改名**就位：

1. 解压新包到 `.aurora-new-*`；
2. 把旧版本整体挪到 `.aurora-old-*`；
3. **在 `.aurora-new-*` 里**合并：先复制旧版本的内容，再用新包的内容覆盖；
4. `Directory.Move(.aurora-new-*, Mods/<Id>)`，这一步是原子的；
5. 最后删除 `.aurora-old-*`。

这样「`Mods/<Id>` 存在就说明它是完整的」这个前提重新成立，`RecoverOldBackup` 不用改。如果暂时不想动流程，至少要让 `RecoverOldBackup` 在 `target` 已存在时**保留备份**（例如改名成 `.aurora-recovered-*` 并在界面上提示），不能直接删。

> **复审补充（后续项）：** 上面「`RecoverOldBackup` 不用改」的判断不完整。新流程只保证**以后**的正式目录是完整的；已经在 0.1.10～0.1.12.2 上更新中断过的用户，磁盘上仍可能是「半个正式目录 + 唯一完整的备份」，升级后第一次扫描照样会删掉备份。所以还需要「**只有确认目标完整才删备份**」：新流程写入完成标记，没有标记的目标不能当作完整处理。这一项在 PR #6 的首版修复中遗漏，已在其后续提交中补上（含回归测试）。

---

## 🔴 F2. 游戏目录混用分隔符的根源在 Steam 定位

**位置**：`Services/SteamGameLocator.cs:336-344`、`294-297`；写入设置的位置有 `Services/AppServices.cs:139`、`Views/Dialogs/FirstRunWizard.xaml.cs:36-41`、`Views/Pages/SettingsPage.xaml.cs:212`

PR #4 的复现说明里提到，把 GamePath 设成 `D:/SteamLibrary/...` 就会触发问题。实际上用户不需要手动输入这样的路径，**Aurora 自己就会生成**：

- `HKCU\Software\Valve\Steam\SteamPath` 这个注册表值本身就是正斜杠写法，例如 `c:/program files (x86)/steam`。
- `Add()` 只做了 `Trim().TrimEnd('\\', '/')`，没有规范化，所以它和硬编码的 `C:\Program Files (x86)\Steam` 被当成**两个不同的库**。
- `Path.Combine(...)` 之后得到 `c:/program files (x86)/steam\steamapps\common\A Dance of Fire and Ice`。
- 当游戏只装在 Steam 默认库时，这两个候选目录分数相同，`ThenBy(OrdinalIgnoreCase)` 里 `/`（0x2F）排在 `\`（0x5C）前面，结果**混合写法的路径胜出**。首次向导和 `AppServices.Initialize` 会把它写进 `settings.json`。

**复现**（在一台游戏装在默认库的机器上编译真实的 `SteamGameLocator.cs`）：

```text
lib: c:/program files (x86)/steam
lib: C:\Program Files (x86)\Steam
candidate: c:/program files (x86)/steam\steamapps\common\A Dance of Fire and Ice
candidate: C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice
tie winner: c:/program files (x86)/steam\steamapps\common\A Dance of Fire and Ice
```

PR #4 在 `PathToExplorer` 里把 `/` 换成了 `\`，修好了 Explorer 这个最显眼的症状。但这种路径还会出现在日志、诊断包、路径比较（例如 `AppServices.Initialize` 里的 `string.Equals(best, configured)`）以及以后任何把路径交给外部程序的地方。

**建议**：

- 在 `SteamGameLocator.Add()` 里用 `Path.GetFullPath(path.Trim())` 规范化，再去重；
- 写入 `GamePath` 时统一规范化（向导、设置页、自动检测三处），读取旧配置时也规范化一次，这样已经存了混合路径的老用户也能自动纠正；
- 这样 `PathToExplorer` 里的替换就只是兜底，不再是唯一的防线。

---

## 🔴 F3. 自更新与内核下载：按名字模糊匹配，而且不做校验

**位置**：

- `Services/AppUpdateService.cs:370-379`（`IsSelf`）、`300-301`、`349-355`
- `Services/AppSelfUpdater.cs:69-130`（下载后直接运行安装包，或解压后覆盖程序目录）
- `Services/KernelBootstrapper.cs:273-275`（`FindLatestSiteKernelAsync`）、`214-222`
- `Services/KernelService.cs:243`（只有清单提供 sha256 时才校验，资源站路径从来不提供）

**问题**

- 认出 Aurora 自己的条件是工具库里 `DisplayName` **包含** "Aurora"，取第一页第一个匹配项。资源站上任何名字里带 Aurora、版本号更高的工具，都会让所有用户看到「发现新版本」。点「立即更新」后：安装版会**直接运行**下载到的 exe；绿色版会用它**覆盖 Aurora 的程序目录并启动**。
- 内核也一样：工具名**包含** "UnityModManager"、版本号比内置的高，「安装 / 修复加载器」的默认选项和首次向导就会下载它，并把其中的 `winhttp.dll` 部署进游戏目录，也就是**注入游戏进程**，整个过程中没有确认环节。
- 两条路径都没有哈希或签名校验。另外，如果用户把「默认站点」改成别的地址，那个站点的工具库也能给 Aurora 推送更新。

这条风险有多大，取决于资源站工具库的上传审核机制。但无论如何，「名字里包含某个词」就作为可执行文件的信任依据，标准都太低了。

**建议**（按投入从小到大）：

1. 匹配时固定资源站上的 **slug / 工具 Id**，不再用名字包含；
2. 自更新只接受 `IsAdofaiToolsHost` 判定为 ADOFAI Tools 的站点；自定义的「默认站点」不参与 Aurora 自身的更新；
3. 每次发版在 GitHub Release 里附上 `SHA256SUMS`，下载后先校验再运行或解压（资源站的包也对照 GitHub 上的哈希）；
4. 内核：内置一份已知版本的哈希白名单；不在白名单里的内核，部署前弹窗确认；
5. 长期：给安装包和程序做 Authenticode 签名，更新前校验签名。

---

## 🟠 F4. 部署加载器会覆盖 BepInEx，而且没有备份

**位置**：`Services/LoaderService.cs:164-178`

```csharp
if (File.Exists(WinhttpPath) && !File.Exists(DoorstopConfigPath))
{
    // 只有"有 winhttp、没有 doorstop_config"时才备份
}
File.Copy(winhttpSource, WinhttpPath, true);
File.WriteAllText(DoorstopConfigPath, ini, ...);
```

BepInEx 本身就会同时放 `winhttp.dll` 和 `doorstop_config.ini`。所以装了 BepInEx 的用户点「安装加载器」时，这两个文件都会**被直接覆盖，不做备份**。卸载时虽然能识别出「不是 Aurora 的文件」（`fedc130`），但到那时原文件已经没了。

另外，`WinhttpLooksLikeOurs()`（第 285-306 行）只和**内置**的 winhttp 比较。如果用户部署的是资源站或本地导入的内核，卸载时 `winhttp.dll` 会被误判为「不是 Aurora 装的」而保留，`doorstop_config.ini` 却被删掉，提示文案也会误导用户。

**建议**：部署前用 `DoorstopConfigLooksLikeOurs()` / `WinhttpLooksLikeOurs()` 判断归属，不是 Aurora 的文件就**把两份都备份**（`*.amm-backup`），或者干脆提示「检测到其它加载器」并要求用户确认。部署时把写入的 winhttp 哈希记到 `LoaderPath` 下，卸载时按这个记录判断归属。

---

## 🟠 F5. Markdown 自动链接没有限制协议

**位置**：`Controls/MarkdownViewer.xaml.cs:432-438`、`491-502`

普通链接（`LinkInline`）限制了只允许 http / https / mailto，但 CommonMark 的尖括号自动链接（`AutolinkInline`，即 `<scheme:...>`）可以是**任意协议**。这里只要 `Uri.TryCreate` 能解析就挂上 `OnRequestNavigate`，点击后用 `UseShellExecute = true` 打开。

mod 简介和更新说明来自四个第三方站点以及任意自定义站点，内容不可信。简介里写一个 `<search-ms:...crumb=location:\\host\share>` 或 `<file://\\host\share\x.exe>`，用户点一下，就会打开远程共享或者触发对应协议的处理程序。

**建议**：在 `OnRequestNavigate` 里**统一**做白名单检查（只放行 http / https / mailto），不要依赖每种 Inline 各自检查。其它页面里 `Process.Start(url)` 的地方（`OnlineModsPage`、`FavoritesPage`、`FirstRunWizard`）也建议走同一个 `SafeOpenUrl` 工具方法。

---

## 🟠 F6. 接口缓存不区分站点

**位置**：`Services/AdofaiToolsClient.cs:182-183`、`231`；`Views/Pages/OnlineModsPage.xaml.cs:1304`

`ApiCache.Key("mods", page, pageSize, search, ...)` 和 `ApiCache.Key("detail", resourceType, slug)` 里**都没有 `_apiRoot`**。而自定义站点同样使用 `AdofaiToolsClient`（`Sources/AdofaiToolsSource.cs:48`），所以：

- 默认站点和自定义站点的列表缓存会互相覆盖：10 分钟内切换来源，看到的是另一个站点的数据；
- 详情缓存里有**文件 Id**，串了之后会用 A 站的文件 Id 去 B 站申请下载意图，结果是 404，或者装错文件；
- 在设置里改了 `ApiBaseUrl` 之后同样会串；请求失败时还会走 `TryGetStale` 回退，这个回退没有过期时间。

**建议**：把 `_apiRoot` 作为缓存键的第一个部分（`ApiCache.Key("mods", _apiRoot, ...)`），`sitedetail` 的键也加上站点地址。

---

## 🟠 F7. 「更新」不校验装进来的是不是同一个 mod

**位置**：`Views/Pages/InstalledModsPage.xaml.cs:558-604`、`Services/ModService.cs:123`

更新来源的匹配有好几层模糊规则：

- 资源站按显示名、slug 以及归一化后的名字匹配（`UpdateCenter.cs:49-76`），`AddKey` 遇到重名时**后写的覆盖先写的**；
- 第三方源只按名字匹配（`UpdateCenter.cs:468`、`RemoteCatalog.Keys`）；
- GitHub Release 里没有文件名包含 modId 的 zip 时，就取**第一个 zip**（`GitHubUpdateService.cs:312-320`）。

匹配一旦出错，「更新」装进来的是另一个 Id 的 mod：它会作为一个新 mod 装在旁边，甚至覆盖另一个同 Id 的已装 mod；原来那个 mod 的更新提示却被 `Updates.Clear(mod)` 清掉了。

**建议**：给 `InstallFromZip` 增加一个 `expectedId` 参数。从「已安装」页发起更新时，如果 `Info.json` 里的 Id 和被更新的 mod 不一致，就**拒绝安装**并提示「下载到的是 X，不是 Y」。

---

## 🟠 F8. `Params.xml` 损坏时会被静默重建

**位置**：`Services/UmmParamsService.cs:117-132`、`106`

`LoadOrCreate()` 解析失败时会返回一个空文档，`SetEnabled` 随后用 `doc.Save` **直接覆盖**原文件。`Params.xml` 里存的不只是各 mod 的启用状态，还有 UMM 自己的设置（快捷键、窗口位置等），一次切换开关就会把这些全部清空。写入也不是原子的，写到一半中断会留下半截 XML，下次又会被当作损坏处理。

**建议**：解析失败时先备份成 `Params.xml.broken`，并且向用户报错，不要静默重建；写入改用「临时文件 + 改名」的方式（可以复用 `AppPaths.WriteAllTextAtomic`）。

---

## 🟠 F9. 部署内核：先复制文件，后检查组件

**位置**：`Services/LoaderService.cs:128-162`

`DeployFromDirectory` 先把 `UnityModManager.dll`、`0Harmony.dll` 等文件复制进游戏目录，**然后**才检查 `winhttp_x64.dll` / `winhttp_x86.dll` 是否存在，不存在就返回失败。对于从资源站下载或本地导入的、缺少注入组件的内核包，结果是游戏里装上了新的 UMM、但还是旧的注入器，而界面提示「部署失败」。

**建议**：把所有前置检查（注入组件存在、`UnityModManager.dll` 存在）放到第一次写入之前；条件允许的话，先复制到 `LoaderPath` 旁边的暂存目录，再整体替换。

---

## 🟡 F10. PR #4 的 follow-up 还没做

PR #4 合并时列了几条建议，main 上目前还没有对应的提交：

- `PathToExplorer.sb` 是 `[ThreadStatic] public static readonly`：字段初始化器只在第一个访问它的线程上运行，换一个线程访问时会拿到 `null`。而且 `public` 字段谁都能改。改成方法内的局部 `StringBuilder` 就行。
- 三处 `catch (InvalidPathException)`（`InstalledModsPage.xaml.cs:389`、`LoaderPage.xaml.cs:428`、`SettingsPage.xaml.cs` 导出诊断包处）都只写日志，用户点了按钮却没有任何反馈。
- 保存 GamePath 时规范化分隔符，见 F2。

---

## 🟡 F11. 收藏 / 安装映射 / 更新源损坏时会被静默清空

**位置**：`FavoritesStore.cs:56`、`InstallMapService.cs:54`、`UpdateSourceStore.cs:62`

`settings.json` 损坏时会先备份成 `.broken`（`SettingsService.cs`），但这三个文件解析失败后直接换成空集合，下一次 `Save()` 就把原文件覆盖了。收藏和手动绑定的更新源都是用户手动积累的数据。建议复用和 `SettingsService` 一样的 `.broken` 备份逻辑。

---

## 🟡 F12. 同一分钟内导出两次诊断包会失败

**位置**：`Views/Pages/SettingsPage.xaml.cs:554`

文件名精确到分钟（`yyyyMMdd-HHmm`），而 `ZipFile.Open(path, ZipArchiveMode.Create)` 内部使用的是 `FileMode.CreateNew`。已验证：第二次导出会抛出 `IOException: The file ... already exists`，界面显示「导出失败」。建议文件名精确到秒，或者在重名时加序号。

---

## 🟡 F13. 安装流程没有全局互斥

- `InstalledModsPage` 的 `_busy` 只在本页面内有效；`OnlineModsPage.Install_Click` **完全不检查** `_busy`，而且切换详情时 `RenderSiteDetail` / `RenderRemoteDetail` 会重新启用安装按钮；`FavoritesPage` 也有自己独立的 `_busy`。
- 所以多个安装可以同时进行。目前没有出问题，只是因为全项目都没有 `ConfigureAwait(false)`，下载完成后 `InstallFromZip` 会回到 UI 线程同步执行，**碰巧被串行化了**。
- 0.1.12.1 的更新日志已经提到「本地 ZIP 安装会让窗口短暂无响应」。以后如果把 `InstallFromZip` 挪到后台线程来解决卡顿，这里就会变成真正的并发写。

`UpdateCenter._results` 的情况类似：`CheckAllAsync` 写入时加了锁，但 `CheckOneAsync`（第 301 行）、`Get`、`Recount` 以及直接暴露出去的 `Results` 都没有加锁，目前同样是靠 UI 线程才没出问题。

**建议**：在 `ModService` 里加一个静态的 `SemaphoreSlim`，把安装、卸载、启停串行化；`UpdateCenter` 的读取统一加锁，或者改用 `ConcurrentDictionary`。

---

## 🟡 F14. 远端返回的 URL 会被当成本地路径尝试

**位置**：`Services/KernelService.cs:231`、`Services/AppSelfUpdater.cs:69`、`476`

`if (File.Exists(release.DownloadUrl))` / `File.Exists(url)` 这类分支原本是给本地导入和开发测试用的，但传进来的值也可能来自资源站的下载意图。如果返回的是 `\\host\share\x.zip` 这样的 UNC 路径，`File.Exists` 会发起 SMB 连接，可能泄露 NTLM 凭据。建议把「本地文件」和「远端 URL」拆成两个入口；远端入口只接受 `https://`。

---

## 🟡 F15. 绿色版更新脚本忽略 robocopy 失败

**位置**：`Services/AppSelfUpdater.cs:368-374`

robocopy 的退出码大于等于 8 表示复制失败（例如文件被杀毒软件占用），但脚本只是记一行日志，然后照常 `start` 新的 exe，程序目录里可能是新旧文件混在一起。建议退出码大于等于 8 时不启动新版本，改为重试，或者弹窗提示用户手动解压。另外，批处理里直接嵌入路径时，`%` 字符会被展开，OEM 代码页无法表示的字符也会变成 `?`，这两种情况也建议处理。

---

## ⚪ F16. 流程：没有 CI，也没有提交进仓库的测试

- 仓库里没有 `.github/workflows`，每次发版都靠本地构建。
- PR #2 收尾时写的安装测试工程放在 `artifacts/install-test`，而 `.gitignore` 里有 `artifacts/`，所以它**从来没有进过仓库**。F1 这样的回归本来可以被它发现：只要加一个「复制到一半中断后扫描」的用例就行。
- `tools/` 下的几个 SmokeTest 都需要手动运行。

**建议**：新建一个 `tests/` 测试工程，把安装相关的用例（恶意 Id、坏包、中断恢复、`Params.xml` 损坏等）放进去；加一个最简单的 GitHub Actions：`dotnet build` 加 `dotnet test`，在 Windows runner 上运行。

---

## ⚪ F17. 文档

- `docs/releases/v0.1.12.2.md` 把「跳到文档」的原因写成了「文件夹不存在」。实际原因是 PR #4 确认的**路径分隔符混用**，而这种路径的来源见 F2。建议在下一版更新日志里更正，并说明 0.1.12.2 的修复没有覆盖这种情况。
- PR #4 的修复已经合进 main（`v0.1.12.2-2-gd7b0ca4`），但**还没有发版**，csproj 里的版本号也还是 0.1.12.2。
- `AppUpdateService` 的类注释还写着「只做提示 + 打开下载页，不自动替换正在运行的程序」，和现在的实现不符。
- README「开发」一节的打包示例还是 `-Version 0.1.6`。

---

## 建议的修复顺序

1. **F1**（数据丢失）和 **F2**（PR #4 根因）：改动小、影响面大，建议合在一起，作为 0.1.12.3 发布；
2. **F3**：至少先做「固定 slug + 限定站点」，这两条几行代码就能完成；哈希校验需要配合发版流程一起做；
3. **F4 ~ F9**：各自独立，可以分几个小 PR；
4. **F16**：建议在修 F1 的同时把测试工程和 CI 建起来，避免同样的回归再次发生。
