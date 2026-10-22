using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using Lumina.Excel.Sheets;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace LKami9.FolkloreBooks;

/// <summary>
/// LKami9秘录助手（在线模块版）
/// 检测角色已使用 / 未使用的传承录与秘籍，显示兑换所需材料、持有量与缺口，材料名可点击复制。
/// </summary>
public sealed class LKami9FolkloreBooks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9秘录助手",
        Description = "列出已使用 / 未使用的传承录与秘籍，显示兑换材料、持有量与缺口，材料名可点击复制。",
        Category    = ModuleCategory.Item,
        Author      = "LKami9",
        Commands    = [new("重新检测传承录与秘籍", "/omni LKami9FolkloreBooks scan")],
    };

    public override bool HasSettings => true;

    // 宿主按“以 Config 结尾的成员”绑定已保存的配置；不能是 readonly，宿主需要给它赋值
    private LKami9FolkloreBooksConfig config = new();

    private const uint FolkloreActionRowId     = 4107;
    private const uint SecretRecipeActionRowId = 2136;

    private readonly List<Book> catalog = [];
    private bool catalogBuilt;

    private readonly Dictionary<uint, List<Cost>> costMap = [];
    private bool costMapBuilt;

    private readonly List<Row> rows = [];
    private List<Holding> holdings = [];
    private List<Need>    needs      = [];

    private int alreadyInBag;
    private string status = "尚未检测。";

    private string? copiedText;
    private double  copiedAt;

    // 自动使用（待确认机制）
    private uint?   pendingItemId;
    private DateTime pendingSinceUtc;
    private bool    pendingSawCast;
    private DateTime nextAutoUseUtc = DateTime.MinValue;

    #region 生命周期

    protected override void OnEnable()
    {
        DalamudServices.Framework.Update += this.OnFrameworkUpdate;
        this.status = "已启用，正在检测…";
        this.Scan();
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= this.OnFrameworkUpdate;
        this.pendingItemId = null;
    }

    public override bool ResetSettings()
    {
        this.config.ShowUsed             = true;
        this.config.AutoUseFolklore      = false;
        this.config.AutoUseSecretRecipe  = false;
        this.config.BookGapMs            = 200;
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals(arguments.Trim(), "scan", StringComparison.OrdinalIgnoreCase))
            return false;

        this.Scan();
        return true;
    }

    #endregion

    #region 检测

    private void EnsureCatalog()
    {
        if (this.catalogBuilt)
            return;

        this.catalogBuilt = true;

        try
        {
            foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
            {
                if (item.RowId == 0 || !item.ItemAction.IsValid)
                    continue;

                if (item.ItemAction.ValueNullable is not { } action)
                    continue;

                var kind = action.Action.RowId switch
                {
                    FolkloreActionRowId     => 0,
                    SecretRecipeActionRowId => 1,
                    _                       => -1,
                };

                if (kind < 0)
                    continue;

                this.catalog.Add(new Book(item.RowId, item.Name.ToString(), item.Icon, kind == 0));
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 建立目录失败");
        }
    }

    private void Scan()
    {
        this.EnsureCatalog();
        this.EnsureCostMap();

        var items = DalamudServices.DataManager.GetExcelSheet<Item>();
        this.rows.Clear();

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

            this.rows.Add(new Row(book, used, FindInBags(book.ItemId)));
        }

        this.ReadHoldings();
        this.BuildNeeds();

        var folklore = this.rows.Count(r => r.Book.IsFolklore);
        var usedFolklore = this.rows.Count(r => r.Book.IsFolklore && r.Used);
        var recipe = this.rows.Count - folklore;
        var usedRecipe = this.rows.Count(r => !r.Book.IsFolklore && r.Used);

        this.status = $"传承录 已用 {usedFolklore}/{folklore}　·　秘籍 已用 {usedRecipe}/{recipe}";
    }

    private void EnsureCostMap()
    {
        if (this.costMapBuilt)
            return;

        this.costMapBuilt = true;

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
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 解析兑换表失败");
        }
    }

    private unsafe void ReadHoldings()
    {
        var list = new List<Holding>();

        try
        {
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();
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
                    list.Add(new Holding(slot->ItemId, name, count));
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 读取货币失败");
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        this.holdings = list;
    }

    private void BuildNeeds()
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

        var held = this.holdings.ToDictionary(h => h.ItemId, h => (long)h.Count);

        this.needs = needed
                     .Select(kv => new Need(kv.Key, names[kv.Key], kv.Value, held.GetValueOrDefault(kv.Key)))
                     .OrderByDescending(n => n.Required)
                     .ToList();
    }

    private string? CostTextOf(uint itemId) =>
        this.costMap.TryGetValue(itemId, out var costs) && costs.Count > 0
            ? "需 " + string.Join(" + ", costs.Select(c => $"{c.Name} ×{c.Count}"))
            : null;

    private string? CostCopyOf(uint itemId) =>
        this.costMap.TryGetValue(itemId, out var costs) && costs.Count > 0
            ? string.Join(" ", costs.Select(c => c.Name))
            : null;

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

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    #endregion

    #region 自动使用

    private static readonly TimeSpan NoEffectWindow = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan CastTimeout    = TimeSpan.FromSeconds(5);

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.config.AutoUseFolklore && !this.config.AutoUseSecretRecipe)
            return;

        if (!this.CheckPending())
            return;

        if (DateTime.UtcNow < this.nextAutoUseUtc)
            return;

        if (this.config.AutoUseFolklore && this.TryUseNext(true) == UseResult.Used)
            return;

        if (this.config.AutoUseSecretRecipe)
            this.TryUseNext(false);
    }

    private enum UseResult
    {
        None,
        Used,
    }

    private bool CheckPending()
    {
        if (this.pendingItemId is not { } pendingId)
            return true;

        var elapsed = DateTime.UtcNow - this.pendingSinceUtc;

        var unlocked   = this.IsUnlocked(pendingId);
        var stillInBag = FindInBags(pendingId);

        if (unlocked || !stillInBag)
        {
            this.pendingItemId  = null;
            this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(this.GapMs);
            return true;
        }

        if (!this.pendingSawCast)
        {
            // 读条一直没出现 → 请求被游戏丢掉，立刻重发
            if (elapsed > NoEffectWindow)
            {
                this.pendingItemId  = null;
                this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(150);
            }

            return false;
        }

        if (elapsed > CastTimeout)
        {
            this.pendingItemId  = null;
            this.nextAutoUseUtc = DateTime.UtcNow.AddMilliseconds(500);
        }

        return false;
    }

    private unsafe UseResult TryUseNext(bool folklore)
    {
        if (this.pendingItemId != null)
            return UseResult.None;

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return UseResult.None;

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

                var itemId = slot->ItemId;
                var book   = this.catalog.FirstOrDefault(b => b.ItemId == itemId);

                if (book is null || book.IsFolklore != folklore)
                    continue;

                if (this.IsUnlocked(itemId))
                    continue;

                try
                {
                    agent->UseItem(itemId, bag, (uint)i);
                }
                catch (Exception ex)
                {
                    DalamudServices.PluginLog.Error(ex, "[LKami9秘录助手] 使用道具失败");
                    return UseResult.None;
                }

                this.nextAutoUseUtc  = DateTime.UtcNow.AddMilliseconds(this.GapMs);
                this.pendingItemId   = itemId;
                this.pendingSinceUtc = DateTime.UtcNow;
                this.pendingSawCast  = false;
                this.status          = $"正在使用：{book.Name}";
                return UseResult.Used;
            }
        }

        return UseResult.None;
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

    private int GapMs => Math.Clamp(this.config.BookGapMs, 50, 1_500);

    #endregion

    #region 界面

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextWrapped(this.status);

        if (this.copiedText is { } copied && (ImGui.GetTime() - this.copiedAt) < 2.5)
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.6f, 1f), $"已复制：{copied}");

        ImGui.Spacing();

        if (ImGui.Button("立即检测"))
        {
            this.Scan();
            this.copiedText = null;
        }

        ImGui.SameLine();
        var showUsed = this.config.ShowUsed;
        if (ImGui.Checkbox("显示已使用", ref showUsed))
        {
            this.config.ShowUsed = showUsed;
            changed = true;
        }

        ImGui.SameLine();
        var autoFolklore = this.config.AutoUseFolklore;
        if (ImGui.Checkbox("自动使用传承录", ref autoFolklore))
        {
            this.config.AutoUseFolklore = autoFolklore;
            changed = true;
        }

        ImGui.SameLine();
        var autoRecipe = this.config.AutoUseSecretRecipe;
        if (ImGui.Checkbox("自动使用秘籍", ref autoRecipe))
        {
            this.config.AutoUseSecretRecipe = autoRecipe;
            changed = true;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var gap = this.config.BookGapMs;
        if (ImGui.SliderInt("每本之间的间隔 (毫秒)", ref gap, 50, 1_500))
        {
            this.config.BookGapMs = gap;
            changed = true;
        }

        ImGui.Separator();

        ImGui.Text
        (
            $"传承录：未用 {this.rows.Count(r => r.Book.IsFolklore && !r.Used)}"
            + "　·　秘籍：未用 " + this.rows.Count(r => !r.Book.IsFolklore && !r.Used)
        );

        using (var child = ImRaii.Child("##lkBooks", new System.Numerics.Vector2(0, -160 * ImGuiHelpers.GlobalScale), true))
        {
            if (child.Success)
            {
                changed |= this.DrawGroup("传承录（采集）", true);
                ImGui.Spacing();
                changed |= this.DrawGroup("秘籍（生产）", false);
            }
        }

        ImGui.Separator();
        changed |= this.DrawSummary();
        return changed;
    }

    private bool DrawGroup(string title, bool folklore)
    {
        if (!ImGui.CollapsingHeader($"{title}###lk{folklore}", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        var changed = false;
        ImGui.Indent();

        var list = this.rows.Where(r => r.Book.IsFolklore == folklore).ToList();

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f), "—— 未使用 ——");

        var any = false;
        foreach (var row in list.Where(r => !r.Used))
        {
            any = true;
            this.DrawRow(row);
        }

        if (!any)
            ImGui.TextDisabled("都已使用。");

        if (this.config.ShowUsed)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(0.55f, 1f, 0.55f, 1f), "—— 已使用 ——");

            var anyUsed = false;
            foreach (var row in list.Where(r => r.Used))
            {
                anyUsed = true;
                ImGui.TextUnformatted(row.Book.Name);
            }

            if (!anyUsed)
                ImGui.TextDisabled("（暂无）");
        }

        ImGui.Unindent();
        return changed;
    }

    private void DrawRow(Row row)
    {
        try
        {
            var texture = DalamudServices.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(row.Book.Icon)).GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new System.Numerics.Vector2(22, 22) * ImGuiHelpers.GlobalScale);
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
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f), "◀ 已持有，无需兑换");
            return;
        }

        var text = this.CostTextOf(row.Book.ItemId);
        if (text is null)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(text);
        this.ClickToCopy(this.CostCopyOf(row.Book.ItemId));
    }

    private bool DrawSummary()
    {
        if (!ImGui.CollapsingHeader("汇总：还需要兑换的材料###lkSummary", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        if (this.needs.Count == 0)
        {
            ImGui.TextDisabled("暂时没有需要兑换的书。");
        }
        else
        {
            using var table = ImRaii.Table("##lkNeeds", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);

            if (table.Success)
            {
                ImGui.TableSetupColumn("材料", ImGuiTableColumnFlags.WidthFixed, 170 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("需要", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("持有", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("还差", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
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
                    ImGui.TextColored
                    (
                        need.Missing > 0
                            ? new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f)
                            : new System.Numerics.Vector4(0.55f, 1f, 0.55f, 1f),
                        need.Missing.ToString()
                    );
                }
            }
        }

        if (this.alreadyInBag > 0)
            ImGui.TextDisabled($"（另有 {this.alreadyInBag} 本已在背包里，未计入）");

        var others = this.holdings
                         .Where(h => h.Name.Contains("票") && this.needs.All(n => n.ItemId != h.ItemId))
                         .ToList();

        if (others.Count > 0)
            ImGui.TextDisabled("其他持有：" + string.Join("   ", others.Select(h => $"{h.Name} ×{h.Count}")));

        return false;
    }

    private void ClickToCopy(string? text)
    {
        if (text is null)
            return;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("点击复制：" + text);

        if (!ImGui.IsItemClicked())
            return;

        ImGui.SetClipboardText(text);
        this.copiedText = text;
        this.copiedAt   = ImGui.GetTime();
    }

    #endregion

    #region 数据模型

    private sealed record Book(uint ItemId, string Name, uint Icon, bool IsFolklore);

    private sealed record Cost(uint ItemId, string Name, uint Count);

    private sealed record Holding(uint ItemId, string Name, uint Count);

    private sealed record Row(Book Book, bool Used, bool InBag);

    private sealed record Need(uint ItemId, string Name, long Required, long Held)
    {
        public long Missing => Math.Max(0, this.Required - this.Held);
    }

    #endregion
}

[Serializable]
public sealed class LKami9FolkloreBooksConfig
{
    public bool ShowUsed { get; set; } = true;

    public bool AutoUseFolklore { get; set; }

    public bool AutoUseSecretRecipe { get; set; }

    public int BookGapMs { get; set; } = 200;
}
