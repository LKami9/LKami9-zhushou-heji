# LKami9 助手合集（OmniToolbox / TreeHouse 在线模块）

一个链接装六个模块：

| 模块 | InternalName | 文件 |
|---|---|---|
| LKami9称号助手 | `LKami9TitleHelper` | `Modules/LKami9TitleHelper.cs` |
| LKami9幻化助手 | `LKami9GlamourHelper` | `Modules/LKami9GlamourHelper.cs` |
| LKami9幻卡包助手 | `LKami9TriadCardPacks` | `Modules/LKami9TriadCardPacks.cs` |
| LKami9秘录助手 | `LKami9FolkloreBooks` | `Modules/LKami9FolkloreBooks.cs` |
| LKami9国服频道私聊守卫 | `LKami9ChannelWhisperGuard` | `Modules/LKami9ChannelWhisperGuard.cs` |
| LKami9农村人助手 | `AutoLimsaOnArrival` | `Modules/AutoLimsaOnArrival.cs` |

仓库地址（填到 OmniToolbox → 在线仓库）：

```
https://raw.githubusercontent.com/LKami9/LKami9-zhushou-heji/main/TreeHouseModules.json
```

模块使用 Omni 在线模块 **API 1**（`OmniModuleBase` + `[ModuleApiVersion(1)]` + `ModuleLocalization`），
最低宿主版本 **1.1.8.2**。老版本 Omni 无法加载这批模块，请先更新本体。

InternalName 与配置 JSON 键都没有变化，升级模块不会丢设置；旧的 raw 链接可以删掉，只保留合集这一条。

## 变更记录

- 2026-10-29：全部模块迁移到 **API 1**（宿主 1.1.8.2 起强制）：
  基类 `ModuleBase` → `OmniModuleBase`、类上补 `[ModuleApiVersion(1)]`、
  生命周期 `OnEnable/OnDisable/OnDispose` → `OnStart(FeatureLifetime)/OnStop/OnDestroy`、
  界面文字改为 `ModuleLocalization` 词条 + `OmniLoc.Get`、清单补 `ApiVersion`/`RepositoryURL`/`Localization`。
  六个模块版本号同步抬到下一个大版本，`MinimumOmniVersion` 统一为 `1.1.8.2`。

- 2026-10-26：新增 `AutoLimsaOnArrival`（LKami9农村人助手）v1.0.0：跨服/超域传送落地稳定后，
  若不在海都就调用原生 `Telepo` 传送到利姆萨·罗敏萨大水晶；服务器记录写在 `pluginConfigs\OmniToolbox\AutoLimsa.state`，
  关客户端再登录也不会丢，短命令 `/L9HD GO`、`/L9HD rset`。

- 2026-10-24：新增 `LKami9ChannelWhisperGuard`（国服频道私聊守卫）v1.0.3。它取代已移除的 `LKami9WhisperLogout`：
  走 Omni 国服频道客户端的 `WhisperReceived` 事件判定正文，命中后经 OmenTools 命令通道执行 `/logout`；
  实测定位链 `Plugin.host → OmniHost.featureLifetime → FeatureLifetime.disposers → chatFrameOptimization → omniWorldChat`。
- 2026-10-24：从合集移除 `LKami9WhisperLogout`（频道私聊登出），原因是它监听的是游戏 `/tell`，抓不到国服频道私聊。
  已安装该模块的用户：合集不再提供它，需在 Omni 里单独停用/卸载（不会自动消失）。
