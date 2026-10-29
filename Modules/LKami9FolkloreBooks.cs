using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using Lumina.Excel.Sheets;

using OmniToolbox.Commands;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace LKami9.FolkloreBooks;

/// <summary>
/// LKami9秘录助手
///
/// 检测角色已使用 / 未使用的传承录与秘籍，显示兑换所需材料、持有量与缺口，材料名可点击复制。
///
/// 功能：
///   · 独立窗口（OmniToolbox.Common 的 OmniWindowChrome / OmniTheme / OmniControls 组件），
///     可贴靠 Omni 主窗口、可折叠、可拖动标题栏解除贴靠；设置页只留配置项。
///   · 短命令 /L9ML（打开 / scan 重检 / window 切换 / close 关闭），
///     保留 /omni LKami9FolkloreBooks 旧入口。
///   · 自动使用：战斗 / 读条 / 占用 / 切区时暂停；0.6 秒未解锁才重发（最多 2 次）；
///     5 秒超时跳过该本；每确认一本局部刷新，全部完成自动汇总。
///   · 兑换材料持有量同时统计货币容器和背包（道具类材料不再恒为 0）。
///   · 窗口内按书名搜索，背包里未使用的书可单本使用。
///
/// 接口（全部公开，不写内存）：
///   InventoryManager / AgentInventoryContext.UseItem / UnlockState.IsItemUnlocked
/// </summary>
[ModuleApiVersion(1)]
public sealed class LKami9FolkloreBooks : OmniModuleBase
{
    // ItemAction 触发的 Action 行号：4107 = 使用传承录，2136 = 使用秘籍
    private const uint FolkloreActionRowId     = 4107;
    private const uint SecretRecipeActionRowId = 2136;

    private const string WindowId     = "###LKami9MiLuMainWindow";
    private const string CollapseId   = "###LKami9MiLuCollapse";
    private const string CloseId      = "###LKami9MiLuClose";
    private const string ShortCommand = "/L9ML";

    private static readonly TimeSpan NoEffectWindow = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan UseTimeout     = TimeSpan.FromSeconds(5);

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Item,
        Author   = "LKami9",
        Commands =
        [
            new("CmdOpen",         OmniCommandCatalog.FormatModule(nameof(LKami9FolkloreBooks), string.Empty)),
            new("CmdScan",         OmniCommandCatalog.FormatModule(nameof(LKami9FolkloreBooks), "scan")),
            new("CmdToggleWindow", OmniCommandCatalog.FormatModule(nameof(LKami9FolkloreBooks), "window")),
            new("CmdClose",        OmniCommandCatalog.FormatModule(nameof(LKami9FolkloreBooks), "close")),
        ],
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"]       = "LKami9秘录助手",
            ["Description"] = "独立窗口 + /L9ML 短命令：检测传承录 / 秘籍使用状态，汇总兑换材料、持有量与缺口，支持自动使用，材料名可点击复制。",

            ["CmdOpen"]          = "打开窗口",
            ["CmdScan"]          = "重新检测",
            ["CmdToggleWindow"]  = "打开 / 关闭窗口",
            ["CmdClose"]         = "关闭窗口",
            ["ShortCommandHelp"] = "LKami9 秘录助手：/L9ML [scan|window|close]",

            ["WindowTitle"]  = "LKami9 秘录助手",
            ["SettingsHint"] = "主界面在独立窗口中（/L9ML 打开），配色与窗口样式跟随 Omni 本体主题。",

            ["OpenWindow"]   = "打开窗口",
            ["CloseWindow"]  = "关闭窗口",
            ["DetectNow"]    = "立即检测",
            ["DetectButton"] = "检测",
            ["SearchHint"]   = "搜索书名",
            ["ShowUsed"]     = "显示已使用",

            ["AutoUseFolklore"]      = "自动使用传承录",
            ["AutoUseRecipe"]        = "自动使用秘籍",
            ["AutoUseFolkloreShort"] = "自动·传承录",
            ["AutoUseRecipeShort"]   = "自动·秘籍",
            ["GapLabel"]             = "每本之间的间隔 (毫秒)",
            ["DockToOmni"]           = "贴靠 Omni 主窗口（拖动标题栏可解除）",

            ["GroupFolklore"] = "传承录（采集）",
            ["GroupRecipe"]   = "秘籍（生产）",
            ["GroupHeader"]   = "{0}（未用 {1}）",
            ["NoMatch"]       = "无匹配的书。",
            ["GroupAllUsed"]  = "都已使用。",
            ["UsedDivider"]   = "—— 已使用 ——",

            ["Held"]      = "◀ 已持有",
            ["Use"]       = "使用",
            ["UseFailed"] = "使用失败：书不在背包、正在使用另一本，或当前状态不允许。",

            ["SummaryHeader"] = "汇总：还需要兑换的材料",
            ["SummaryEmpty"]  = "暂时没有需要兑换的书。",
            ["ColMaterial"]   = "材料",
            ["ColRequired"]   = "需要",
            ["ColHeld"]       = "持有",
            ["ColMissing"]    = "还差",
            ["OtherInBag"]    = "（另有 {0} 本已在背包里，未计入）",
            ["OtherHoldings"] = "其他持有：{0}",

            ["CostText"]    = "需 {0}",
            ["Copied"]      = "已复制：{0}",
            ["ClickToCopy"] = "点击复制：{0}",

