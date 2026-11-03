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

## 已知事项

- **配置必须是「嵌套 Settings + config 成员」**：宿主 API 1 的 `FindConfigMember` 会检查这两个条件，
  不满足直接拒载（日志原文：`配置必须使用嵌套 Settings 类型和 config 成员`）。
  六个模块的配置类都已改成模块内的 `private sealed class Settings`，属性名、类型、默认值、JSON 键一字未改。


- **幻化助手的物品来源表**：原文件直接引用 `LuminaSupplemental.Excel`，但 Omni 1.1.8.7 的插件目录里没有这个程序集。
  现在改成文件内的反射桥（按类型名取 `CsvLoader.LoadResource<T>`），编译期不再依赖它：
  运行时能取到就用真实数据，取不到就退回 wiki 兜底（与原 `try/catch` 分支效果一致）。
- **仍未本地化的文字**（有意保留）：幻化模板写入/应用的进度提示（那段按“逻辑不动”处理）、
  Omni 互通的分享文本格式、装备槽位名（同时是查表键）、`无 / 无染色` 等哨兵串、命令参数别名（`界面/打开/关闭/识别`）。
  这些不挡加载，后续可以逐步挪进词条。
- **命令入口**：`/omni <模块名> <参数>` 由 `OmniCommandCatalog.FormatModule` 生成；各模块原有的短命令（`/L9CHH`、`/L9KH`、`/L9HH`、`/L9HD`）照旧可用。
  秘录助手自 2.1.0 起**不再注册任何命令**（含原 `/L9ML`），入口改在 Omni 模块设置页的「打开窗口 / 立即检测 / 诊断」。

## 变更记录

- 2026-11-04：**秘录助手 2.1.0**（1673 → 2786 行）。
  修复：`CostType=3` 的货币桶被当成道具 ID（导致秘籍第三卷起显示「火之碎晶 ×100」这一幽灵需求）、
  收藏价值被当成数量、碎晶/水晶/晶簇在独立容器 `InventoryType.Crystals` 漏检、需 HQ 材料无任何标记、
  同一本书多商店成本并集累加、目录为空仍报「检测完成 0/0」、切换语言后成本文案不刷新、`ResetSettings` 恒返回 true。
  新增：持有量接入 Omni 库存快照（鞍囊 / 雇员 / 收藏柜 / 部队箱）并补齐实时容器、持有分布 tooltip、
  一键取回、材料名点击打开制作笔记（右键物品信息 / Shift+左键复制）、汇总「只看有缺口 / 复制缺口清单」、
  搜索支持材料名反查、目录磁盘缓存。
  移除：全部聊天命令（含 `/L9ML`）、窗口贴靠 Omni、导出兑换数据。

- 2026-10-30：修「只有农村人助手能装」的问题——其余五个模块的配置类还是顶层 `XxxConfig`，
  被宿主 `FindConfigMember` 拒绝（要求嵌套 `Settings` 类型 + `config` 成员）。
  已把配置类收敛为模块内 `private sealed class Settings`，配置属性和 JSON 键未变；
  版本：称号 3.0.1，频道/秘录/幻化/幻卡包 2.0.1，农村人 2.0.0 不变。


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
