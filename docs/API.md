# ADOFAI Tools 资源站 API（实测记录）

> 实测时间：2026-09-28
> 结论：**可用**。已用你的 API key 跑通「列表 → 详情 → 取下载地址」全流程。

---

## 1. 基本信息

| 项 | 值 |
|---|---|
| 站点 | `https://www.adofaitools.top` |
| API 根 | `https://www.adofaitools.top/api` |
| 鉴权 | `Authorization: Bearer <API_KEY>` |
| API Key 格式 | `adof_sk_...`（在站点「个人中心」创建） |
| Spec（OpenAPI） | `https://www.adofaitools.top/api/openapi/public.json` |
| 文档 UI（Scalar） | `https://www.adofaitools.top/api/docs/` |
| 后端形态 | NestJS（`{"statusCode","message","error"}` 错误结构） |

### ⚠️ 重要注意
1. **必须用 `www.adofaitools.top`**。裸域 `adofaitools.top` 当前**没有 A 记录**（DNS 查不到），老 ADOFAI-Tools 源码里的 `adofaitools.top/api/get_tools.php` 等 PHP 接口**已全部失效**（返回 404）。旧源码只能作参考，不能照抄地址。
2. 我们这边直连报 TLS 失败，是**本机代理（127.0.0.1:7900，fake-IP）**导致的；通过该代理可正常访问。软件里应**走系统代理**（.NET `HttpClient` 默认会读取系统代理）。
3. API Key 鉴权**只认 `Authorization: Bearer`**；`X-API-Key` 和 `?api_key=` 都返回 401。

---

## 2. 我们需要的接口（只读部分）

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/mods` | Mod 列表（分页/搜索/筛选/排序） |
| GET | `/api/mods/{resourceType}/{slug}` | Mod 详情（含所有版本、文件） |
| POST | `/api/mod-files/{fileId}/download-intent` | 换取签名下载地址 |
| GET | `/api/mod-files/{fileId}/download` | 直接下载（跳转） |
| GET | `/api/me/mod-favorites` | 我的收藏（站内） |
| GET | `/api/me/mod-follows` | 我的关注（站内） |
| GET | `/api/mods/{resourceType}/{slug}/comments` | 评论 |
| GET | `/api/announcements` | 公告 |
| GET | `/api/online-tools` | 在线工具 |

> 另有 `/api/creator/...` 一整套「投稿/创作中心」接口，本期不需要。

### `/api/mods` 查询参数
| 参数 | 说明 |
|---|---|
| `page` | 页码，从 1 开始 |
| `pageSize` | 每页数量，**最大 100** |
| `search` | 搜索关键词 |
| `resourceType` | `MOD` / `RESOURCEPACK` / `SHADER` / `PLUGIN` / `LIBRARY` / `TOOL` / `OTHER` |
| `loader` | 加载器名，如 `unitymodmanager` |
| `featured` | 是否只看精选 |
| `sort` | `updated` / `downloads` / `favorites` |

---

## 3. 数据模型（关键字段）

### 列表项 `ModListItemResponse`
```
id, resourceType, slug, displayName, summary,
authors[{name,userId,username,nickname,avatarUrl}],
iconUrl, featured, status, downloadCount, favoriteCount,
followerCount, commentCount, favorited, followed,
latestVersion{id, versionId, versionType, publishedAt},
publishedAt, createdAt, updatedAt
```

### 详情 `ModDetailResponse`（在列表项基础上增加）
```
description, readme, color, games[], loaders[], categories[],
versionTypes[], dependencies[{...}], license, sourceUrl, homepageUrl,
screenshots[{fileId,url}], publisher{...},
versions[{
  id, versionId, versionType, tags[], changelog,
  files[{
    id, name, supportedGames[{name,version}], loaders[],
    size, mimeType, status, downloadUrl, downloadCount
  }],
  downloadCount, publishedAt, createdAt, updatedAt
}]
```

### 下载结果 `ModFileDownloadIntentResponse`
```
{
  "url":       "https://....r2.cloudflarestorage.com/...&X-Amz-Expires=86400&...",  // 签名直链
  "sourceUrl": "https://assets.adofaitools.top/files/<uuid>.zip",
  "fileName":  "ADOF AITools-Mod-<ModId>-<版本>-unitymodmanager.zip"
}
```

---

## 4. 实测流程与结果

```
GET  /api/mods?pageSize=5            → total=121
GET  /api/mods/MOD/adofai-editortweaks-chartrendering
                                     → versions=2，最新 1.0.3，文件 37MB，
                                       loaders=[unitymodmanager]
POST /api/mod-files/{fileId}/download-intent
                                     → 签名 URL（24h）+ fileName
```

- 全站共有 **121 个 Mod 资源**（含 MOD / 资源包 / 谱面等）。
- 文件名与 `loaders: unitymodmanager` 说明**资源站上的 Mod 就是 UMM 格式的 zip**，可直接用我们的安装逻辑解压到 `Mods/<Id>/`。
- `files[].supportedGames[]` 提供 **游戏版本兼容信息**（我们的兼容性提示用它）。

---

## 5. 遗留待确认
- [x] 站长已同意第三方客户端调用并公开分发；已创建 Aurora 专用公共 key，可在后台随时撤销（2026-10-31）。
- [ ] 确认该 key 的**频率限制 / 配额**（所有 Aurora 用户共用；超限时的返回码和恢复时间）。
- [ ] 站点域名后续是否会把裸域 `adofaitools.top` 恢复（软件里建议把 base URL 做成可配置，默认 `https://www.adofaitools.top`）。
