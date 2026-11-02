# Aurora

《冰与火之舞》（A Dance of Fire and Ice）的 Windows Mod 管理器。

Aurora 将多个来源的 Mod 集中到同一个界面，方便浏览、搜索、安装和检查更新，同时提供本地 Mod 管理和 UnityModManager 加载器维护功能。界面以中文为主，默认连接 ADOFAI Tools，也支持 modrift、TUF、modlist 和兼容接口的自定义站点。

> 项目原名 ADOFAI Mod Manager（AMM），从 v0.1.6 起更名为 Aurora，以避免与另一个同名工具混淆。
>
> 本项目是第三方工具，与 7th Beat Games、UnityModManager 官方及各 Mod 资源站没有隶属关系。

## 功能

### 在线 Mod

- 切换 ADOFAI Tools、modrift、TUF、modlist 和自定义来源
- 浏览、搜索、排序和分类筛选 Mod，具体选项取决于来源支持的功能
- 查看 Mod 介绍和版本，安装指定版本
- 识别已安装和可更新的 Mod
- 显示来源信息及原站页面链接
- 安装前校验下载内容，避免将网页等错误内容当作压缩包
- 标注需要 MelonLoader 的 Mod；目前不支持这类 Mod 的自动安装，也不安装 MelonLoader 加载器

### 已安装 Mod

- 启用、禁用、更新、卸载和打开 Mod 文件夹
- 从本地 ZIP 安装尚未收录到资源站、自己制作或从其他渠道获得的 Mod
- 手动绑定 GitHub 仓库，在软件内检查和获取更新
- 从多个来源检查更新
- 检测重复安装，减少更新后留下旧副本的情况

### UMM 环境

- 检测、安装、修复和卸载 UnityModManager 加载器
- 单独更新或回滚 UMM loader
- 使用内置文件、资源站下载或本地导入的加载器文件
- 通过 Steam 或直接运行游戏程序启动游戏

### 其他功能

- 收藏不同来源的 Mod，并使用对应来源安装
- 启动时检查已安装 Mod 的更新
- 检查 Aurora 自身更新，并在软件内完成升级
- 首次启动向导
- 浅色、深色和跟随系统主题

## 系统要求

- Windows 10 / 11（x64）
- Steam 版《冰与火之舞》

发布包已包含 .NET 运行时，无需另行安装。

## 下载与升级

前往 [Releases](../../releases) 下载：

| 版本 | 说明 |
| --- | --- |
| 安装包 | 可选择安装范围和目录，并创建快捷方式 |
| 绿色版 ZIP | 解压后运行 `Aurora.exe`，不写入注册表 |

配置、日志和缓存保存在用户目录中，不写入程序安装目录，安装到 `Program Files` 也可以正常使用。

从旧版 AMM 升级时，安装版可直接覆盖安装，绿色版可解压覆盖原目录。原有设置、收藏和日志会保留，GitHub 旧地址也会自动重定向到新地址。

## 第一次使用

首次启动向导会引导你完成游戏目录、资源站和 UMM 环境的基本配置。完成后，在“在线 Mod”页面右上角选择来源，即可开始查找和安装 Mod。

## Mod 来源

默认站点和自定义站点在“设置”中配置，日常浏览时在“在线 Mod”页面切换来源。

| 来源 | 说明 |
| --- | --- |
| [ADOFAI Tools](https://adofaitools.top/) | 默认来源，已内置公共 API key，开箱即用（也可以填自己的 key） |
| [modrift](https://modrift.org) | 无需 API key，提供公开 API |
| [TUF（The Universal Forums）](https://tuforums.com/mods) | 无需 API key，提供 Mod 目录和下载服务 |
| [modlist](https://modlist.org) | 无需 API key，提供公开 API |
| 自定义站点 | 实现兼容接口后，可在“设置 → 自定义站点”中添加 |

Aurora 不绑定单一资源站。来自各站点的内容会注明出处，并提供对应的原站页面链接。

从某个来源安装 Mod 后，更新时会优先使用该来源。“已安装”页面的更新检查也会查询其他来源，查找较新的版本。手动检查会刷新相关数据，启动时的自动检查则使用缓存。

Aurora 内置了一个由 ADOFAI Tools 站长提供的公共 API key，默认站点开箱即用，所有用户共用，可能触发限流；如果遇到这种情况，可以在“设置”里填写自己的 key（自己的 key 优先）。这个 key 会随开源客户端公开，站长可随时在后台撤销或更换。Mod 下载通过各来源的下载接口完成，并使用本地缓存减少重复请求。

自定义站点的接口要求见[资源站接口规范](docs/RESOURCE-SITE-API.md)。

## 使用说明

- 新版本不一定适合当前游戏环境，更新前请查看 Mod 作者提供的说明。
- Mod 或资源站提供的兼容性信息仅供参考，不代表 Aurora 能自动判断其是否适配当前游戏版本。
- 当前不提供“一键更新全部 Mod”，需要逐个选择更新。
- 在线功能依赖对应资源站。站点暂时不可用时，仍可从本地 ZIP 安装 Mod。

## 开发

需要 .NET 10 SDK。

调试运行：

```powershell
dotnet run --project src/AdofaiModManager
```

构建发布包：

```powershell
powershell -ExecutionPolicy Bypass `
  -File installer/build-installer.ps1 `
  -Version 0.1.6
```

默认生成绿色版 ZIP，发布目录为 `dist\Aurora\`。需要安装包时，加上 `-Installer` 参数。

## 致谢

- 感谢 [ADOFAI Tools](https://adofaitools.top/) 站长 [small-lizi](https://github.com/small-lizi/) 允许 Aurora 接入，并提供在线 Mod 数据和下载服务。相关代码见 [ADOFAI Tools 仓库](https://github.com/small-lizi/ADOFAI-Tools)。
- 感谢 [modrift](https://modrift.org) 作者 Ilythra 提供公开 API，并允许 Aurora 接入。
- 感谢 [TUF（The Universal Forums）](https://tuforums.com/mods) 维护团队（[GitHub：T21C](https://github.com/T21C)）提供 Mod 目录、推荐与下载服务。Aurora 内展示的 TUF 数据会标注来源并回链到 TUF 站点。
- 感谢 [modlist.org](https://modlist.org) 作者 square3ang 提供公开 API，并允许 Aurora 接入。相关项目见 [modlist-org](https://github.com/modlist-org)。
- 感谢 [UnityModManager](https://github.com/newman55/unity-mod-manager) 及其作者 [newman55](https://github.com/newman55/) 提供加载器基础。

## 贡献者

- [@create-xiaoyu](https://github.com/create-xiaoyu)：安装安全、更新检查与稳定性修复（[PR #2](https://github.com/lishangi688/adofai-aurora/pull/2)）。
- [@xbodwf](https://github.com/xbodwf)：Markdown 渲染器；多加载器可行性调研（[PR #1](https://github.com/lishangi688/adofai-aurora/pull/1)）。

## 许可证

本项目以 [MIT 许可证](LICENSE) 发布。

随程序分发的第三方组件许可证位于：

```text
src/AdofaiModManager/Resources/UmmLoader/
```

## 免责声明

- 使用前建议备份游戏文件、存档和 Mod，使用本软件的风险由用户自行承担。
- 在线功能依赖第三方资源站，其可用性和内容由对应站点负责。
- 安装 Mod 前请遵守 Mod 作者和资源站的许可与使用规定。
