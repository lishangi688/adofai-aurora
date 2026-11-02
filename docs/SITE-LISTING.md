# 把 Aurora 上架到资源站 —— 对接说明（致站长）

> 本文档是写给 **ADOFAI Tools 站长** 的：说明如何把 Aurora（Aurora）放进资源站的「工具库」，
> 以及客户端会读取哪些字段、需要遵守哪些小约定。
>
> 前置背景：Aurora 是一个开源的《冰与火之舞》mod 管理器（MIT 许可），
> 对接 ADOFAI Tools 的公开接口，**已获得站长授权**；站长此前也同意把 Aurora 放到资源站上。

- 项目地址：<https://github.com/lishangi688/adofai-aurora>
- 发布页：<https://github.com/lishangi688/adofai-aurora/releases>
- 当前版本：**v0.1**
- 许可：**MIT**

---

## 一、这件事的好处

客户端检查 Aurora 自身是否有新版本时，会**同时查两条通道**（取版本号更高的那个）：

| 通道 | 国内可用性 |
|---|---|
| GitHub Releases | 不稳定（`api.github.com` 有时能通，`raw.githubusercontent.com` 常被污染） |
| **资源站「工具库」** | **稳定** |

把 Aurora 放进资源站后，国内用户不必依赖 GitHub 也能收到新版本提示，对用户和资源站都是好事。

---

## 二、需要站长做什么

在**工具库**（也就是 `/api/tools` 对应的那一类，UnityModManager 就在里面）新增一个条目，
**用法和现有的 UnityModManager 条目完全一样**。下面是几个要注意的约定。

### 1）显示名（最重要）

> ⚠️ 站点上工具库的 `slug` 是自动生成的（形如 `tool-b813b01d`），客户端无法靠 slug 识别，
> 所以**只能靠显示名**。

请使用下面任一形式（大小写不敏感）：

| ✅ 可以 | 例子 |
|---|---|
| 正好是 `Aurora` | `Aurora` |
| 包含 `Aurora` | `Aurora`、`Aurora（Aurora）` |
| 以 `Aurora` 开头 | `Aurora 模组管理器`、`Aurora管理器` *(需 0.1.1 及以上客户端)* |

用别的名字（例如「Aurora工具」写在中间、或纯中文「模组管理器」）客户端会认不出来，
那时用户只能用 GitHub 通道检查更新。

### 2）版本号

`latestVersion.versionId` 填**版本号本体**即可：

- ✅ `0.1`、`0.1.1`、`1.0`
- ✅ 带 `v` 前缀也可以（`v0.1.1`），客户端会自动忽略前缀
- ❌ 不要填成 `v0.1 beta`、`最新版` 这类非版本号文本（客户端按点分数字比较大小）

### 3）下载文件

在 `versions[].file` 里放一个可下载的文件：

- **推荐**：站长把安装包上传到资源站自己托管 → 国内用户下载最快
  - 文件：`Aurora-Setup-0.1.exe`（约 47 MB）
- **也可以**：直接指向 GitHub Release 的资产生成地址

> 说明：客户端目前**不会自动下载安装**，只会打开该工具的页面引导用户去下载
> （安装版涉及管理员权限，交给用户手动更新更稳妥）。所以 `file` 主要用于版本标识 + 让用户能下载。

### 4）版本列表

`GET /api/tools/{slug}` 的 `versions[]` 保留历史版本即可，**不要求按新到旧排序**——
客户端会自己挑出版本号最大的那个。

---

## 三、客户端只读取两个现有接口（无需新增任何接口）

```
GET /api/tools?page=1&pageSize=100     → 在 items[] 里找 Aurora
GET /api/tools/{slug}                  → 取 versions[] 里最新的 versionId
```

读取字段：`items[].displayName`、`items[].latestVersion.versionId`、`versions[].versionId` ——
**全部是现有字段，站长不需要为 Aurora 改动或新增任何接口。**

> 补充：工具库目前共 45 条，客户端一次拉 100 条，一次请求即可覆盖全部。

---

## 四、可以直接复制去填表的信息

| 项目 | 内容 |
|---|---|
| **名称** | `Aurora`（简称 `Aurora`） |
| **分类** | 工具 |
| **一句话简介** | 独立的《冰与火之舞》mod 管理器，可取代 UnityModManager；对接 ADOFAI Tools 资源站，一键浏览 / 安装 / 更新 mod。 |
| **版本** | `0.1` |
| **下载地址** | <https://github.com/lishangi688/adofai-aurora/releases>（或站长自行上传安装包） |
| **开源仓库** | <https://github.com/lishangi688/adofai-aurora> |
| **许可** | MIT |
| **系统要求** | Windows 10 / 11（x64），自包含发布，无需另外安装 .NET 运行时 |
| **图标** | 仓库内 `src/AdofaiModManager/Assets/app-128.png`（128×128 PNG） |

图标也可以直接用这个链接（如站长那边访问 GitHub 不通，可直接向作者索取该 PNG 文件）：
<https://github.com/lishangi688/adofai-aurora/blob/main/src/AdofaiModManager/Assets/app-128.png>

---

## 五、上架之后

麻烦告知 Aurora 在站上的**实际显示名**（以及你希望的后续更新方式）。
随后发布的 **0.1.1** 会把「资源站通道」正式接入并测试通过，届时：

- 国内用户即使访问不了 GitHub，也能收到 Aurora 的新版本提示；
- 资源站与 GitHub 版本不一致时，客户端取版本号更高的那个；
- GitHub 若连不上，客户端会**静默降级**，只用资源站的结果，不会报错打扰用户。

---

## 六、Aurora 对资源站的原则

- 内置了一个由站长提供的 **Aurora 专用公共 API key**，默认站点开箱即用；该 key 随开源客户端公开、可在后台随时撤销，用户也可以填写自己的 key 覆盖；
- 界面上的 key 默认以圆点打码，避免直播 / 截图泄露；
- 主动做了**本地缓存**以减少对资源站的请求压力：
  图标 7 天、Mod 列表 10 分钟、Mod 详情 30 分钟、内核与安装包缓存；
- 更新检查**不过度频繁**：GitHub 结果缓存 10 分钟，且启动时只检查一次。

再次感谢站长对第三方客户端的授权与支持 🙏
