# LKami9 助手合集（OmniToolbox / TreeHouse 在线模块）

一个链接装五个模块：

| 模块 | InternalName | 文件 |
|---|---|---|
| LKami9称号助手 | `LKami9TitleHelper` | `Modules/LKami9TitleHelper.cs` |
| LKami9幻化助手 | `LKami9GlamourHelper` | `Modules/LKami9GlamourHelper.cs` |
| LKami9幻卡包助手 | `LKami9TriadCardPacks` | `Modules/LKami9TriadCardPacks.cs` |
| LKami9秘录助手 | `LKami9FolkloreBooks` | `Modules/LKami9FolkloreBooks.cs` |
| LKami9国服频道私聊守卫 | `LKami9ChannelWhisperGuard` | `Modules/LKami9ChannelWhisperGuard.cs` |

仓库地址（填到 OmniToolbox → 在线仓库）：

```
https://raw.githubusercontent.com/LKami9/LKami9-zhushou-heji/main/TreeHouseModules.json
```

`Modules/*.cs` 与原先各仓库发布的文件字节完全一致（SHA256 相同），InternalName 不变，
所以换用这个合集链接不会丢设置；原来的链接可以删掉。

## 变更记录

- 2026-10-24：新增 `LKami9ChannelWhisperGuard`（国服频道私聊守卫）v1.0.3。它取代已移除的 `LKami9WhisperLogout`：
  走 Omni 国服频道客户端的 `WhisperReceived` 事件判定正文，命中后经 OmenTools 命令通道执行 `/logout`；
  实测定位链 `Plugin.host → OmniHost.featureLifetime → FeatureLifetime.disposers → chatFrameOptimization → omniWorldChat`。
- 2026-10-24：从合集移除 `LKami9WhisperLogout`（频道私聊登出），原因是它监听的是游戏 `/tell`，抓不到国服频道私聊。
  已安装该模块的用户：合集不再提供它，需在 Omni 里单独停用/卸载（不会自动消失）。