            ["StatusStarting"]           = "已启用，正在检测…",
            ["StatusNotScanned"]         = "尚未检测。",
            ["StatusScanDone"]           = "传承录 已用 {0}/{1} · 秘籍 已用 {2}/{3}",
            ["StatusScanFailed"]         = "检测失败：{0}",
            ["StatusAutoIdle"]           = "没有可自动使用的书（背包里没有未使用的书，或已全部跳过）。",
            ["StatusUseSkipped"]         = "已跳过 {0}（等待解锁超时），下次检测后重试。",
            ["StatusUsing"]              = "正在使用：{0}",
            ["StatusUsedOne"]            = "已使用：{0}（还剩 {1} 本）",
            ["StatusAllUsed"]            = "全部书籍都已使用。",
            ["StatusShortCommandFailed"] = "/L9ML 注册失败：找不到命令服务。",
        });

    public override bool HasSettings => true;

    // 宿主按成员名 config 绑定已保存的配置；不能是 readonly，宿主需要给它赋值。
    // 属性名 / 类型 / 默认值保持不变，否则已发布用户的 JSON 会丢。
    private Settings config = new();

    private ICommandManager shortCommandManager;
    private bool shortCommandRegistered;

    #region 数据

    private readonly List<Book> catalog = [];
    private readonly Dictionary<uint, Book> catalogById = [];
    private bool catalogBuilt;

    private readonly Dictionary<uint, List<Cost>> costMap = [];
    private bool costMapBuilt;

    private readonly List<BookRow> rows = [];
    private readonly Dictionary<uint, BookRow> rowByItem = [];

    private List<BookRow> folkloreUnusedView = [];
    private List<BookRow> folkloreUsedView   = [];
    private List<BookRow> recipeUnusedView   = [];
    private List<BookRow> recipeUsedView     = [];

    private List<Need> needs = [];
    private List<Holding> currencyHoldings = [];
    private List<Holding> itemHoldings     = [];
    private List<Holding> otherCurrencies  = [];
    private int alreadyInBag;

    private readonly HashSet<uint> bagItems = [];
    private readonly Dictionary<uint, (InventoryType Bag, uint Slot)> bagIndex = [];

    private bool built;
    // 状态文字：键 + 参数 + 是否异常（SPEC 第 8 节），渲染时再取本地化文案
    private string statusKey    = "StatusNotScanned";
    private object[] statusArgs = [];
    private bool statusBad;
    private string search = string.Empty;

    private string? copiedText;
    private double copiedAt;

    #endregion

    #region 自动使用

    private uint? pendingItemId;
    private DateTime pendingSinceUtc;
    private int pendingRetries;
    private DateTime nextAutoUseUtc   = DateTime.MinValue;
    private DateTime nextBagRefreshUtc = DateTime.MinValue;
    private readonly HashSet<uint> skippedItems = [];
    private bool autoIdleNoted;

    private ICondition condition;
    private bool conditionResolved;

    #endregion

    #region 窗口

    private bool collapsed;
    private bool resizePending;
    private System.Numerics.Vector2 expandedSize = new(760f, 560f);
    private Dalamud.Interface.ManagedFontAtlas.IFontHandle omniFont;

    #endregion

    #region 生命周期

    protected override void OnStart(FeatureLifetime activation)
    {
        this.MigrateConfig();

        var uiBuilder = DalamudServices.PluginInterface.UiBuilder;
        activation.OnStopping(() => uiBuilder.Draw -= this.DrawWindow);
        uiBuilder.Draw += this.DrawWindow;

        var framework = DalamudServices.Framework;
        activation.OnStopping(() => framework.Update -= this.OnFrameworkUpdate);
        framework.Update += this.OnFrameworkUpdate;

        // 短命令：注册在 OnStart，注销交给 activation.OnStopping
        activation.OnStopping(this.UnregisterShortCommand);
        this.RegisterShortCommand();

        this.ResetSessionState();

        this.SetStatus("StatusStarting");
        this.Scan();
    }

    protected override void OnStop() => this.ResetSessionState();

    /// <summary>
    /// 每次启用都会拿到新的 FeatureLifetime，所以本次启用的 pending / 计数 / 标志位在这里归零；
    /// 目录与成本表是可复用的缓存（catalog / costMap），不随启用次数清空。
    /// </summary>
    private void ResetSessionState()
    {
        this.pendingItemId     = null;
        this.pendingRetries    = 0;
        this.pendingSinceUtc   = DateTime.MinValue;
        this.nextAutoUseUtc    = DateTime.MinValue;
        this.nextBagRefreshUtc = DateTime.MinValue;
        this.autoIdleNoted     = false;
        this.skippedItems.Clear();

        // 服务可能随插件重载变化，重新解析
        this.condition         = null;
        this.conditionResolved = false;
    }

    /// <summary>失败态状态文字的颜色（原版没有判色，这里只给异常态上色）。</summary>
    private static readonly System.Numerics.Vector4 StatusErrorColor = new(0.95f, 0.45f, 0.45f, 1f);

    private string StatusText =>
        this.statusArgs.Length == 0
            ? OmniLoc.Get(this, this.statusKey)
            : string.Format(OmniLoc.Get(this, this.statusKey), this.statusArgs);

    private void SetStatus(string key, bool bad = false, params object[] args)
    {
        this.statusKey  = key;
        this.statusArgs = args ?? [];
        this.statusBad  = bad;
    }

    /// <summary>
    /// 配置迁移：1.0.2 起「已使用」的书默认不再显示（1.0.1 及以前默认显示）。
    /// 老配置没有 ConfigVersion（默认 0），启用时应用一次；用户之后手动改过的值以保存的配置为准。
    /// </summary>
    private void MigrateConfig()
    {
        if (this.config.ConfigVersion >= 1)
            return;

        this.config.ShowUsed      = false;
        this.config.ConfigVersion = 1;
    }

    public override bool ResetSettings()
    {
        this.config.WindowOpen           = false;
        this.config.DockToOmni           = true;
        this.config.ShowUsed             = false;
        this.config.AutoUseFolklore      = false;
        this.config.AutoUseSecretRecipe  = false;
        this.config.BookGapMs            = 200;
        this.collapsed                   = false;
        this.resizePending               = true;
        return true;
    }

    private void RegisterShortCommand()
    {
        try
        {
            this.shortCommandManager =
                DalamudServices.PluginInterface.GetService(typeof(ICommandManager)) as ICommandManager;
            if (this.shortCommandManager == null)
            {
                this.SetStatus("StatusShortCommandFailed", true);
                return;
            }

            this.shortCommandRegistered = this.shortCommandManager.AddHandler(
                ShortCommand,
                new CommandInfo(this.OnShortCommand)
                {
                    HelpMessage = OmniLoc.Get(this, "ShortCommandHelp"),
                });
        }
        catch (Exception ex)
        {
            this.shortCommandRegistered = false;
            DalamudServices.PluginLog.Warning(ex, "[LKami9秘录助手] 注册 /L9ML 失败");
        }
    }

    private void UnregisterShortCommand()
    {
        if (!this.shortCommandRegistered) return;
        try { this.shortCommandManager?.RemoveHandler(ShortCommand); }
        catch { }
        this.shortCommandRegistered = false;
        this.shortCommandManager = null;
    }

    private void OnShortCommand(string command, string arguments) =>
        this.TryHandleCommand(arguments);

    public override bool TryHandleCommand(string arguments)
    {
        var text  = (arguments ?? string.Empty).Trim();
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd   = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (cmd)
        {
            case "":
                this.ToggleWindow(true);
                return true;

            case "window":
                this.ToggleWindow(!this.config.WindowOpen);
                return true;

            case "scan":
            case "refresh":
                this.skippedItems.Clear();
                this.Scan();
                this.ToggleWindow(true);
                return true;

            case "close":
            case "hide":
                this.ToggleWindow(false);
                return true;

            default:
                return false;
        }
    }

    private void ToggleWindow(bool open)
    {
        if (open && !this.built) this.Scan();
        this.config.WindowOpen = open;
        if (open && this.collapsed)
        {
            this.collapsed     = false;
            this.resizePending = true;
        }
    }

    #endregion

    #region 检测

    private void EnsureCatalog()
    {
        if (this.catalogBuilt) return;

        try
        {
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();

            // 同一个 ItemAction 行只解析一次 ValueNullable：全表几万行、 distinct ItemAction 行很少，
            // 避免对每个道具都做一次 RowRef 解析。
            var kindByItemActionRow = new Dictionary<uint, int>();

            foreach (var item in items)
            {
                if (item.RowId == 0 || !item.ItemAction.IsValid)
                    continue;

                var itemActionRow = item.ItemAction.RowId;
                if (!kindByItemActionRow.TryGetValue(itemActionRow, out var kind))
                {
                    kind = item.ItemAction.ValueNullable is { } action
                        ? action.Action.RowId switch
                        {
                            FolkloreActionRowId     => 0,
                            SecretRecipeActionRowId => 1,
                            _                       => -1,
                        }
                        : -1;
                    kindByItemActionRow[itemActionRow] = kind;
                }

                if (kind < 0)
                    continue;

                var book = new Book(item.RowId, item.Name.ToString(), item.Icon, kind == 0);
                this.catalog.Add(book);
                this.catalogById[book.ItemId] = book;
            }

            // 全部成功才置位；中途异常时清空，下次重试
            this.catalogBuilt = true;
        }
        catch (Exception ex)
        {
            this.catalog.Clear();
            this.catalogById.Clear();
            this.catalogBuilt = false;
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 建立目录失败");
        }
    }

    private void EnsureCostMap()
    {
        if (this.costMapBuilt) return;

        try
        {
            var known = this.catalog.Select(b => b.ItemId).ToHashSet();
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();

            foreach (var shop in DalamudServices.DataManager.GetExcelSheet<SpecialShop>())
            {
                foreach (var offer in shop.Item)
                {
                    if (offer.ItemCosts.Count == 0)
                        continue;

                    foreach (var receive in offer.ReceiveItems)
                    {
                        var receiveId = receive.Item.RowId;

                        if (receiveId == 0 || !known.Contains(receiveId))
                            continue;

                        if (!this.costMap.TryGetValue(receiveId, out var list))
                            this.costMap[receiveId] = list = [];

                        foreach (var cost in offer.ItemCosts)
                        {
                            var currencyId = cost.ItemCost.RowId;
                            if (currencyId == 0)
                                continue;

                            var count = cost.CurrencyCost != 0 ? cost.CurrencyCost : cost.CollectabilityCost;
                            if (count == 0 || list.Any(c => c.ItemId == currencyId))
                                continue;

                            var name = items.TryGetRow(currencyId, out var row) ? row.Name.ToString() : $"#{currencyId}";
                            list.Add(new Cost(currencyId, name, count));
                        }
                    }
                }
            }

            // 目录建好后补上每本书的成本展示 / 复制文本（Scan 时算一次，界面每帧直接取）
            foreach (var book in this.catalog)
            {
                if (!this.costMap.TryGetValue(book.ItemId, out var costs) || costs.Count == 0)
                    continue;

                book.CostText = string.Format(
                    OmniLoc.Get(this, "CostText"),
                    string.Join(" + ", costs.Select(c => $"{c.Name} ×{c.Count}")));
                book.CostCopy = string.Join("、", costs.Select(c => $"{c.Name} ×{c.Count}"));
            }

            this.costMapBuilt = true;
        }
        catch (Exception ex)
        {
            this.costMap.Clear();
            this.costMapBuilt = false;
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 解析兑换表失败");
        }
    }

    private void Scan()
    {
        try
        {
            this.EnsureCatalog();
            this.EnsureCostMap();
            this.RefreshBagIndex();

            var items = DalamudServices.DataManager.GetExcelSheet<Item>();
            this.rows.Clear();
            this.rowByItem.Clear();

            foreach (var book in this.catalog)
            {
                var used = false;

                try
                {
                    if (items.TryGetRow(book.ItemId, out var row))
                        used = DalamudServices.UnlockState.IsItemUnlocked(row);
                }
                catch (Exception ex)
                {
                    DalamudServices.PluginLog.Debug(ex, $"[LKami9秘录助手] 读取 {book.Name} 状态失败");
                }

                var entry = new BookRow { Book = book, Used = used, InBag = this.bagItems.Contains(book.ItemId) };
                this.rows.Add(entry);
                this.rowByItem[book.ItemId] = entry;
            }

            this.ReadHoldings();
            this.Regroup();

            var folklore     = this.rows.Count(r => r.Book.IsFolklore);
            var usedFolklore = this.rows.Count(r => r.Book.IsFolklore && r.Used);
            var recipe       = this.rows.Count - folklore;
            var usedRecipe   = this.rows.Count(r => !r.Book.IsFolklore && r.Used);

            this.built = true;
            this.SetStatus("StatusScanDone", false, usedFolklore, folklore, usedRecipe, recipe);
            DalamudServices.PluginLog.Information(
                $"[LKami9秘录助手] 检测完成：传承录 {usedFolklore}/{folklore}，秘籍 {usedRecipe}/{recipe}");
        }
        catch (Exception ex)
        {
            this.SetStatus("StatusScanFailed", true, ex.Message);
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 检测失败");
        }
    }

    /// <summary>从 rows / holdings 重算分组、缺口汇总与其他持有（局部刷新也走这里）。</summary>
    private void Regroup()
    {
        var needed = new Dictionary<uint, long>();
        var names  = new Dictionary<uint, string>();

        this.alreadyInBag = 0;

        foreach (var row in this.rows)
        {
            if (row.Used)
                continue;

            if (row.InBag)
            {
                this.alreadyInBag++;
                continue;
            }

            if (!this.costMap.TryGetValue(row.Book.ItemId, out var costs))
                continue;

            foreach (var cost in costs)
            {
                needed[cost.ItemId] = needed.GetValueOrDefault(cost.ItemId) + cost.Count;
                names[cost.ItemId]  = cost.Name;
            }
        }

        var needs = new List<Need>();
        foreach (var kv in needed)
        {
            var held = 0L;
            foreach (var h in this.currencyHoldings) if (h.ItemId == kv.Key) held += h.Count;
            foreach (var h in this.itemHoldings)     if (h.ItemId == kv.Key) held += h.Count;
            needs.Add(new Need(kv.Key, names[kv.Key], kv.Value, held));
        }

        needs.Sort((a, b) => b.Required.CompareTo(a.Required));
        this.needs = needs;

        this.otherCurrencies = this.currencyHoldings
            .Where(h => !needed.ContainsKey(h.ItemId))
            .Take(12)
            .ToList();

        this.RebuildViews();
    }

    /// <summary>按搜索词过滤四个分组（界面每帧直接画缓存列表，不再现算）。</summary>
    private void RebuildViews()
    {
        var kw = this.search.Trim();

        this.folkloreUnusedView = View(this.rows.Where(r => r.Book.IsFolklore && !r.Used), kw);
        this.folkloreUsedView   = View(this.rows.Where(r => r.Book.IsFolklore && r.Used), kw);
        this.recipeUnusedView   = View(this.rows.Where(r => !r.Book.IsFolklore && !r.Used), kw);
        this.recipeUsedView     = View(this.rows.Where(r => !r.Book.IsFolklore && r.Used), kw);
    }

    private static List<BookRow> View(IEnumerable<BookRow> source, string kw)
    {
        var list = kw.Length == 0
            ? source.ToList()
            : source.Where(r => r.Book.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Book.Name, b.Book.Name));
        return list;
    }

    private unsafe void ReadHoldings()
    {
        var currency = new Dictionary<uint, Holding>();
        var bagged   = new Dictionary<uint, Holding>();

        try
        {
            var items     = DalamudServices.DataManager.GetExcelSheet<Item>();
            var container = GetContainer(InventoryType.Currency);

            if (container != null)
            {
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var count = slot->GetQuantity();
                    if (count == 0)
                        continue;

                    var name = items.TryGetRow(slot->ItemId, out var row) ? row.Name.ToString() : $"#{slot->ItemId}";
                    AddHolding(currency, slot->ItemId, name, count);
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 读取货币失败");
        }

        try
        {
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();

            foreach (var bag in PlayerBags)
            {
                var container = GetContainer(bag);
                if (container == null)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var count = slot->GetQuantity();
                    if (count == 0)
                        continue;

                    var name = items.TryGetRow(slot->ItemId, out var row) ? row.Name.ToString() : $"#{slot->ItemId}";
                    AddHolding(bagged, slot->ItemId, name, count);
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 读取背包失败");
        }

        this.currencyHoldings = currency.Values.OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        this.itemHoldings     = bagged.Values.ToList();
    }

    private static void AddHolding(Dictionary<uint, Holding> map, uint itemId, string name, uint count)
    {
        if (map.TryGetValue(itemId, out var existing))
            existing.Count += count;
        else
            map[itemId] = new Holding(itemId, name, count);
    }

    #endregion

    #region 背包

    private static unsafe InventoryContainer* GetContainer(InventoryType type)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        var container = manager->GetInventoryContainer(type);
        return container != null && container->IsLoaded ? container : null;
    }

    /// <summary>刷新背包快照：bagItems 用于“是否持有”，bagIndex 用于 UseItem 定位槽位。</summary>
    private unsafe void RefreshBagIndex()
    {
        this.bagItems.Clear();
        this.bagIndex.Clear();

        foreach (var bag in PlayerBags)
        {
            var container = GetContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                this.bagItems.Add(slot->ItemId);
                if (!this.bagIndex.ContainsKey(slot->ItemId))
                    this.bagIndex[slot->ItemId] = (bag, (uint)i);
            }
        }
    }

    private static unsafe bool FindInBags(uint itemId)
    {
        foreach (var bag in PlayerBags)
        {
            var container = GetContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    return true;
            }
        }

        return false;
    }

    #endregion

    #region 自动使用

    private int GapMs => Math.Clamp(this.config.BookGapMs, 50, 1_500);

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 有待确认的使用请求时优先处理（手动“使用”按钮也走这条）
        if (this.pendingItemId is { } pendingId)
        {
            this.CheckPending(pendingId);
            return;
        }

        if (!this.config.AutoUseFolklore && !this.config.AutoUseSecretRecipe)
            return;

        if (DateTime.UtcNow < this.nextAutoUseUtc)
            return;

        // 背包快照约 1 秒刷新一次：新拿到的书能及时自动使用，槽位变化也不会长期用过期的
        if (DateTime.UtcNow >= this.nextBagRefreshUtc)
        {
            this.nextBagRefreshUtc = DateTime.UtcNow.AddSeconds(1);
            this.RefreshBagIndex();
        }

        if (!this.CanActNow())
            return;

        if (this.config.AutoUseFolklore && this.TryUseNext(true))
        {
            this.autoIdleNoted = false;
            return;
        }

        if (this.config.AutoUseSecretRecipe && this.TryUseNext(false))
        {
            this.autoIdleNoted = false;
            return;
        }

        if (!this.autoIdleNoted)
        {
            this.autoIdleNoted = true;
            this.SetStatus("StatusAutoIdle");
        }
    }

    /// <summary>战斗 / 读条 / 占用 / 切区时不对游戏发使用请求，等状态恢复正常再继续。</summary>
    private bool CanActNow()
    {
        if (!DalamudServices.PlayerState.IsLoaded)
            return false;

        if (!this.conditionResolved)
        {
            this.conditionResolved = true;
            try
            {
                this.condition = DalamudServices.PluginInterface.GetService(typeof(ICondition)) as ICondition;
            }
            catch
            {
                this.condition = null;
            }
        }

        if (this.condition == null)
            return true;

        try
        {
            return !(this.condition[ConditionFlag.InCombat]
                  || this.condition[ConditionFlag.Casting]
                  || this.condition[ConditionFlag.Occupied]
                  || this.condition[ConditionFlag.BetweenAreas]);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 等待一本使用中的书确认解锁：
    ///   · 已解锁 → 局部刷新该行，按间隔继续下一本；
    ///   · 书不在背包了（被存仓库等）→ 放弃等待；
    ///   · 0.6 秒还没解锁 → 认为请求被游戏忽略，重发（最多 2 次）；
    ///   · 超过 5 秒仍不解锁 → 跳过这本，避免卡死整个自动流程。
    /// </summary>
    private void CheckPending(uint pendingId)
    {
        var elapsed = DateTime.UtcNow - this.pendingSinceUtc;

        if (this.IsUnlocked(pendingId))
        {
            this.pendingItemId  = null;
            this.ConfirmUsed(pendingId);
            this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(this.GapMs);
            return;
        }

        if (!FindInBags(pendingId))
        {
            this.pendingItemId  = null;
            this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(this.GapMs);
            return;
        }

        if (elapsed > NoEffectWindow && this.pendingRetries < 2)
        {
            this.pendingRetries++;
            this.pendingSinceUtc = DateTime.UtcNow;
            this.nextAutoUseUtc  = DateTime.UtcNow.AddMilliseconds(150);
            this.TryUseItem(pendingId);
            return;
        }

        if (elapsed > UseTimeout)
        {
            var name = this.rowByItem.TryGetValue(pendingId, out var row) ? row.Book.Name : $"#{pendingId}";
            this.skippedItems.Add(pendingId);
            this.pendingItemId  = null;
            this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(500);
            this.SetStatus("StatusUseSkipped", true, name);
        }
    }

    private bool TryUseNext(bool folklore)
    {
        foreach (var book in this.catalog)
        {
            if (book.IsFolklore != folklore)
                continue;

            if (!this.rowByItem.TryGetValue(book.ItemId, out var row) || row.Used)
                continue;

            if (this.skippedItems.Contains(book.ItemId))
                continue;

            if (!this.bagItems.Contains(book.ItemId))
                continue;

            if (this.TryUseItem(book.ItemId))
                return true;
        }

        return false;
    }

    /// <summary>对背包里一本未使用的书发使用请求。槽位先做新鲜度校验，过期就放弃（等下一秒的快照）。</summary>
    private unsafe bool TryUseItem(uint itemId)
    {
        if (this.pendingItemId != null)
            return false;

        if (!this.rowByItem.TryGetValue(itemId, out var row) || row.Used)
            return false;

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return false;

        if (!this.bagIndex.TryGetValue(itemId, out var loc))
            return false;

        var container = GetContainer(loc.Bag);
        if (container == null || loc.Slot >= container->Size)
            return false;

        var slot = container->GetInventorySlot((int)loc.Slot);
        if (slot == null || slot->ItemId != itemId)
            return false;

        try
        {
            agent->UseItem(itemId, loc.Bag, loc.Slot);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 使用道具失败");
            return false;
        }

        this.pendingItemId   = itemId;
        this.pendingSinceUtc = DateTime.UtcNow;
        this.pendingRetries  = 0;
        this.nextAutoUseUtc  = DateTime.UtcNow.AddMilliseconds(this.GapMs);
        this.SetStatus("StatusUsing", false, row.Book.Name);
        return true;
    }

    /// <summary>确认解锁后局部刷新：只改这一行的状态并重算汇总，不做全量重扫。</summary>
    private void ConfirmUsed(uint itemId)
    {
        if (!this.rowByItem.TryGetValue(itemId, out var row))
            return;

        row.Used = true;
        this.Regroup();

        var remaining = this.rows.Count(r => !r.Used);
        if (remaining == 0)
            this.SetStatus("StatusAllUsed");
        else
            this.SetStatus("StatusUsedOne", false, row.Book.Name, remaining);
    }

    private bool IsUnlocked(uint itemId)
    {
        try
        {
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();
            return items.TryGetRow(itemId, out var row) && DalamudServices.UnlockState.IsItemUnlocked(row);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 界面（独立窗口）

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextUnformatted(OmniLoc.Get(this, "Title"));
        ImGui.TextDisabled(OmniLoc.Get(this, "SettingsHint"));

        ImGui.Spacing();

        if (ImGui.Button(OmniLoc.Get(this, this.config.WindowOpen ? "CloseWindow" : "OpenWindow")))
        {
            this.ToggleWindow(!this.config.WindowOpen);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button(OmniLoc.Get(this, "DetectNow")))
        {
            this.skippedItems.Clear();
            this.Scan();
        }

        var autoFolklore = this.config.AutoUseFolklore;
        if (ImGui.Checkbox(OmniLoc.Get(this, "AutoUseFolklore"), ref autoFolklore))
        {
            this.config.AutoUseFolklore = autoFolklore;
            this.autoIdleNoted = false;
            changed = true;
        }

        var autoRecipe = this.config.AutoUseSecretRecipe;
        if (ImGui.Checkbox(OmniLoc.Get(this, "AutoUseRecipe"), ref autoRecipe))
        {
            this.config.AutoUseSecretRecipe = autoRecipe;
            this.autoIdleNoted = false;
            changed = true;
        }

        var gap = this.config.BookGapMs;
        ImGui.SetNextItemWidth(200 * OmniTheme.Scale(1f));
        if (ImGui.SliderInt(OmniLoc.Get(this, "GapLabel"), ref gap, 50, 1_500))
        {
            this.config.BookGapMs = gap;
            changed = true;
        }

        var dock = this.config.DockToOmni;
        if (ImGui.Checkbox(OmniLoc.Get(this, "DockToOmni"), ref dock))
        {
            this.config.DockToOmni = dock;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextDisabled(this.StatusText);

        return changed;
    }

    /// <summary>扫当前 ImGui 上下文，取名字含 Omni、本帧活跃、面积最大的根窗口。</summary>
    private static unsafe ImGuiWindowPtr FindOmniMainWindow()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.Handle == null) return ImGuiWindowPtr.Null;

        var list  = ctx.Handle->Windows;
        var frame = ctx.Handle->FrameCount;
        var best  = ImGuiWindowPtr.Null;

        for (var i = 0; i < list.Size; i++)
        {
            var w = list[i];
            if (w.IsNull) continue;
            if (w.Handle->Hidden != 0 || w.Handle->IsFallbackWindow != 0) continue;
            if (w.Handle->ParentWindow != null) continue;
            if (w.Handle->LastFrameActive != frame) continue;

            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)w.Handle->Name) ?? string.Empty;
            if (!name.Contains("Omni", StringComparison.OrdinalIgnoreCase)) continue;

            if (best.IsNull ||
                w.Handle->Size.X * w.Handle->Size.Y > best.Handle->Size.X * best.Handle->Size.Y)
                best = w;
        }

        return best;
    }

    private unsafe System.Numerics.Vector2 ComputeDockedPosition(
        ImGuiWindowPtr omni, System.Numerics.Vector2 mySize)
    {
        var vp       = ImGui.GetMainViewport();
        var gap      = OmniTheme.Scale(12f);
        var fallback = OmniTheme.ClampWindowPosition(
            vp.WorkPos + vp.WorkSize - mySize - new System.Numerics.Vector2(gap, gap), mySize);

        if (omni.IsNull) return fallback;

        var oPos = omni.Handle->Pos;
        var oSz  = omni.Handle->Size;
        var candidates = new[]
        {
            new System.Numerics.Vector2(oPos.X + oSz.X + gap, oPos.Y),
            new System.Numerics.Vector2(oPos.X - mySize.X - gap, oPos.Y),
            new System.Numerics.Vector2(oPos.X, oPos.Y + oSz.Y + gap),
        };

        foreach (var cand in candidates)
        {
            var clamped = OmniTheme.ClampWindowPosition(cand, mySize);
            if (MathF.Abs(clamped.X - cand.X) < 1f &&
                MathF.Abs(clamped.Y - cand.Y) < 1f &&
                !Overlaps(clamped, mySize, oPos, oSz))
                return clamped;
        }

        return fallback;
    }

    private static bool Overlaps(
        System.Numerics.Vector2 aPos, System.Numerics.Vector2 aSize,
        System.Numerics.Vector2 bPos, System.Numerics.Vector2 bSize) =>
        aPos.X < bPos.X + bSize.X && aPos.X + aSize.X > bPos.X &&
        aPos.Y < bPos.Y + bSize.Y && aPos.Y + aSize.Y > bPos.Y;

    private static void DrawThemedRule(System.Numerics.Vector4 color)
    {
        var pos   = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var y     = pos.Y + OmniTheme.Scale(1f);
        ImGui.GetWindowDrawList().AddLine(pos with { Y = y }, new System.Numerics.Vector2(pos.X + width, y),
            ImGui.GetColorU32(color));
        ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.ContentGap()));
    }

    private static void FillCurrentWindow(System.Numerics.Vector4 color, float radius)
    {
        var pos  = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size,
            ImGui.GetColorU32(Opaque(color)), radius);
    }

    private unsafe void DrawWindow()
    {
        if (!this.config.WindowOpen) return;

        var viewport = ImGui.GetMainViewport();
        var open     = this.config.WindowOpen;

        var targetSize = this.collapsed
            ? new System.Numerics.Vector2(OmniTheme.Scale(460f), OmniTheme.TitleBarHeight() + OmniTheme.Scale(14f))
            : OmniTheme.ClampWindowSize(this.expandedSize);

        // 折叠时目标高度只有标题栏那么高，必须同步放小最小尺寸约束，
        // 否则会被 MainWindowMinSize 夹回大尺寸，出现“折叠了但窗体还是很大”的问题
        if (this.collapsed)
        {
            ImGui.SetNextWindowSizeConstraints(
                new System.Numerics.Vector2(OmniTheme.Scale(280f), OmniTheme.TitleBarHeight() + OmniTheme.Scale(8f)),
                viewport.WorkSize);
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(OmniTheme.MainWindowMinSize(), viewport.WorkSize);
        }

        if (this.resizePending)
        {
            ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
            this.resizePending = false;
        }
        else if (!this.collapsed)
        {
            ImGui.SetNextWindowSize(OmniTheme.ClampWindowSize(this.expandedSize), ImGuiCond.FirstUseEver);
        }

        var omni = this.config.DockToOmni ? FindOmniMainWindow() : ImGuiWindowPtr.Null;

        if (this.config.DockToOmni)
        {
            var dockPos = this.ComputeDockedPosition(omni, targetSize);
            OmniWindowChrome.SetNextWindowPosition(WindowId, dockPos);
            ImGui.SetNextWindowPos(dockPos, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowPos(
                OmniTheme.ClampWindowPosition(
                    viewport.WorkPos + viewport.WorkSize - targetSize,
                    targetSize),
                ImGuiCond.FirstUseEver);
        }

        var self = ImGuiWindowPtr.Null;

        using var style = new OmniStyleScope();
        PushOmniStyle(style);

        // 玩家 UI 字体：直接用 Dalamud 的插件默认字体句柄（原 OmniFonts 辅助类型在宿主里不存在）
        this.omniFont ??= TreeToolKit.Fonts.GetUIFont();
        IDisposable fontScope = this.omniFont is { Available: true } ? this.omniFont.Push() : null;

        try
        {
            if (ImGui.Begin(WindowId, ref open,
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoDocking  | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoBringToFrontOnFocus))
            {
                self = ImGuiP.GetCurrentWindow();

                var framePos  = ImGui.GetWindowPos();
                var frameSize = ImGui.GetWindowSize();
                var tokens    = OmniTheme.Tokens;
                var radius    = OmniTheme.Scale(tokens.BorderRadius);
                var draw      = ImGui.GetWindowDrawList();

                draw.AddRectFilled(framePos, framePos + frameSize,
                    ImGui.GetColorU32(Opaque(tokens.Background)), radius);

                var chrome = OmniWindowChrome.Draw(
                    framePos, frameSize, this.collapsed,
                    OmniLoc.Get(this, "WindowTitle"), CollapseId, CloseId, true);

                if (this.config.DockToOmni)
                {
                    // 贴靠每帧强制位置；用户在标题栏按下并拖动就自动解除贴靠
                    var titleHeight = OmniTheme.TitleBarHeight();
                    var inTitle = ImGui.IsWindowHovered()
                               && ImGui.GetIO().MousePos.Y < framePos.Y + titleHeight;

                    if (inTitle && ImGui.IsMouseDragging(ImGuiMouseButton.Left, OmniTheme.Scale(3f)))
                        this.config.DockToOmni = false;
                }

                if (chrome.CloseClicked)
                {
                    this.config.WindowOpen = false;
                    this.collapsed         = false;
                    this.resizePending     = true;
                }

                if (chrome.ToggleCollapse)
                {
                    if (this.collapsed)
                    {
                        this.collapsed     = false;
                        this.resizePending = true;
                    }
                    else
                    {
                        this.expandedSize  = frameSize;
                        this.collapsed     = true;
                        this.resizePending = true;
                    }
                }
                else if (!this.collapsed)
                {
                    this.expandedSize = frameSize;
                }

                if (!this.collapsed)
                {
                    var (contentPos, contentSize) = OmniWindowChrome.GetContentBounds(framePos, frameSize);
                    if (contentSize.X > 1f && contentSize.Y > 1f)
                    {
                        var panel = PanelColor(tokens);

                        ImGui.SetCursorScreenPos(contentPos);
                        ImGui.BeginChild("###LKami9MiLuContent", contentSize, false, ImGuiWindowFlags.None);
                        FillCurrentWindow(panel, radius);
                        this.DrawContent();
                        ImGui.EndChild();
                    }
                }
            }

            ImGui.End();
        }
        finally
        {
            fontScope?.Dispose();
        }

        if (!self.IsNull && this.config.DockToOmni && !omni.IsNull)
            ImGuiP.BringWindowToDisplayBehind(self, omni);

        if (!open) this.config.WindowOpen = false;
    }

    private void DrawContent()
    {
        var tokens = OmniTheme.Tokens;
        var pad    = MathF.Max(OmniTheme.Scale(8f), OmniTheme.WindowInset());
        var cText  = Opaque(tokens.Text);

        ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.ContentGap()));
        ImGui.Indent(pad);

        // 工具行：搜索 + 检测 + 显示已使用
        var sText = this.search;
        if (OmniControls.InputTextWithHint("###mlsearch", OmniLoc.Get(this, "SearchHint"), ref sText, 120, OmniTheme.Scale(200f)))
        {
            this.search = sText;
            this.RebuildViews();
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "DetectButton"), false))
        {
            this.skippedItems.Clear();
            this.Scan();
        }

        ImGui.SameLine();
        var showUsed = this.config.ShowUsed;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "ShowUsed"), ref showUsed))
        {
            this.config.ShowUsed = showUsed;
        }

        // 自动使用快捷开关
        var autoFolklore = this.config.AutoUseFolklore;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "AutoUseFolkloreShort"), ref autoFolklore))
        {
            this.config.AutoUseFolklore = autoFolklore;
            this.autoIdleNoted = false;
        }

        ImGui.SameLine();
        var autoRecipe = this.config.AutoUseSecretRecipe;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "AutoUseRecipeShort"), ref autoRecipe))
        {
            this.config.AutoUseSecretRecipe = autoRecipe;
            this.autoIdleNoted = false;
        }

        ImGui.Spacing();
        ImGui.TextColored(this.statusBad ? StatusErrorColor : cText, this.StatusText);

        if (this.copiedText is { } copied && (ImGui.GetTime() - this.copiedAt) < 2.5)
        {
            ImGui.SameLine();
            ImGui.TextColored(tokens.Success, string.Format(OmniLoc.Get(this, "Copied"), copied));
        }

        DrawThemedRule(tokens.Border);

        // 汇总放在书单上面：先看“还差什么”，再翻书单
        this.DrawSummary(tokens, cText);

        ImGui.Spacing();
        this.DrawBookGroup("###mlFolklore", OmniLoc.Get(this, "GroupFolklore"),
            this.folkloreUnusedView, this.folkloreUsedView, tokens, cText);

        ImGui.Spacing();
        this.DrawBookGroup("###mlRecipe", OmniLoc.Get(this, "GroupRecipe"),
            this.recipeUnusedView, this.recipeUsedView, tokens, cText);

        ImGui.Unindent(pad);
    }

    private void DrawBookGroup(string id, string title,
        List<BookRow> unused, List<BookRow> used,
        ThemeTokens tokens, System.Numerics.Vector4 cText)
    {
        if (!ImGui.CollapsingHeader(
                string.Format(OmniLoc.Get(this, "GroupHeader"), title, unused.Count) + id,
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();

        if (unused.Count == 0)
            ImGui.TextDisabled(OmniLoc.Get(this, this.search.Length > 0 ? "NoMatch" : "GroupAllUsed"));

        foreach (var row in unused)
            this.DrawBookRow(row, tokens, cText);

        if (this.config.ShowUsed && used.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(tokens.Success, OmniLoc.Get(this, "UsedDivider"));
            ImGui.TextWrapped(string.Join("、", used.Select(r => r.Book.Name)));
        }

        ImGui.Unindent();
    }

    private void DrawBookRow(BookRow row, ThemeTokens tokens, System.Numerics.Vector4 cText)
    {
        try
        {
            var texture = DalamudServices.TextureProvider
                .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(row.Book.Icon))
                .GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new System.Numerics.Vector2(22, 22) * OmniTheme.Scale(1f));
            ImGui.SameLine();
        }
        catch
        {
            // 图标失败就只显示文字
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(row.Book.Name);

        if (row.InBag)
        {
            ImGui.SameLine();
            ImGui.TextColored(tokens.Success, OmniLoc.Get(this, "Held"));
            ImGui.SameLine();
            if (OmniControls.SmallButton(OmniLoc.Get(this, "Use"), false))
            {
                if (!this.TryUseItem(row.Book.ItemId))
                    this.SetStatus("UseFailed", true);
            }
            return;
        }

        if (row.Book.CostText is { } cost)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(cost);
            this.ClickToCopy(row.Book.CostCopy);
        }
    }

    private void DrawSummary(ThemeTokens tokens, System.Numerics.Vector4 cText)
    {
        if (!ImGui.CollapsingHeader(
                OmniLoc.Get(this, "SummaryHeader") + "###mlSummary",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (this.needs.Count == 0)
        {
            ImGui.TextDisabled(this.rows.Count > 0 && this.rows.All(r => r.Used)
                ? OmniLoc.Get(this, "StatusAllUsed")
                : OmniLoc.Get(this, "SummaryEmpty"));
        }
        else
        {
            using var table = ImRaii.Table("###mlNeeds", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);

            if (table.Success)
            {
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColMaterial"), ImGuiTableColumnFlags.WidthFixed, 170 * OmniTheme.Scale(1f));
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColRequired"), ImGuiTableColumnFlags.WidthFixed, 78 * OmniTheme.Scale(1f));
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColHeld"), ImGuiTableColumnFlags.WidthFixed, 78 * OmniTheme.Scale(1f));
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColMissing"), ImGuiTableColumnFlags.WidthFixed, 78 * OmniTheme.Scale(1f));
                ImGui.TableHeadersRow();

                foreach (var need in this.needs)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(need.Name);
                    this.ClickToCopy(need.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(need.Required.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(need.Held.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextColored(NeedColor(tokens, need.Missing > 0), need.Missing.ToString());
                }
            }
        }

        if (this.alreadyInBag > 0)
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "OtherInBag"), this.alreadyInBag));

        if (this.otherCurrencies.Count > 0)
            ImGui.TextDisabled(string.Format(
                OmniLoc.Get(this, "OtherHoldings"),
                string.Join("   ", this.otherCurrencies.Select(h => $"{h.Name} ×{h.Count}"))));
    }

    private void ClickToCopy(string? text)
    {
        if (text is null)
            return;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(OmniLoc.Get(this, "ClickToCopy"), text));

        if (!ImGui.IsItemClicked())
            return;

        ImGui.SetClipboardText(text);
        this.copiedText = text;
        this.copiedAt   = ImGui.GetTime();
    }

    /// <summary>面板色：沿用 Omni 主内容区的深色底板。</summary>
    private static System.Numerics.Vector4 PanelColor(ThemeTokens t)
    {
        return System.Numerics.Vector4.Lerp(Opaque(t.Background), Opaque(t.Surface), 0.5f);
    }

    /// <summary>
    /// “还差”列配色：红 = 有缺口，绿 = 已够。
    /// 按主题背景明暗自动切换深浅变体：浅色主题下用深红深绿（浅红浅绿在浅底上对比度不足），
    /// 深色主题下用浅红浅绿。
    /// </summary>
    private static System.Numerics.Vector4 NeedColor(ThemeTokens tokens, bool missing)
    {
        var bg   = Opaque(tokens.Background);
        var luma = (0.2126f * bg.X) + (0.7152f * bg.Y) + (0.0722f * bg.Z);

        return missing
            ? luma > 0.5f
                ? new System.Numerics.Vector4(0.80f, 0.12f, 0.12f, 1f)  // 浅色底：深红
                : new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f)    // 深色底：浅红
            : luma > 0.5f
                ? new System.Numerics.Vector4(0.10f, 0.52f, 0.24f, 1f)  // 浅色底：深绿
                : new System.Numerics.Vector4(0.55f, 1f, 0.55f, 1f);   // 深色底：浅绿
    }

    private static System.Numerics.Vector4 Opaque(System.Numerics.Vector4 c) => c with { W = 1f };

    /// <summary>Omni 外观作用域：自计数 push，Dispose 按计数 pop —— 结构上不可能泄漏。</summary>
    private sealed class OmniStyleScope : IDisposable
    {
        private int colors;
        private int vars;

        public void Color(ImGuiCol idx, System.Numerics.Vector4 c)
        {
            ImGui.PushStyleColor(idx, c);
            this.colors++;
        }

        public void Var(ImGuiStyleVar v, System.Numerics.Vector2 val)
        {
            ImGui.PushStyleVar(v, val);
            this.vars++;
        }

        public void Var(ImGuiStyleVar v, float val)
        {
            ImGui.PushStyleVar(v, val);
            this.vars++;
        }

        public void Dispose()
        {
            if (this.vars   > 0) ImGui.PopStyleVar(this.vars);
            if (this.colors > 0) ImGui.PopStyleColor(this.colors);
            this.vars   = 0;
            this.colors = 0;
        }
    }

    /// <summary>Omni 本体外观：颜色 + 样式变量（含滚动条），全部交给 scope 回收。</summary>
    private static void PushOmniStyle(OmniStyleScope style)
    {
        var t = OmniTheme.Tokens;

        var textDisabled    = t.Text with { W = t.Text.W * 0.55f };
        var titleBackground = System.Numerics.Vector4.Lerp(t.Background, t.Primary, 0.35f);
        var titleActive     = System.Numerics.Vector4.Lerp(t.Background, t.Accent,  0.65f);

        style.Color(ImGuiCol.WindowBg, Opaque(t.Background));
        style.Color(ImGuiCol.ChildBg, System.Numerics.Vector4.Zero);
        style.Color(ImGuiCol.PopupBg, Opaque(t.Background));
        style.Color(ImGuiCol.Border, t.Border);
        style.Color(ImGuiCol.Text, t.Text);
        style.Color(ImGuiCol.TextDisabled, textDisabled);
        style.Color(ImGuiCol.Button, t.Surface);
        style.Color(ImGuiCol.ButtonHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ButtonActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Header, t.Surface);
        style.Color(ImGuiCol.HeaderHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.HeaderActive, t.Accent);
        style.Color(ImGuiCol.FrameBg, t.Surface);
        style.Color(ImGuiCol.FrameBgHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.FrameBgActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Tab, t.Surface);
        style.Color(ImGuiCol.TabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.TabActive, t.Accent);
        style.Color(ImGuiCol.TitleBg, titleBackground);
        style.Color(ImGuiCol.TitleBgActive, titleActive);
        style.Color(ImGuiCol.TitleBgCollapsed, titleBackground);
        style.Color(ImGuiCol.ScrollbarBg, Opaque(t.Background));
        style.Color(ImGuiCol.ScrollbarGrab, t.Secondary);
        style.Color(ImGuiCol.ScrollbarGrabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ScrollbarGrabActive, t.Accent);
        style.Color(ImGuiCol.CheckMark, t.Success);
        style.Color(ImGuiCol.SliderGrab, t.Secondary);
        style.Color(ImGuiCol.SliderGrabActive, t.Accent);
        style.Color(ImGuiCol.Separator, t.Border);
        style.Color(ImGuiCol.ResizeGrip, t.Surface);
        style.Color(ImGuiCol.ResizeGripHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ResizeGripActive, t.Accent);

        // 表格（汇总表）配色：不设置的话表头会是 ImGui 默认深蓝色，和 Omni 主题割裂
        style.Color(ImGuiCol.TableHeaderBg, Opaque(t.Surface));
        style.Color(ImGuiCol.TableBorderStrong, t.Border);
        style.Color(ImGuiCol.TableBorderLight, t.Border);
        style.Color(ImGuiCol.TableRowBg, System.Numerics.Vector4.Zero);
        style.Color(ImGuiCol.TableRowBgAlt, Opaque(t.Surface) with { W = 0.35f });
        style.Color(ImGuiCol.TextSelectedBg, t.Accent with { W = 0.35f });
        style.Color(ImGuiCol.SeparatorHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.SeparatorActive, t.Accent);

        style.Var(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        style.Var(ImGuiStyleVar.WindowRounding, OmniTheme.Scale(t.BorderRadius));
        style.Var(ImGuiStyleVar.WindowBorderSize, OmniTheme.BorderThickness());
        style.Var(ImGuiStyleVar.FrameRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.PopupRounding, OmniTheme.Scale(t.BorderRadius));
        style.Var(ImGuiStyleVar.ScrollbarRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.GrabRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.FramePadding, OmniTheme.Scale(t.FramePadding));
        style.Var(ImGuiStyleVar.ItemSpacing, OmniTheme.Scale(t.ItemSpacing));
    }

    #endregion

    #region 数据模型

    private sealed class Book
    {
        public Book(uint itemId, string name, uint icon, bool isFolklore)
        {
            this.ItemId     = itemId;
            this.Name       = name;
            this.Icon       = icon;
            this.IsFolklore = isFolklore;
        }

        public uint ItemId { get; }
        public string Name { get; }
        public uint Icon { get; }
        public bool IsFolklore { get; }

        /// <summary>Scan 时预计算的成本展示 / 复制文本，避免界面每帧重复拼字符串。</summary>
        public string? CostText { get; set; }
        public string? CostCopy { get; set; }
    }

    private sealed class Cost(uint itemId, string name, uint count)
    {
        public uint ItemId { get; } = itemId;
        public string Name { get; } = name;
        public uint Count { get; } = count;
    }

    private sealed class Holding(uint itemId, string name, long count)
    {
        public uint ItemId { get; } = itemId;
        public string Name { get; } = name;
        public long Count { get; set; } = count;
    }

    private sealed class BookRow
    {
        public Book Book = null!;
        public bool Used;
        public bool InBag;
    }

    private sealed record Need(uint ItemId, string Name, long Required, long Held)
    {
        public long Missing => Math.Max(0, this.Required - this.Held);
    }

    #endregion

    private sealed class Settings
    {
        /// <summary>配置结构版本，用于一次性迁移（见 MigrateConfig）。</summary>
        public int ConfigVersion { get; set; }
        public bool WindowOpen { get; set; }
        /// <summary>贴靠 Omni 主窗口右侧；拖动标题栏可自动解除。</summary>
        public bool DockToOmni { get; set; } = true;
        public bool ShowUsed { get; set; }
        public bool AutoUseFolklore { get; set; }
        public bool AutoUseSecretRecipe { get; set; }
        public int BookGapMs { get; set; } = 200;
    }

}
