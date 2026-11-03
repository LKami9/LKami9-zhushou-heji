using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using Lumina.Excel.Sheets;

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
/// 检测角色已使用 / 未使用的传承录与秘籍，显示兑换所需材料、持有量与缺口。
///
/// 功能：
///   · 独立窗口（OmniToolbox.Common 的 OmniWindowChrome / OmniTheme / OmniControls 组件），
///     可折叠、可拖动标题栏移动；设置页只留配置项。
///   · 不注册任何聊天命令（Info.Commands 为空、也不加短命令），
///     入口是 Omni 模块设置页里的「打开窗口 / 立即检测 / 诊断」按钮。
///   · 自动使用：战斗 / 读条 / 占用 / 切区时暂停；0.6 秒未解锁才重发（最多 2 次）；
///     5 秒超时跳过该本；每确认一本局部刷新，全部完成自动汇总。
///   · 兑换材料持有量统计货币 / 水晶容器、四个背包、鞍囊与宇宙背包，并可叠 Omni 库存快照
///     （含雇员 / 收藏柜 / 部队箱 / 住宅缓存）；悬停材料名可看容器分布。
///   · 材料名交互：左键打开制作笔记（该材料可制作时）/ 右键查看 Omni 物品信息 / Shift+左键复制名称；
///     悬停可看持有分布（各容器数量），「持有」列同样可悬停。
///   · 窗口内按书名或材料名搜索，背包里未使用的书可单本使用。
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

    /// <summary>
    /// SpecialShop 的 CostType == 3 表示这一槽的 ItemCost 存的是「货币桶」编号，不是道具 ID。
    /// 实测（CN 客户端 7.x，146 本书的 253 条成本行）只有两种组合：
    ///   UseCurrencyType=16 & 桶 2 → 巧手紫票（33913），秘籍第三卷起
    ///   UseCurrencyType=16 & 桶 4 → 大地紫票（33914），传承录
    /// 未收录的组合会跳过并记日志，绝不按道具 ID 直接显示（否则会显示成「火之碎晶」）。
    /// </summary>
    private static readonly Dictionary<(byte CurrencyType, uint Bucket), uint> ScripBuckets = new()
    {
        [(16, 2)] = 33913,
        [(16, 4)] = 33914,
    };

    private static readonly TimeSpan NoEffectWindow = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan UseTimeout     = TimeSpan.FromSeconds(5);

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    /// <summary>
    /// 背包之外的常规物品容器：普通 / 高级鞍囊、宇宙背包。
    /// Omni 的库存快照也含鞍囊，但缓存可能过期，实时读一遍更稳（下面取值时用较大值）。
    /// </summary>
    private static readonly InventoryType[] ExtraItemContainers =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
        InventoryType.Cosmopouch1,
        InventoryType.Cosmopouch2,
    ];

    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Item,
        Author   = "LKami9",

        // 不注册任何命令：宿主的路由名是 InternalName（很长），短命令也不保留，
        // 入口统一走设置页按钮。留空列表而不是 null，避免宿主遍历时判空。
        Commands = [],
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"]       = "LKami9秘录助手",
            ["Description"] = "独立窗口：检测传承录 / 秘籍使用状态，汇总兑换材料、持有量与缺口，支持自动使用，材料名可点击复制。",

            ["WindowTitle"]  = "LKami9 秘录助手",
            ["SettingsHint"] = "主界面在独立窗口中（点上方按钮打开），配色与窗口样式跟随 Omni 本体主题。",

            ["OpenWindow"]   = "打开窗口",
            ["CloseWindow"]  = "关闭窗口",
            ["DetectNow"]    = "立即检测",
            ["Diagnose"]     = "诊断",
            ["DetectButton"] = "检测",
            ["SearchHint"]   = "搜索书名或材料",
            ["ShowUsed"]     = "显示已使用",

            ["AutoUseFolklore"]      = "自动使用传承录",
            ["AutoUseRecipe"]        = "自动使用秘籍",
            ["AutoUseFolkloreShort"] = "自动·传承录",
            ["AutoUseRecipeShort"]   = "自动·秘籍",
            ["GapLabel"]             = "每本之间的间隔 (毫秒)",

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
            ["OtherInBag"]      = "（另有 {0} 本已在背包里，未计入）",
            ["OtherElsewhere"]  = "（另有 {0} 本在鞍囊 / 雇员 / 部队箱等位置，未计入，取出来即可使用）",
            ["OtherHoldingsPrefix"] = "其他持有：",
            ["OmniCacheNote"]   = "持有量含 Omni 库存缓存（鞍囊 / 雇员 / 收藏柜 / 部队箱），采集于 {0}",
            ["Loc.Inventory"]        = "背包",
            ["Loc.Currency"]         = "货币",
            ["Loc.Crystals"]         = "水晶",
            ["Loc.Cosmopouch"]       = "宇宙背包",
            ["Loc.Armory"]           = "兵装库",
            ["Loc.Equipped"]         = "装备中",
            ["Loc.Saddlebag"]        = "鞍囊",
            ["Loc.Retainer"]         = "雇员",
            ["Loc.RetainerMarket"]   = "雇员在售",
            ["Loc.GlamourDresser"]   = "幻化台",
            ["Loc.Armoire"]          = "收藏柜",
            ["Loc.FreeCompanyChest"] = "部队箱",
            ["Loc.HousingStoreroom"] = "住宅仓库",
            ["OmniCacheMissing"] = "未取到 Omni 库存缓存，持有量仅统计背包与货币。",
            ["HeldElsewhereRow"] = "在别处 {0} 本，取出后可使用",
            ["Retrieve"]         = "取回",
            ["StatusRetrieveRequested"] = "已请求取回：{0}",
            ["StatusRetrieveFailed"]    = "取回失败：{0}",
            ["RetrieveNoIpc"]           = "Omni 未提供自动存取接口（请确认已启用 Omni 的自动存取模块）",
            ["OnlyMissing"]      = "只看有缺口",
            ["CopyMissing"]      = "复制缺口清单",
            ["StatusCopiedMissing"] = "已复制 {0} 项缺口到剪贴板。",
            ["StatusCopiedNothing"] = "当前没有缺口可复制。",
            ["StatusConditionUnavailable"] = "读取游戏状态失败，自动使用已暂停（详见日志）。",

            ["CostText"]    = "需 {0}",
            ["CostMore"]    = "{0}（另有 {1} 处可换）",
            ["CollectabilityNote"] = "收藏价值 {0}",
            ["CostHqRequired"]     = "需 HQ",
            ["Copied"]      = "已复制：{0}",
            ["ClickToCopy"] = "点击复制：{0}",
            ["HintRecipe"]        = "左键：打开制作笔记",
            ["HintItemInfoLeft"]  = "左键：查看物品信息",
            ["HintCopyLeft"]      = "左键：复制名称",
            ["HintItemInfoRight"] = "右键：查看物品信息",
            ["HintCopyRight"]     = "右键：复制名称",
            ["HoldingsBreakdown"]   = "持有分布：",
            ["ItemInfoUnavailable"] = "无法打开 Omni 物品信息（{0}），点击材料名改为复制。",

            ["StatusStarting"]           = "已启用，正在检测…",
            ["StatusNotScanned"]         = "尚未检测。",
            ["StatusScanDone"]           = "传承录 已用 {0}/{1} · 秘籍 已用 {2}/{3}",
            ["StatusScanFailed"]         = "检测失败：{0}",
            ["StatusCatalogEmpty"]       = "没有识别到传承录 / 秘籍，可能是游戏数据已更新（详见日志）。",
            ["StatusAutoIdle"]           = "没有可自动使用的书（背包里没有未使用的书，或已全部跳过）。",
            ["StatusUseSkipped"]         = "已跳过 {0}（等待解锁超时），下次检测后重试。",
            ["StatusUsing"]              = "正在使用：{0}",
            ["StatusUsedOne"]            = "已使用：{0}（还剩 {1} 本）",
            ["StatusAllUsed"]            = "全部书籍都已使用。",
        });

    public override bool HasSettings => true;

    // 宿主按成员名 config 绑定已保存的配置；不能是 readonly，宿主需要给它赋值。
    // 属性名 / 类型 / 默认值保持不变，否则已发布用户的 JSON 会丢。
    private Settings config = new();

    #region 数据

    private readonly List<Book> catalog = [];
    private readonly Dictionary<uint, Book> catalogById = [];
    private bool catalogBuilt;
    private string? catalogError;

    private readonly Dictionary<uint, List<Cost>> costMap = [];
    private readonly Dictionary<uint, int> costAlternatives = [];
    private readonly HashSet<(byte CurrencyType, uint Bucket)> unknownBuckets = [];
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
    private long alreadyElsewhere;

    private readonly HashSet<uint> bagItems = [];
    private readonly Dictionary<uint, (InventoryType Bag, uint Slot)> bagIndex = [];

    /// <summary>
    /// Omni 本体库存快照（背包之外还含鞍囊 / 雇员 / 上架 / 收藏柜 / 部队箱 / 住宅缓存）。
    /// 由 IPC 异步取回，只在 framework 线程落地；含缓存过期风险，取不到时回退到背包统计。
    /// </summary>
    private readonly Dictionary<uint, long> omniHoldings = [];

    /// <summary>每个物品在 Omni 快照里的容器分布，用于悬停时显示「背包 12 / 雇员 30」。</summary>
    private readonly Dictionary<uint, List<(string Location, long Count)>> omniLocations = [];

    /// <summary>实时扫描到的容器分布。Omni 快照没有该物品时用它兜底，避免悬停什么都不显示。</summary>
    private readonly Dictionary<uint, List<(string Location, long Count)>> liveLocations = [];

    /// <summary>产物物品 → 配方行。整表只建一次索引，避免每次绘制都对 14909 行做全扫。</summary>
    private readonly Dictionary<uint, uint> recipeByItem = [];
    private bool recipeIndexBuilt;

    private readonly Dictionary<uint, long> omniHoldingsHq = [];
    private Dictionary<uint, long>? pendingOmniHoldings;
    private Dictionary<uint, long>? pendingOmniHq;
    private Dictionary<uint, List<(string Location, long Count)>>? pendingOmniLocations;
    private volatile bool omniDirty;
    private int omniGeneration;
    private bool omniReady;
    private string? omniUnavailableReason;
    private DateTime omniSampledUtc = DateTime.MinValue;

    /// <summary>
    /// 打开 Omni 物品信息窗的三级回退：
    ///   1. 宿主补了 IPC（<see cref="OpenItemInfoIpc"/>）就用 IPC，最稳；
    ///   2. 否则反射宿主内部——MainWindow 有公开静态单例，ItemSearchTab 里有个 Action&lt;uint&gt; 字段；
    ///   3. 都失败就退回复制名称。
    /// 反射只按「形状」找成员（类型名 / 字段类型），不依赖混淆后的名字。
    /// </summary>
    private const string OpenItemInfoIpc = "OmniToolbox.Item.OpenInformation";

    /// <summary>Omni 自动存取：把物品从雇员 / 鞍囊 / 部队箱取回背包。</summary>
    private const string AutoStoreRetrieveIpc = "OmniToolbox.AutoStore.StartRetrieve";

    private Action<uint>? openItemInfoAction;
    private DateTime nextOpenItemInfoAttemptUtc = DateTime.MinValue;
    private int      openItemInfoAttempts;
    private string? openItemInfoReason;

    private bool built;
    // 状态文字：键 + 参数 + 是否异常（SPEC 第 8 节），渲染时再取本地化文案
    private string statusKey    = "StatusNotScanned";
    private object[] statusArgs = [];
    private bool statusBad;
    private string search = string.Empty;
    private bool   onlyMissing;

    private string? copiedText;
    private double copiedAt;

    #endregion

    #region 自动使用

    private uint? pendingItemId;
    private DateTime pendingSinceUtc;
    private int pendingRetries;
    private DateTime nextAutoUseUtc   = DateTime.MinValue;
    private DateTime nextBagRefreshUtc = DateTime.MinValue;
    private DateTime nextBagScanUtc    = DateTime.MinValue;

    /// <summary>取回请求受理后安排一次重检，等物品落到背包再刷新。</summary>
    private DateTime nextRescanUtc = DateTime.MinValue;

    private DateTime nextConditionResolveUtc  = DateTime.MinValue;
    private bool     conditionUnavailableNoted;
    private readonly HashSet<uint> skippedItems = [];
    private bool autoIdleNoted;

    private ICondition condition;

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
        this.nextBagScanUtc    = DateTime.MinValue;
        this.autoIdleNoted     = false;
        this.skippedItems.Clear();

        // 服务可能随插件重载变化，重新解析
        this.condition                 = null;
        this.nextConditionResolveUtc   = DateTime.MinValue;
        this.conditionUnavailableNoted = false;
        this.nextRescanUtc             = DateTime.MinValue;

        // 作废尚未返回的 Omni 库存查询
        this.omniGeneration++;
        this.omniDirty             = false;
        this.pendingOmniHoldings   = null;
        this.omniReady             = false;
        this.omniUnavailableReason = null;
        this.omniHoldings.Clear();
        this.omniHoldingsHq.Clear();
        this.omniLocations.Clear();
        this.liveLocations.Clear();
        this.recipeByItem.Clear();
        this.recipeIndexBuilt             = false;
        this.pendingOmniHq                = null;
        this.pendingOmniLocations         = null;
        this.openItemInfoAction           = null;
        this.openItemInfoReason           = null;
        this.nextOpenItemInfoAttemptUtc   = DateTime.MinValue;
        this.openItemInfoAttempts         = 0;
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
        var before = Snapshot();

        this.config.WindowOpen          = false;
        this.config.ShowUsed            = false;
        this.config.AutoUseFolklore     = false;
        this.config.AutoUseSecretRecipe = false;
        this.config.BookGapMs           = 200;
        this.collapsed                  = false;
        this.resizePending              = true;

        // 宿主契约：没有实际变更时必须返回 false，否则每次点重置都会触发一次配置保存
        return before != Snapshot();

        (bool, bool, bool, bool, int, bool, bool) Snapshot() => (
            this.config.WindowOpen,
            this.config.ShowUsed,
            this.config.AutoUseFolklore,
            this.config.AutoUseSecretRecipe,
            this.config.BookGapMs,
            this.collapsed,
            this.resizePending);
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
            // 目录完全由静态数据决定：命中缓存就跳过四万行扫描
            if (this.TryLoadCatalogCache())
            {
                this.catalogBuilt = true;
                return;
            }

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
            this.SaveCatalogCache();
        }
        catch (Exception ex)
        {
            this.catalog.Clear();
            this.catalogById.Clear();
            this.catalogBuilt = false;
            this.catalogError = ex.Message;
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 建立目录失败");
        }
    }

    private string CatalogCachePath
        => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(),
                        "LKami9FolkloreBooks.catalog.json");

    /// <summary>读目录缓存：版本与道具行数都对得上才用，否则返回 false 走全表扫描。</summary>
    private bool TryLoadCatalogCache()
    {
        try
        {
            var path = this.CatalogCachePath;
            if (!File.Exists(path))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (!root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var schema) || schema != 1)
                return false;

            var items = DalamudServices.DataManager.GetExcelSheet<Item>();

            if (!root.TryGetProperty("items", out var itemCount)
                || !itemCount.TryGetInt32(out var cachedCount)
                || cachedCount != items.Count)
                return false;

            if (!root.TryGetProperty("books", out var books) || books.ValueKind != JsonValueKind.Array)
                return false;

            var parsed = new List<Book>(books.GetArrayLength());

            foreach (var entry in books.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idValue) || !idValue.TryGetUInt32(out var itemId))
                    return false;

                if (!entry.TryGetProperty("f", out var folkloreValue)
                    || folkloreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;

                if (!items.TryGetRow(itemId, out var itemRow))
                    return false;

                parsed.Add(new Book(itemId, itemRow.Name.ToString(), itemRow.Icon, folkloreValue.GetBoolean()));
            }

            if (parsed.Count == 0)
                return false;

            foreach (var book in parsed)
            {
                this.catalog.Add(book);
                this.catalogById[book.ItemId] = book;
            }

            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9秘录助手] 读取目录缓存失败，改为全表扫描");
            this.catalog.Clear();
            this.catalogById.Clear();
            return false;
        }
    }

    private void SaveCatalogCache()
    {
        try
        {
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();
            var payload = new
            {
                v     = 1,
                items = items.Count,
                books = this.catalog.Select(book => new { id = book.ItemId, f = book.IsFolklore }).ToArray(),
            };

            File.WriteAllText(this.CatalogCachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9秘录助手] 写入目录缓存失败");
        }
    }

    private void EnsureCostMap()
    {
        if (this.costMapBuilt) return;

        this.costAlternatives.Clear();

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

                    // 先把这一条 offer 的材料算完整，再分给它的接收物品
                    List<Cost> costs = [];

                    foreach (var cost in offer.ItemCosts)
                    {
                        var currencyId = cost.ItemCost.RowId;
                        if (currencyId == 0)
                            continue;

                        // CostType=3：这一槽是货币桶编号，必须先映射成真正的货币道具
                        if (cost.CostType == 3)
                        {
                            if (!ScripBuckets.TryGetValue((shop.UseCurrencyType, currencyId), out var scripId))
                            {
                                if (this.unknownBuckets.Add((shop.UseCurrencyType, currencyId)))
                                    DalamudServices.PluginLog.Warning(
                                        $"[LKami9秘录助手] 未收录的货币桶：UseCurrencyType={shop.UseCurrencyType} 桶={currencyId}（商店 {shop.RowId}），已跳过该成本");

                                continue;
                            }

                            currencyId = scripId;
                        }

                        // CurrencyCost 是数量；为 0 时这一槽是「收藏品兑换」，CollectabilityCost 是要求的
                        // 收藏价值而不是数量（数量恒为 1）。原来直接把收藏价值当数量，会算出上千的缺口。
                        var quantity       = cost.CurrencyCost;
                        var collectability = (uint)cost.CollectabilityCost;

                        if (quantity == 0 && collectability == 0)
                            continue;

                        if (quantity == 0)
                            quantity = 1;

                        if (costs.Any(c => c.ItemId == currencyId))
                            continue;

                        // SpecialShop 的成本槽没有独立 HQ 字段，HQ 要求记在 CostType 上：
                        // 实测（146 本书 / 253 条成本行）0=普通道具、1=需要 HQ、3=特殊货币桶。
                        var requiresHq = cost.CostType == 1;

                        var name = items.TryGetRow(currencyId, out var row) ? row.Name.ToString() : $"#{currencyId}";
                        costs.Add(new Cost(currencyId, name, quantity, collectability, requiresHq));
                    }

                    if (costs.Count == 0)
                        continue;

                    foreach (var receive in offer.ReceiveItems)
                    {
                        var receiveId = receive.Item.RowId;

                        if (receiveId == 0 || !known.Contains(receiveId))
                            continue;

                        // 同一本书可能有多处可换：只保留第一条，避免不同途径的材料被并集累加、把「需要」算高
                        if (this.costMap.ContainsKey(receiveId))
                        {
                            this.costAlternatives[receiveId] = this.costAlternatives.GetValueOrDefault(receiveId) + 1;
                            continue;
                        }

                        this.costMap[receiveId] = [.. costs];
                    }
                }
            }

            this.costMapBuilt = true;
        }
        catch (Exception ex)
        {
            this.costMap.Clear();
            this.costAlternatives.Clear();
            this.costMapBuilt = false;
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 解析兑换表失败");
        }
    }

    private void Scan()
    {
        try
        {
            this.EnsureCatalog();

            if (this.catalog.Count == 0)
            {
                // 行号失效或数据异常时，原来会静默显示「检测完成 0/0」，看不出问题
                this.built = false;
                this.SetStatus("StatusCatalogEmpty", true);
                DalamudServices.PluginLog.Warning(
                    $"[LKami9秘录助手] 目录为空，未匹配到传承录/秘籍：{this.catalogError ?? "Action 行号未命中"}");
                return;
            }

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

            // 背包之外的容器（鞍囊 / 雇员 / 收藏柜 / 部队箱）走 Omni 的库存缓存，
            // 异步取回后在 framework 线程落地并重算一次，界面先显示背包口径再自动刷新。
            this.RequestOmniHoldings();
            this.EnsureOpenItemInfo();

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
        // 同一材料可能既要 NQ（别的书）又要 HQ，所以键里带上 HQ 标记
        var needed = new Dictionary<(uint ItemId, bool Hq), long>();
        var names  = new Dictionary<(uint ItemId, bool Hq), string>();

        this.alreadyInBag     = 0;
        this.alreadyElsewhere = 0;

        foreach (var row in this.rows)
        {
            if (row.Used)
                continue;

            if (row.InBag)
            {
                this.alreadyInBag++;
                continue;
            }

            // 书在鞍囊 / 雇员 / 部队箱里也算已拥有：不需要再兑换，只是要取到背包才能使用
            if (row.HeldElsewhere > 0)
            {
                this.alreadyElsewhere += row.HeldElsewhere;
                continue;
            }

            if (!this.costMap.TryGetValue(row.Book.ItemId, out var costs))
                continue;

            foreach (var cost in costs)
            {
                var key = (cost.ItemId, cost.RequiresHq);
                needed[key] = needed.GetValueOrDefault(key) + cost.Count;
                names[key]  = cost.Name;
            }
        }

        var needs = new List<Need>();
        foreach (var kv in needed)
        {
            var itemId = kv.Key.ItemId;
            var held   = 0L;
            var heldHq = 0L;

            foreach (var h in this.currencyHoldings)
                if (h.ItemId == itemId)
                {
                    held   += h.Count;
                    heldHq += h.HqCount;
                }

            foreach (var h in this.itemHoldings)
                if (h.ItemId == itemId)
                {
                    held   += h.Count;
                    heldHq += h.HqCount;
                }

            // Omni 快照已经包含背包，直接相加会重复计数，所以取两者较大值：
            // 缓存比实时少（过期）时用实时值，缓存多出来的鞍囊 / 雇员 / 部队箱数量也能算进来。
            if (this.omniHoldings.TryGetValue(itemId, out var omniHeld) && omniHeld > held)
                held = omniHeld;

            if (this.omniHoldingsHq.TryGetValue(itemId, out var omniHq) && omniHq > heldHq)
                heldHq = omniHq;

            // 要求 HQ 的材料只认 HQ 持有量：NQ 再多也交不了
            var effectiveHeld = kv.Key.Hq ? heldHq : held;

            needs.Add(new Need(itemId, names[kv.Key], kv.Value, effectiveHeld, heldHq, kv.Key.Hq));
        }

        // 缺口大的排前面：一眼看到最该补的材料；缺口相同再按需求量
        needs.Sort((a, b) =>
        {
            var byMissing = b.Missing.CompareTo(a.Missing);
            return byMissing != 0 ? byMissing : b.Required.CompareTo(a.Required);
        });
        this.needs = needs;

        this.otherCurrencies = this.currencyHoldings
            .Where(h => !needed.Keys.Any(key => key.ItemId == h.ItemId))
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

    private List<BookRow> View(IEnumerable<BookRow> source, string kw)
    {
        var list = source.Where(row => this.MatchesSearch(row, kw)).ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Book.Name, b.Book.Name));
        return list;
    }

    /// <summary>搜索同时匹配书名与兑换材料名：输入「军票」能筛出所有用它换的书。</summary>
    private bool MatchesSearch(BookRow row, string kw)
    {
        if (kw.Length == 0)
            return true;

        if (row.Book.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            return true;

        return this.costMap.TryGetValue(row.Book.ItemId, out var costs)
               && costs.Any(cost => cost.Name.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private void ReadHoldings()
    {
        var currency  = new Dictionary<uint, Holding>();
        var bagged    = new Dictionary<uint, Holding>();
        var locations = new Dictionary<uint, Dictionary<string, long>>();

        try
        {
            CollectContainer(InventoryType.Currency, currency, locations, "Currency");

            // 碎晶 / 水晶 / 晶簇 存在独立的 Crystals 容器里（任务道具及水晶页），
            // 不在背包也不在货币容器中，漏掉它就会把「持有」算成 0。
            CollectContainer(InventoryType.Crystals, currency, locations, "Crystals");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 读取货币与水晶失败");
        }

        try
        {
            foreach (var bag in PlayerBags)
                CollectContainer(bag, bagged, locations, "Inventory");

            foreach (var container in ExtraItemContainers)
                CollectContainer(container, bagged, locations,
                    container is InventoryType.SaddleBag1 or InventoryType.SaddleBag2
                        or InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2
                        ? "Saddlebag"
                        : "Cosmopouch");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 读取背包与额外容器失败");
        }

        this.currencyHoldings = currency.Values.OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        this.itemHoldings     = bagged.Values.ToList();

        this.liveLocations.Clear();

        foreach (var pair in locations)
            this.liveLocations[pair.Key] = pair.Value
                .OrderByDescending(entry => entry.Value)
                .Select(entry => (entry.Key, entry.Value))
                .ToList();
    }

    /// <summary>把一个容器的非空槽位累加进目标表；容器未加载时跳过。</summary>
    private static unsafe void CollectContainer(
        InventoryType type,
        Dictionary<uint, Holding> target,
        Dictionary<uint, Dictionary<string, long>> locations,
        string location)
    {
        var container = GetContainer(type);
        if (container == null)
            return;

        var items = DalamudServices.DataManager.GetExcelSheet<Item>();

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;

            var count = slot->GetQuantity();
            if (count == 0)
                continue;

            var name = items.TryGetRow(slot->ItemId, out var row) ? row.Name.ToString() : $"#{slot->ItemId}";
            AddHolding(target, slot->ItemId, name, count, slot->IsHighQuality());

            if (!locations.TryGetValue(slot->ItemId, out var byLocation))
                locations[slot->ItemId] = byLocation = [];

            byLocation[location] = byLocation.GetValueOrDefault(location) + count;
        }
    }

    private static void AddHolding(Dictionary<uint, Holding> map, uint itemId, string name, uint count, bool hq)
    {
        if (map.TryGetValue(itemId, out var existing))
        {
            existing.Count += count;
            if (hq)
                existing.HqCount += count;
        }
        else
        {
            map[itemId] = new Holding(itemId, name, count, hq);
        }
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

    #region Omni 库存

    private const string InventoryIpc = "OmniToolbox.Inventory.GetItem";

    /// <summary>
    /// 收集本次检测要查的物品（未使用的书 + 汇总里的材料），向 Omni 本体要跨容器持有量。
    /// scope=currentCharacter + includeCached：当前角色背包、兵装库、鞍囊、雇员（含上架）、
    /// 幻化台、收藏柜、部队箱与住宅缓存。
    /// </summary>
    private void RequestOmniHoldings()
    {
        var ids = new HashSet<uint>();
        foreach (var row in this.rows)
            if (!row.Used)
                ids.Add(row.Book.ItemId);
        foreach (var need in this.needs)
            ids.Add(need.ItemId);

        var generation = ++this.omniGeneration;
        if (ids.Count == 0)
        {
            this.pendingOmniHoldings = new Dictionary<uint, long>();
            this.omniDirty           = true;
            return;
        }

        _ = this.QueryOmniHoldingsAsync(ids.ToList(), generation);
    }

    private async Task QueryOmniHoldingsAsync(List<uint> ids, int generation)
    {
        try
        {
            var subscriber = DalamudServices.PluginInterface
                .GetIpcSubscriber<uint, string, ulong, bool, Task<string>>(InventoryIpc);

            if (!subscriber.HasFunction)
            {
                this.ReportOmniUnavailable("OmniToolbox 未提供库存 IPC");
                return;
            }

            var found     = new Dictionary<uint, long>(ids.Count);
            var foundHq   = new Dictionary<uint, long>(ids.Count);
            var locations = new Dictionary<uint, List<(string Location, long Count)>>(ids.Count);
            foreach (var id in ids)
            {
                // 重新检测 / 停用模块都会推进 generation，旧请求直接作废
                if (generation != this.omniGeneration || !this.IsEnabled)
                    return;

                // 只读缓存快照，不触发采集，因此在后台线程调用是安全的
                var response = await subscriber
                    .InvokeFunc(id, "currentCharacter", 0UL, true)
                    .ConfigureAwait(false);

                var count = ParseOmniItem(response, out var breakdown, out var hqCount);
                if (count > 0)
                {
                    found[id] = count;
                    if (hqCount > 0)
                        foundHq[id] = hqCount;
                    if (breakdown.Count > 0)
                        locations[id] = breakdown;
                }
            }

            if (generation != this.omniGeneration)
                return;

            this.pendingOmniHoldings  = found;
            this.pendingOmniHq        = foundHq;
            this.pendingOmniLocations = locations;
            this.omniDirty            = true;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9秘录助手] 查询 Omni 库存失败，回退到背包统计");
            this.ReportOmniUnavailable(ex.Message);
        }
    }

    private void ReportOmniUnavailable(string reason)
    {
        this.omniUnavailableReason = reason;
        this.pendingOmniHoldings   = null;
        this.omniDirty             = true;
    }

    /// <summary>在 framework 线程应用异步结果：写 omniHoldings、回填每本书的持有量，然后重算汇总。</summary>
    private void ApplyOmniHoldings()
    {
        var pending    = this.pendingOmniHoldings;
        var pendingHq  = this.pendingOmniHq;
        var pendingLoc = this.pendingOmniLocations;
        this.pendingOmniHoldings  = null;
        this.pendingOmniHq        = null;
        this.pendingOmniLocations = null;

        this.omniHoldings.Clear();
        this.omniHoldingsHq.Clear();
        this.omniLocations.Clear();

        if (pendingHq is not null)
            foreach (var pair in pendingHq)
                this.omniHoldingsHq[pair.Key] = pair.Value;

        if (pendingLoc is not null)
            foreach (var pair in pendingLoc)
                this.omniLocations[pair.Key] = pair.Value;
        if (pending is not null)
        {
            foreach (var pair in pending)
                this.omniHoldings[pair.Key] = pair.Value;

            this.omniReady             = true;
            this.omniUnavailableReason = null;
            this.omniSampledUtc        = DateTime.Now;
        }
        else
        {
            this.omniReady = false;
        }

        foreach (var row in this.rows)
            row.HeldElsewhere = this.omniHoldings.GetValueOrDefault(row.Book.ItemId);

        this.Regroup();
    }

    /// <summary>
    /// 解析 GetItem 的返回：优先 observedTotal，其次 NQ+HQ，最后累加 items[].quantity；
    /// 同时按 items[].location 汇总容器分布。
    /// </summary>
    private static long ParseOmniItem(string? json, out List<(string Location, long Count)> locations, out long hqTotal)
    {
        locations = [];
        hqTotal   = 0;

        if (string.IsNullOrWhiteSpace(json))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return 0;

            if (root.TryGetProperty("observedTotal", out var total) && total.ValueKind == JsonValueKind.Number)
                return total.GetInt64();

            var sum = 0L;
            if (root.TryGetProperty("observedNQ", out var nq) && nq.ValueKind == JsonValueKind.Number)
                sum += nq.GetInt64();
            if (root.TryGetProperty("observedHQ", out var hq) && hq.ValueKind == JsonValueKind.Number)
            {
                hqTotal = hq.GetInt64();
                sum    += hqTotal;
            }

            if (sum > 0)
                return sum;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var byLocation = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("quantity", out var quantity) || quantity.ValueKind != JsonValueKind.Number)
                        continue;

                    var qty = quantity.GetInt64();
                    sum += qty;

                    var location = item.TryGetProperty("location", out var locationValue)
                                   && locationValue.ValueKind == JsonValueKind.String
                        ? locationValue.GetString() ?? string.Empty
                        : string.Empty;

                    if (location.Length == 0 || qty <= 0)
                        continue;

                    byLocation[location] = byLocation.GetValueOrDefault(location) + qty;
                }

                if (byLocation.Count > 0)
                    locations = byLocation
                        .OrderByDescending(pair => pair.Value)
                        .Select(pair => (pair.Key, pair.Value))
                        .ToList();
            }

            return sum;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>解析一次「打开物品信息」入口，成功后缓存委托。</summary>
    private void EnsureOpenItemInfo()
    {
        // 成功一次就固定下来；失败则每 5 秒最多重试一次（宿主窗口可能是延迟创建的）。
        // 试满 3 次仍失败就停手：宿主这版没有可用入口，再试也只是反复反射和刷日志。
        if (this.openItemInfoAction is not null)
            return;

        if (this.openItemInfoAttempts >= 3)
            return;

        if (DateTime.UtcNow < this.nextOpenItemInfoAttemptUtc)
            return;

        this.nextOpenItemInfoAttemptUtc = DateTime.UtcNow.AddSeconds(5);

        // 1) 宿主注册了约定 IPC
        try
        {
            var subscriber = DalamudServices.PluginInterface.GetIpcSubscriber<uint, bool>(OpenItemInfoIpc);
            if (subscriber.HasFunction)
            {
                this.openItemInfoAction = itemId => subscriber.InvokeFunc(itemId);
                this.openItemInfoReason = null;
                return;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9秘录助手] 探测物品信息 IPC 失败");
        }

        // 2) 反射宿主内部
        if (this.TryResolveItemInformationAction(out var action, out var reason))
        {
            this.openItemInfoAction = action;
            this.openItemInfoReason = null;
            return;
        }

        this.openItemInfoAction = null;
        this.openItemInfoReason = reason;
        this.openItemInfoAttempts++;

        // 刻意不在这里替玩家打开 Omni 主界面：宿主窗口是懒创建的，强行拉起会打断玩家操作。
        // 想手动试这条路，设置页「诊断」里会给一次机会。
        DalamudServices.PluginLog.Debug($"[LKami9秘录助手] 物品信息入口不可用：{reason}");
    }

    /// <summary>通过 Dalamud 的公开插件接口打开 Omni 主界面，让宿主构造自己的窗口对象。</summary>
    /// <summary>重置自动重试计数，让「诊断」可以再触发一轮解析。</summary>
    private void ResetOpenItemInfoAttempts()
    {
        this.openItemInfoAttempts        = 0;
        this.nextOpenItemInfoAttemptUtc  = DateTime.MinValue;
    }

    private bool TryOpenHostMainUi()
    {
        try
        {
            var plugin = DalamudServices.PluginInterface.InstalledPlugins
                .FirstOrDefault(candidate => candidate.InternalName == "OmniToolbox");

            if (plugin is not { IsLoaded: true })
                return false;

            plugin.OpenMainUi();
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9秘录助手] 请求打开 Omni 主界面失败");
            return false;
        }
    }

    /// <summary>
    /// 反射取 MainWindow 单例 → itemSearchTab → Action&lt;uint&gt; 字段。
    /// 成员按形状定位：静态属性返回 MainWindow 自身、字段类型名 ItemSearchTab、字段类型 Action&lt;uint&gt;。
    /// </summary>
    private bool TryResolveItemInformationAction(out Action<uint>? action, out string reason, List<string>? trace = null)
    {
        action = null;

        try
        {
            // 模块可能加载在独立的 AssemblyLoadContext 里，Type.GetType 未必能解析宿主程序集，
            // 所以先在已加载程序集里按名字找。
            var hostAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "OmniToolbox");

            var mainWindowType = hostAssembly?.GetType("OmniToolbox.UI.MainWindow")
                ?? Type.GetType("OmniToolbox.UI.MainWindow, OmniToolbox");

            trace?.Add($"宿主程序集：{(hostAssembly is null ? "未找到" : hostAssembly.GetName().Version?.ToString() ?? "ok")}");
            trace?.Add($"MainWindow 类型：{(mainWindowType is null ? "未找到" : "ok")}");

            if (mainWindowType is null)
            {
                reason = "未加载 OmniToolbox.UI.MainWindow";
                return false;
            }

            // 单例可能以静态字段、静态属性或静态工厂方法三种形式存在，按稳定性依次尝试：
            // 字段是宿主自己的实例存储，方法有可能是工厂（会新建对象），所以放最后。
            var staticFields = mainWindowType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.FieldType == mainWindowType)
                .ToArray();

            trace?.Add($"静态字段候选 {staticFields.Length}：" + string.Join("，", staticFields.Select(field =>
                $"{field.Name}={(field.GetValue(null) is null ? "null" : "有值")}")));

            var instance = staticFields
                .Select(field => field.GetValue(null))
                .FirstOrDefault(value => value is not null);

            instance ??= mainWindowType
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.PropertyType == mainWindowType)
                .Select(property => property.GetValue(null))
                .FirstOrDefault(value => value is not null);

            instance ??= mainWindowType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.ReturnType == mainWindowType && method.GetParameters().Length == 0)
                .Select(method => method.Invoke(null, null))
                .FirstOrDefault(value => value is not null);

            trace?.Add($"MainWindow 实例：{(instance is null ? "未取到" : instance.GetType().Name)}");

            if (instance is null)
            {
                reason = "未取到 MainWindow 实例（静态字段/属性/方法均为空）";
                return false;
            }

            // 先开窗，否则物品信息窗没有绘制宿主
            mainWindowType.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance)?.Invoke(instance, null);

            var searchTab = mainWindowType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(field => field.FieldType.Name == "ItemSearchTab")
                ?.GetValue(instance);

            trace?.Add($"ItemSearchTab：{(searchTab is null ? "未取到" : "ok")}");

            if (searchTab is null)
            {
                reason = "未取到 ItemSearchTab";
                return false;
            }

            var field = searchTab.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.FieldType == typeof(Action<uint>))
                .OrderByDescending(candidate =>
                    candidate.Name.Contains("ItemInformation", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            trace?.Add($"Action<uint> 字段：{field?.Name ?? "未找到"}");

            if (field?.GetValue(searchTab) is not Action<uint> open)
            {
                reason = "ItemSearchTab 上没有 Action<uint> 字段";
                return false;
            }

            action = open;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>把「打开物品信息」的解析过程逐条打到聊天框，供游戏内排查。</summary>
    /// <summary>
    /// 请求 Omni 把这本书取回背包。source 按 Omni 快照里的容器挑一个可取的；
    /// 拿不到分布时传 None，由 Omni 自己选。
    /// </summary>
    private void RequestRetrieve(uint itemId)
    {
        _ = this.RetrieveAsync(itemId);
    }

    private async Task RetrieveAsync(uint itemId)
    {
        try
        {
            var subscriber = DalamudServices.PluginInterface
                .GetIpcSubscriber<uint, int, bool?, string, ulong, Task<string>>(AutoStoreRetrieveIpc);

            if (!subscriber.HasFunction)
            {
                this.SetStatus("StatusRetrieveFailed", true, OmniLoc.Get(this, "RetrieveNoIpc"));
                return;
            }

            var source   = this.PickRetrieveSource(itemId);
            var response = await subscriber.InvokeFunc(itemId, 1, false, source, 0UL).ConfigureAwait(false);
            var name     = this.rowByItem.TryGetValue(itemId, out var row) ? row.Book.Name : $"#{itemId}";

            if (IsAccepted(response))
            {
                this.SetStatus("StatusRetrieveRequested", false, name);
                this.nextRescanUtc = DateTime.UtcNow.AddSeconds(3);
            }
            else
            {
                this.SetStatus("StatusRetrieveFailed", true, name);
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9秘录助手] 请求取回失败");
            this.SetStatus("StatusRetrieveFailed", true, ex.GetBaseException().Message);
        }
    }

    /// <summary>按可取出优先级挑容器；都没有就返回 None 让 Omni 自己选。</summary>
    private string PickRetrieveSource(uint itemId)
    {
        if (!this.omniLocations.TryGetValue(itemId, out var breakdown))
            return "None";

        string[] priority = ["Retainer", "Saddlebag", "FreeCompanyChest", "Armoire", "GlamourDresser"];

        foreach (var source in priority)
            foreach (var (location, count) in breakdown)
                if (count > 0 && string.Equals(location, source, StringComparison.OrdinalIgnoreCase))
                    return source;

        return "None";
    }

    private static bool IsAccepted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("accepted", out var accepted)
                   && accepted.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void PrintItemInfoDiagnostics()
    {
        this.ResetOpenItemInfoAttempts();

        var trace = new List<string>();
        var ok    = this.TryResolveItemInformationAction(out _, out var reason, trace);

        foreach (var line in trace)
            DalamudServices.ChatGUI.Print($"[秘录助手] {line}");

        if (ok)
        {
            DalamudServices.ChatGUI.Print("[秘录助手] 物品信息入口可用");
            return;
        }

        DalamudServices.ChatGUI.Print($"[秘录助手] 物品信息入口不可用：{reason}");

        // 主窗口是首次打开时才构造的：诊断时手动拉一次，让玩家再点一次
        if (this.TryOpenHostMainUi())
            DalamudServices.ChatGUI.Print("[秘录助手] 已请求打开 Omni 主界面，稍等一秒再点一次「诊断」");
    }

    /// <summary>Omni 返回的容器名 → 词条键；未知容器直接显示原文。</summary>
    private static readonly Dictionary<string, string> LocationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Inventory"]        = "Loc.Inventory",
        ["Currency"]         = "Loc.Currency",
        ["Crystals"]         = "Loc.Crystals",
        ["Cosmopouch"]       = "Loc.Cosmopouch",
        ["Armory"]           = "Loc.Armory",
        ["Equipped"]         = "Loc.Equipped",
        ["Saddlebag"]        = "Loc.Saddlebag",
        ["Retainer"]         = "Loc.Retainer",
        ["RetainerMarket"]   = "Loc.RetainerMarket",
        ["GlamourDresser"]   = "Loc.GlamourDresser",
        ["Armoire"]          = "Loc.Armoire",
        ["FreeCompanyChest"] = "Loc.FreeCompanyChest",
        ["HousingStoreroom"] = "Loc.HousingStoreroom",
    };

    private string LocationName(string location)
        => LocationKeys.TryGetValue(location, out var key) ? OmniLoc.Get(this, key) : location;

    /// <summary>拼出「持有分布」明细；没有容器数据时返回 null。</summary>
    private string? BuildHoldingsBreakdown(uint itemId)
    {
        var breakdown = this.omniLocations.TryGetValue(itemId, out var omni) && omni.Count > 0
            ? omni
            : this.liveLocations.GetValueOrDefault(itemId);

        if (breakdown is not { Count: > 0 })
            return null;

        var lines = new List<string> { OmniLoc.Get(this, "HoldingsBreakdown") };

        foreach (var (location, count) in breakdown)
            lines.Add($"{this.LocationName(location)} ×{count}");

        return string.Join('\n', lines);
    }

    /// <summary>「持有」列：数量（必要时追加 HQ 数量），悬停整个单元格可看储存位置。</summary>
    private void DrawHeldCell(Need need)
    {
        var min = ImGui.GetCursorScreenPos();

        ImGui.TextUnformatted(need.Held.ToString());

        if (need.HeldHq > 0)
        {
            // 与数字同色，避免整列被强调色切碎
            ImGui.SameLine(0f, 4f * OmniTheme.Scale(1f));
            ImGui.TextUnformatted($"HQ {need.HeldHq}");
        }

        // 数字本身很窄，悬停判定用整格矩形，不然很难点到
        var max = new System.Numerics.Vector2(
            min.X + ImGui.GetColumnWidth(),
            min.Y + ImGui.GetTextLineHeight());

        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max)
            && this.BuildHoldingsBreakdown(need.ItemId) is { } tooltip)
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>材料名交互：悬停显示容器分布，点击优先打开 Omni 物品信息，否则复制名称。</summary>
    private void ItemNameInteraction(uint itemId, string name)
    {
        this.EnsureOpenItemInfo();

        var recipeId = this.FindRecipe(itemId);

        if (ImGui.IsItemHovered())
        {
            var lines = new List<string>();

            if (this.BuildHoldingsBreakdown(itemId) is { } breakdown)
            {
                lines.Add(breakdown);
                lines.Add(string.Empty);
            }

            // 有配方时左键走制作笔记，右键留给物品信息（没有入口就复制）
            if (recipeId != 0)
            {
                lines.Add(OmniLoc.Get(this, "HintRecipe"));
                lines.Add(OmniLoc.Get(this,
                    this.openItemInfoAction is null ? "HintCopyRight" : "HintItemInfoRight"));
            }
            else
            {
                lines.Add(OmniLoc.Get(this,
                    this.openItemInfoAction is null ? "HintCopyLeft" : "HintItemInfoLeft"));
            }

            ImGui.SetTooltip(string.Join('\n', lines));
        }

        var shift = ImGui.GetIO().KeyShift;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (!this.TryOpenItemInfo(itemId))
                this.CopyName(name);
            return;
        }

        if (!ImGui.IsItemClicked())
            return;

        // Shift+左键固定复制，方便搜名字
        if (shift)
        {
            this.CopyName(name);
            return;
        }

        if (recipeId != 0 && TryOpenRecipe(recipeId))
            return;

        if (this.TryOpenItemInfo(itemId))
            return;

        this.CopyName(name);
    }

    private void CopyName(string name)
    {
        ImGui.SetClipboardText(name);
        this.copiedText = name;
        this.copiedAt   = ImGui.GetTime();
    }

    /// <summary>查物品对应的配方行；没有配方返回 0。</summary>
    private uint FindRecipe(uint itemId)
    {
        this.EnsureRecipeIndex();
        return this.recipeByItem.GetValueOrDefault(itemId);
    }

    /// <summary>
    /// 建「产物 → 配方」索引。Recipe 表约 1.5 万行，全表扫一次的代价远低于
    /// 每次绘制都对每种材料扫一遍，所以只建一次（同一产物多配方时取第一条）。
    /// </summary>
    private void EnsureRecipeIndex()
    {
        if (this.recipeIndexBuilt)
            return;

        this.recipeIndexBuilt = true;

        try
        {
            foreach (var recipe in DalamudServices.DataManager.GetExcelSheet<Recipe>())
            {
                var product = recipe.ItemResult.RowId;
                if (product == 0 || this.recipeByItem.ContainsKey(product))
                    continue;

                this.recipeByItem[product] = recipe.RowId;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9秘录助手] 建立配方索引失败");
        }
    }

    /// <summary>打开制作笔记并定位到指定配方。</summary>
    private static unsafe bool TryOpenRecipe(uint recipeId)
    {
        try
        {
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
                return false;

            agent->OpenRecipeByRecipeId(recipeId);
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9秘录助手] 打开制作笔记失败");
            return false;
        }
    }

    /// <summary>调用宿主的物品信息入口；不可用或调用失败时返回 false。</summary>
    private bool TryOpenItemInfo(uint itemId)
    {
        if (this.openItemInfoAction is not { } open)
            return false;

        try
        {
            open(itemId);
            return true;
        }
        catch (Exception ex)
        {
            // 宿主内部结构变了：退回复制，避免点了没反应
            DalamudServices.PluginLog.Warning(ex, "[LKami9秘录助手] 打开物品信息失败，改为复制名称");
            this.openItemInfoAction = null;
            this.openItemInfoReason = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>物品信息入口不可用时的原因（用于设置页提示，可空）。</summary>
    private string? OpenItemInfoUnavailableReason => this.openItemInfoAction is null ? this.openItemInfoReason : null;

    #endregion

    #region 自动使用

    private int GapMs => Math.Clamp(this.config.BookGapMs, 50, 1_500);

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 取回请求受理后等物品落到背包再重检一次
        if (this.nextRescanUtc != DateTime.MinValue && DateTime.UtcNow >= this.nextRescanUtc)
        {
            this.nextRescanUtc = DateTime.MinValue;
            this.Scan();
            return;
        }

        // Omni 库存是异步取回的，统一在 framework 线程落地，避免和绘制线程抢集合
        if (this.omniDirty)
        {
            this.omniDirty = false;
            this.ApplyOmniHoldings();
        }

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

        // 服务可能延迟就绪：失败后每 5 秒重试一次
        if (this.condition == null && DateTime.UtcNow >= this.nextConditionResolveUtc)
        {
            this.nextConditionResolveUtc = DateTime.UtcNow.AddSeconds(5);
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
        {
            // 拿不到条件服务时保守等待：否则战斗 / 读条 / 切图时也会发使用请求
            if (!this.conditionUnavailableNoted)
            {
                this.conditionUnavailableNoted = true;
                this.SetStatus("StatusConditionUnavailable", true);
                DalamudServices.PluginLog.Warning("[LKami9秘录助手] ICondition 不可用，自动使用暂停");
            }

            return false;
        }

        try
        {
            return !(this.condition[ConditionFlag.InCombat]
                  || this.condition[ConditionFlag.Casting]
                  || this.condition[ConditionFlag.Occupied]
                  || this.condition[ConditionFlag.BetweenAreas]);
        }
        catch
        {
            return false;
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

        // 物品是否已离开背包：250ms 才真扫一次，不必每帧遍历四个容器
        if (DateTime.UtcNow >= this.nextBagScanUtc)
        {
            this.nextBagScanUtc = DateTime.UtcNow.AddMilliseconds(250);

            if (!FindInBags(pendingId))
            {
                this.pendingItemId  = null;
                this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(this.GapMs);
                return;
            }
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

        // 没有命令入口后，把解析诊断放到界面上
        ImGui.SameLine();
        if (ImGui.Button(OmniLoc.Get(this, "Diagnose")))
            this.PrintItemInfoDiagnostics();

        // 物品信息入口不可用时给出原因，方便宿主更新后定位（不挡功能，点击材料名仍会复制）
        if (this.OpenItemInfoUnavailableReason is { Length: > 0 } infoReason)
            DrawWrappedText(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
                string.Format(OmniLoc.Get(this, "ItemInfoUnavailable"), infoReason));

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

        ImGui.Spacing();
        DrawWrappedText(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], this.StatusText);

        return changed;
    }

    /// <summary>
    /// 超宽时按字符切行。中文没有天然断点，ImGui 自带的 wrap 只在空格处断，
    /// 所以中文长句（状态栏、已用清单）会溢出被裁，这里统一按宽度硬切。
    /// </summary>
    private static string WrapByWidth(string text, float maxWidth)
    {
        if (text.Length == 0 || maxWidth <= 1f || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        // 按全角字宽估算每行字数：中文刚好，英文会稍早换行（不影响阅读）
        var perLine = Math.Max(4, (int)(maxWidth / MathF.Max(ImGui.GetFontSize(), 1f)));
        var lines   = new List<string>();

        foreach (var paragraph in text.Split('\n'))
            for (var i = 0; i < paragraph.Length; i += perLine)
                lines.Add(paragraph.Substring(i, Math.Min(perLine, paragraph.Length - i)));

        return string.Join('\n', lines);
    }

    /// <summary>带颜色绘制一段可自动折行的文字。</summary>
    private static void DrawWrappedText(System.Numerics.Vector4 color, string text)
    {
        if (text.Length == 0)
            return;

        var width = MathF.Max(ImGui.GetContentRegionAvail().X, OmniTheme.Scale(60f));

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(WrapByWidth(text, width));
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// 按可用宽度把若干片段拼成多行文本：首行带 prefix，超过宽度就折行，避免被窗口裁掉。
    /// 单个片段本身比整行还宽时不再硬断（中文名没有断字点），让它独占一行。
    /// </summary>
    private static string WrapFragments(string prefix, IReadOnlyList<string> fragments, string separator, float maxWidth)
    {
        if (fragments.Count == 0)
            return prefix;

        var lines    = new List<string>();
        var current  = new System.Text.StringBuilder(prefix);
        var width    = ImGui.CalcTextSize(prefix).X;
        var sepWidth = ImGui.CalcTextSize(separator).X;
        var needSep  = false;

        foreach (var fragment in fragments)
        {
            var fragmentWidth = ImGui.CalcTextSize(fragment).X;
            var extra         = (needSep ? sepWidth : 0f) + fragmentWidth;

            if (width > 0f && width + extra > maxWidth)
            {
                lines.Add(current.ToString().TrimEnd());
                current.Clear();
                width   = 0f;
                needSep = false;
                extra   = fragmentWidth;
            }

            if (needSep)
                current.Append(separator);

            current.Append(fragment);
            width   += extra;
            needSep  = true;
        }

        if (current.Length > 0)
            lines.Add(current.ToString().TrimEnd());

        return string.Join('\n', lines);
    }

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

        ImGui.SetNextWindowPos(
            OmniTheme.ClampWindowPosition(
                viewport.WorkPos + viewport.WorkSize - targetSize,
                targetSize),
            ImGuiCond.FirstUseEver);

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
                var framePos  = ImGui.GetWindowPos();
                var frameSize = ImGui.GetWindowSize();

                // 窗口是 NoTitleBar，ImGui 不会自己拖动；用宿主提供的标题栏拖动计算
                var dragged = OmniWindowChrome.DragTitleBar(WindowId, framePos, frameSize);
                if (dragged != framePos)
                {
                    ImGui.SetWindowPos(dragged);
                    framePos = dragged;
                }
                var tokens    = OmniTheme.Tokens;
                var radius    = OmniTheme.Scale(tokens.BorderRadius);
                var draw      = ImGui.GetWindowDrawList();

                draw.AddRectFilled(framePos, framePos + frameSize,
                    ImGui.GetColorU32(Opaque(tokens.Background)), radius);

                var chrome = OmniWindowChrome.Draw(
                    framePos, frameSize, this.collapsed,
                    OmniLoc.Get(this, "WindowTitle"), CollapseId, CloseId, true);

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
        DrawWrappedText(this.statusBad ? StatusErrorColor : cText, this.StatusText);

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
            DrawWrappedText(tokens.Success, string.Join("、", used.Select(r => r.Book.Name)));
        }

        ImGui.Unindent();
    }

    /// <summary>
    /// 成本文案在绘制时才格式化：Scan 阶段缓存的是未本地化的明细，
    /// 切换界面语言后不会再显示旧语言的「需 …」。
    /// </summary>
    private string FormatCost(uint bookItemId, List<Cost> costs)
    {
        var text = string.Format(OmniLoc.Get(this, "CostText"),
            string.Join(" + ", costs.Select(this.DescribeCost)));

        return this.costAlternatives.TryGetValue(bookItemId, out var alternatives) && alternatives > 0
            ? string.Format(OmniLoc.Get(this, "CostMore"), text, alternatives)
            : text;
    }

    private string FormatCostCopy(List<Cost> costs) => string.Join("、", costs.Select(this.DescribeCost));

    /// <summary>单个成本的展示文本：HQ 要求与收藏价值都要带上，避免误判能不能交。</summary>
    private string DescribeCost(Cost cost)
    {
        var text = $"{cost.Name} ×{cost.Count}";

        if (cost.RequiresHq)
            text += $"（{OmniLoc.Get(this, "CostHqRequired")}）";

        if (cost.Collectability > 0)
            text += $"（{string.Format(OmniLoc.Get(this, "CollectabilityNote"), cost.Collectability)}）";

        return text;
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

        // 书在鞍囊 / 雇员 / 部队箱里：不用再兑换，但要取到背包才能用
        if (row.HeldElsewhere > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "HeldElsewhereRow"), row.HeldElsewhere));

            ImGui.SameLine();
            if (OmniControls.SmallButton(OmniLoc.Get(this, "Retrieve"), false))
                this.RequestRetrieve(row.Book.ItemId);
        }

        if (this.costMap.TryGetValue(row.Book.ItemId, out var costs) && costs.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.FormatCost(row.Book.ItemId, costs));
            this.ClickToCopy(this.FormatCostCopy(costs));
        }
    }

    private void DrawSummary(ThemeTokens tokens, System.Numerics.Vector4 cText)
    {
        if (!ImGui.CollapsingHeader(
                OmniLoc.Get(this, "SummaryHeader") + "###mlSummary",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var visible = this.onlyMissing
            ? this.needs.Where(need => need.Missing > 0).ToList()
            : this.needs;

        if (this.needs.Count > 0)
        {
            var onlyMissing = this.onlyMissing;
            if (ImGui.Checkbox(OmniLoc.Get(this, "OnlyMissing") + "###mlOnlyMissing", ref onlyMissing))
                this.onlyMissing = onlyMissing;

            ImGui.SameLine();
            if (ImGui.Button(OmniLoc.Get(this, "CopyMissing")))
                this.CopyMissingList();

            ImGui.Spacing();
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled(this.needs.Count == 0 && this.rows.Count > 0 && this.rows.All(r => r.Used)
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
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColHeld"), ImGuiTableColumnFlags.WidthFixed, 118 * OmniTheme.Scale(1f));
                ImGui.TableSetupColumn(OmniLoc.Get(this, "ColMissing"), ImGuiTableColumnFlags.WidthFixed, 78 * OmniTheme.Scale(1f));
                ImGui.TableHeadersRow();

                foreach (var need in visible)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(need.Name);
                    this.ItemNameInteraction(need.ItemId, need.Name);

                    if (need.RequiresHq)
                    {
                        // 与材料名同色（都用默认文字色），不再用强调色，避免整列发花
                        ImGui.SameLine(0f, 4f * OmniTheme.Scale(1f));
                        ImGui.TextUnformatted(OmniLoc.Get(this, "CostHqRequired"));
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(need.Required.ToString());

                    ImGui.TableNextColumn();
                    this.DrawHeldCell(need);

                    ImGui.TableNextColumn();
                    ImGui.TextColored(NeedColor(tokens, need.Missing > 0), need.Missing.ToString());
                }
            }
        }

        if (this.alreadyInBag > 0)
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "OtherInBag"), this.alreadyInBag));

        if (this.alreadyElsewhere > 0)
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "OtherElsewhere"), this.alreadyElsewhere));

        if (this.omniReady)
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "OmniCacheNote"), this.omniSampledUtc.ToString("HH:mm:ss")));
        else if (this.omniUnavailableReason is { Length: > 0 })
            ImGui.TextDisabled(OmniLoc.Get(this, "OmniCacheMissing"));

        if (this.otherCurrencies.Count > 0)
        {
            // TextDisabled 不换行，长清单会被窗口裁掉；按可用宽度手工折行后再画
            var text = WrapFragments(
                OmniLoc.Get(this, "OtherHoldingsPrefix"),
                this.otherCurrencies.Select(h => $"{h.Name} ×{h.Count}").ToList(),
                "   ",
                ImGui.GetContentRegionAvail().X - OmniTheme.Scale(8f));

            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>把缺口清单按「材料 / 还差 / 持有 / 需要」复制成文本。</summary>
    private void CopyMissingList()
    {
        var lines = this.needs
            .Where(need => need.Missing > 0)
            .Select(need => string.Join('\t',
                need.RequiresHq ? $"{need.Name}（{OmniLoc.Get(this, "CostHqRequired")}）" : need.Name,
                $"还差 {need.Missing}",
                $"持有 {need.Held} / 需要 {need.Required}"))
            .ToList();

        if (lines.Count == 0)
        {
            this.SetStatus("StatusCopiedNothing");
            return;
        }

        ImGui.SetClipboardText(string.Join('\n', lines));
        this.SetStatus("StatusCopiedMissing", false, lines.Count);
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

    }

    private sealed class Cost(uint itemId, string name, uint count, uint collectability = 0, bool requiresHq = false)
    {
        public uint ItemId { get; } = itemId;
        public string Name { get; } = name;
        public uint Count { get; } = count;

        /// <summary>该成本必须以 HQ 提交（商店成本行的 CostType == 1）。</summary>
        public bool RequiresHq { get; } = requiresHq;

        /// <summary>收藏品兑换要求的收藏价值；0 表示普通道具成本。</summary>
        public uint Collectability { get; } = collectability;
    }

    private sealed class Holding(uint itemId, string name, long count, bool hq = false)
    {
        public uint ItemId { get; } = itemId;
        public string Name { get; } = name;

        /// <summary>该容器内该物品的总数量（NQ + HQ）。</summary>
        public long Count { get; set; } = count;

        /// <summary>其中 HQ 的数量。商店有的条目只收 HQ，需要单独看。</summary>
        public long HqCount { get; set; } = hq ? count : 0;
    }

    private sealed class BookRow
    {
        public Book Book = null!;
        public bool Used;
        public bool InBag;

        /// <summary>Omni 库存快照里这本书的持有量（含背包之外的容器）；0 表示没查到或确实没有。</summary>
        public long HeldElsewhere;
    }

    private sealed record Need(uint ItemId, string Name, long Required, long Held, long HeldHq, bool RequiresHq)
    {
        public long Missing => Math.Max(0, this.Required - this.Held);
    }

    #endregion

    private sealed class Settings
    {
        /// <summary>配置结构版本，用于一次性迁移（见 MigrateConfig）。</summary>
        public int ConfigVersion { get; set; }
        public bool WindowOpen { get; set; }
        public bool ShowUsed { get; set; }
        public bool AutoUseFolklore { get; set; }
        public bool AutoUseSecretRecipe { get; set; }
        public int BookGapMs { get; set; } = 200;
    }

}
