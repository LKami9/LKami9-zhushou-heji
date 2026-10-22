using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel;
using Lumina.Excel.Sheets;

using OmenTools;
using OmenTools.Extensions;
using OmenTools.OmenService;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace LKami9.GlamourHelper;

/// <summary>
/// LKami9幻化助手 —— 从截图识别石之家幻化，列出装备 / 染剂 / 持有情况 / 获取方式。
///
/// 输入方式：剪贴板图片 或 图片文件路径。
/// 识别引擎：Windows 自带 OCR（Windows.Media.Ocr），离线、零安装，经内嵌的 PowerShell 脚本调用。
/// 关联算法：用 OCR 的坐标把染剂挂到对应装备上（详见 ParseOcrJson 的注释）。
///
/// 界面：独立窗口两页（识别与装备 / 幻化预设），短命令 /L9HH（重新识别 / open / window / close）。
///   窗口底板随主题明暗自适应：深色主题通深，浅色主题用主题主色做实板；
///   角色查看窗口旁的快捷栏可拖拽解除跟随，并与 Omni「更好的幻化管理」保持同侧。
///
/// 线程模型：后台只跑 PowerShell 和读文件；游戏内存读取（持有量）与
///   results / unknowns 赋值全部在框架线程完成（ApplyPendingOcr）。
/// </summary>
public sealed class LKami9GlamourHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9幻化助手",
        Description = "独立窗口（识别与装备 / 幻化预设）+ /L9HH 短命令：识别石之家截图、显示持有与获取方式、写入/应用幻化模板、本地试穿；整合 Omni「更好的幻化管理」预设编辑、发型/眼镜、套装自动试穿；角色快捷栏可拖拽并与 Omni 窗口同侧，窗口底板随主题明暗自适应。",
        Category    = ModuleCategory.Item,
        Author      = "LKami9",
        Commands    =
        [
            new("重新识别（用上次的图）", "/L9HH"),
            new("打开幻化助手窗口",      "/L9HH open"),
            new("打开 / 关闭窗口",       "/L9HH window"),
            new("关闭窗口",             "/L9HH close"),
            new("打开幻化助手窗口（旧入口）", "/omni LKami9GlamourHelper open"),
            new("重新识别（旧入口）",       "/omni LKami9GlamourHelper scan"),
        ],
    };

    public override bool HasSettings => true;

    private const string ShortCommand = "/L9HH";

    private ICommandManager? shortCommandManager;
    private bool shortCommandRegistered;

    // 宿主按「以 Config 结尾的成员」绑定已保存的配置；不能是 readonly
    private LKami9GlamourHelperConfig config = new();

    // ── 运行时状态 ──
    private readonly List<SlotResult> results = [];
    private string status = "尚未识别。把截图放进剪贴板，或填图片路径后点「识别」。";
    private bool statusIsError;
    private string lastTiming = "";
    private bool busy;
    private int editingIndex = -1;

    // 修改编辑缓冲（ImGui.InputText 需要 ref 变量，属性不能传 ref；反正一次只编辑一行）
    private string editName = "";
    private string editDye1 = "";
    private string editDye2 = "";
    private string editHint = "";
    private readonly List<string> editCandidates = [];
    private string pathBuffer = "";
    private bool pathInit;

    // ── 本地试穿 / 导入幻化管理 ──
    // ── 本地试穿 ──
    private string tryOnStatus = "";

    private OmniToolbox.Items.ItemPreviewService? previewService;   // 懒建：第一次试穿时才 new
    private readonly List<(int Slot, uint ItemId, byte S0, byte S1)> tryOnItems = [];
    private readonly HashSet<int> tryOnSkipped = [];   // 怎么都穿不上的（不是装备等）
    private int tryOnLastIndex = -1;
    private int tryOnRetry;          // 同一件连续失败几帧
    private bool tryOnHeadDone;      // 眼镜/发型是否已处理
    private bool tryOnActive;
    private uint tryOnGlassesId;
    private uint tryOnHairstyleId;
    private bool frameworkHooked;
    private bool standaloneUiHooked;
    private bool mainWindowOpen;
    private bool mainWindowCollapsed;
    private bool mainWindowResizePending;
    private System.Numerics.Vector2 mainWindowExpandedSize;
    private bool quickActionsDragging;   // 角色快捷栏自由拖拽中

    // ── Omni 幻化管理整合 ──
    private int selectedPresetIndex = -1;
    private int presetEditSlot = -1;
    private string presetNameBuffer = "";
    private string presetItemInput = "";
    private string presetDye0Input = "";
    private string presetDye1Input = "";
    private string presetGlassesInput = "";
    private string presetHairstyleInput = "";
    private string presetStatus = "";
    private readonly List<string> presetItemCandidates = [];
    private int activePresetIndex = -1;
    private bool activePresetFromGearset;
    private int lastGearsetIndex = int.MinValue;
    private Dictionary<string, uint>? glassesByName;
    private Dictionary<uint, string>? glassesNameById;
    private Dictionary<string, uint>? hairstyleByName;
    private Dictionary<uint, string>? hairstyleNameById;
    private readonly Dictionary<uint, string> presetItemNameCache = [];
    private readonly Dictionary<uint, uint> presetItemIconCache = [];

    // ── 文字颜色：一律跟随 Omni 主题（不再单独配置）──
    //    取 OmniTheme.Tokens，切换主题/明暗时本模块会跟着变。

    /// <summary>警示色（模糊匹配问号、修改提示）—— Omni 主题 Warning。</summary>
    private System.Numerics.Vector4 WarnVec => OmniTheme.Tokens.Warning;

    /// <summary>错误色（OCR 失败、路径无效等）—— Omni 主题 Error。</summary>
    private System.Numerics.Vector4 ErrorVec => OmniTheme.Tokens.Error;

    /// <summary>持有色（「有 xN」）—— Omni 主题 Success。</summary>
    private System.Numerics.Vector4 OwnedVec => OmniTheme.Tokens.Success;

    /// <summary>未持有色（「无」「缺」）—— Omni 主题 Secondary（次要文字，天然偏暗）。</summary>
    private System.Numerics.Vector4 NotOwnedVec => OmniTheme.Tokens.Secondary;

    // ── 数据索引（惰性构建）──
    private Dictionary<string, (uint Id, uint Icon, string Slot, bool Untradable)>? itemByName;
    private Dictionary<string, uint>? stainByName;
    private Dictionary<uint, string>? stainNameById;
    private List<string>? dyeLex;

    private static readonly string[] SpecialLex = ["不可染色", "无染色"];
    private static readonly string[] SlotOrder =
    [
        "主手", "副手", "头部", "上衣", "手部", "腿部", "脚部", "耳坠", "项链", "手镯", "戒指",
        "面部配饰", "时尚配饰",
    ];

    // ══════════════════════════════════════════════════════════
    //  生命周期
    // ══════════════════════════════════════════════════════════

    protected override void OnEnable()
    {
        this.config.Presets ??= [];
        foreach (var preset in this.config.Presets)
        {
            preset.Name ??= "";
            preset.Items ??= [];
        }
        try { EnsureOcrScript(); }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "[幻化助手] 写出 OCR 脚本失败"); }

        // 试穿要每帧推进（同帧多次调 Agent/模型接口会互相覆盖）
        try
        {
            DalamudServices.Framework.Update += this.OnFrameworkTick;
            this.frameworkHooked = true;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 注册框架更新失败（试穿不可用）"); }

        try
        {
            DalamudServices.PluginInterface.UiBuilder.Draw += this.DrawStandaloneUi;
            this.standaloneUiHooked = true;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 注册独立窗口失败"); }

        this.RegisterShortCommand();
    }

    protected override void OnDisable()
    {
        try
        {
            if (this.frameworkHooked) DalamudServices.Framework.Update -= this.OnFrameworkTick;
            this.frameworkHooked = false;
        }
        catch { }

        try
        {
            if (this.standaloneUiHooked)
                DalamudServices.PluginInterface.UiBuilder.Draw -= this.DrawStandaloneUi;
            this.standaloneUiHooked = false;
        }
        catch { }

        this.StopTryOn(clear: true);
        this.mainWindowOpen = false;
        this.mainWindowCollapsed = false;
        this.mainWindowResizePending = true;
        this.lastGearsetIndex = int.MinValue;
        this.activePresetIndex = -1;
        this.activePresetFromGearset = false;
        this.pendingOcrJson = null;   // 防止重启模块后应用上一轮遗留的识别结果
        this.UnregisterShortCommand();
    }

    private void RegisterShortCommand()
    {
        try
        {
            this.shortCommandManager =
                DalamudServices.PluginInterface.GetService(typeof(ICommandManager)) as ICommandManager;
            if (this.shortCommandManager == null)
            {
                DalamudServices.PluginLog.Information("[幻化助手] 找不到命令服务，/L9HH 未注册");
                return;
            }

            this.shortCommandRegistered = this.shortCommandManager.AddHandler(
                ShortCommand,
                new CommandInfo(this.OnShortCommand)
                {
                    HelpMessage = "LKami9 幻化助手：/L9HH [open|close|window|scan]",
                });
        }
        catch (Exception ex)
        {
            this.shortCommandRegistered = false;
            DalamudServices.PluginLog.Warning(ex, "[幻化助手] 注册 /L9HH 失败");
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

    protected override void OnDispose()
    {
        try { this.previewService?.Dispose(); } catch { }
        this.previewService = null;
    }

    public override bool ResetSettings()
    {
        this.config = new LKami9GlamourHelperConfig();
        this.results.Clear();
        this.selectedPresetIndex = -1;
        this.presetEditSlot = -1;
        this.presetStatus = "";
        this.status = "设置已重置。";
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        var arg = (arguments ?? "").Trim().ToLowerInvariant();
        if (arg is "open" or "window" or "界面" or "打开")
        {
            this.OpenMainWindow();
            return true;
        }

        if (arg is "close" or "关闭")
        {
            this.mainWindowOpen = false;
            this.mainWindowCollapsed = false;
            this.mainWindowResizePending = true;
            return true;
        }

        if (arg is "scan" or "识别" or "")
        {
            if (!string.IsNullOrWhiteSpace(this.config.LastImagePath) && File.Exists(this.config.LastImagePath))
                _ = this.RunRecognize(this.config.LastImagePath);
            else
                _ = this.RunFromClipboard();
            return true;
        }
        return false;
    }

    private void OpenMainWindow()
    {
        if (this.mainWindowCollapsed)
        {
            this.mainWindowCollapsed = false;
            this.mainWindowResizePending = true;
        }

        this.mainWindowOpen = true;
    }

    // ══════════════════════════════════════════════════════════
    //  UI
    // ══════════════════════════════════════════════════════════

    public override bool DrawSettings()
    {
        ImGui.TextUnformatted("LKami9幻化助手");
        ImGui.TextDisabled("主界面已改为独立窗口，可在游戏里连续缩放、移动和查看。");
        if (ImGui.Button("打开幻化助手窗口"))
            this.OpenMainWindow();

        ImGui.SameLine();
        var showInspect = this.config.ShowInspectQuickActions;
        if (ImGui.Checkbox("角色查看窗口旁显示快捷栏###showInspectQuickActions", ref showInspect))
            this.config.ShowInspectQuickActions = showInspect;

        ImGui.Spacing();
        if (this.results.Count > 0)
            ImGui.TextDisabled($"当前识别结果：{this.results.Count} 件；可用主窗口继续编辑或写入模板。");
        else
            ImGui.TextDisabled("还没有识别结果。打开主窗口后可从剪贴板或图片文件识别。");
        return false;
    }

    private void DrawAssistantContent()
    {
        DrawWorkspaceSummary();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("###LKami9GlamourTabs", ImGuiTabBarFlags.FittingPolicyScroll))
            return;

        if (ImGui.BeginTabItem("识别与装备"))
        {
            DrawIdentifyTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("幻化预设"))
        {
            DrawPresetTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawWorkspaceSummary()
    {
        var filled = this.CountFilledResultSlots();
        var stateText = this.busy ? "识别中" : this.tryOnActive ? "本地试穿中" : "就绪";

        ImGui.TextColored(OmniTheme.Tokens.Text, stateText);
        ImGui.SameLine();
        ImGui.TextDisabled($"装备 {filled}/14");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled($"预设 {this.config.Presets.Count} 个");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled(this.config.ShowInspectQuickActions ? "角色快捷栏已开启" : "角色快捷栏已关闭");
    }

    private void DrawIdentifyTab()
    {
        ImGui.BeginChild("###identifyTab", System.Numerics.Vector2.Zero, false, ImGuiWindowFlags.None);
        DrawInputSection();
        ImGui.Separator();
        DrawResultSection();
        DrawResultActionsSection();
        ImGui.EndChild();
    }

    private void DrawPresetTab()
    {
        ImGui.BeginChild("###presetTab", System.Numerics.Vector2.Zero, false, ImGuiWindowFlags.None);
        DrawPresetSection();
        ImGui.EndChild();
    }

    private static void DrawSectionTitle(string title, string subtitle = "")
    {
        ImGui.TextColored(OmniTheme.Tokens.Text, title);
        if (string.IsNullOrEmpty(subtitle))
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(subtitle);
    }

    private void DrawStandaloneUi()
    {
        try
        {
            if (this.mainWindowOpen)
                this.DrawMainWindow();

            if (this.config.ShowInspectQuickActions)
                this.DrawInspectQuickActions();
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[幻化助手] 独立窗口绘制失败");
        }
    }

    private static void PushOmniWindowStyle(OmniStyleScope style)
    {
        var tokens = OmniTheme.Tokens;
        var panel = PanelColor(tokens);
        var board = BoardColor(tokens);
        var light = !OmniTheme.UsesDarkPalette;
        var textDisabled = tokens.Text with { W = tokens.Text.W * 0.55f };
        var titleBackground = System.Numerics.Vector4.Lerp(tokens.Background, tokens.Primary, 0.35f);
        var titleActive = System.Numerics.Vector4.Lerp(tokens.Background, tokens.Accent, 0.65f);

        style.Color(ImGuiCol.WindowBg, board);
        style.Color(ImGuiCol.ChildBg, System.Numerics.Vector4.Zero);
        style.Color(ImGuiCol.PopupBg, panel);
        style.Color(ImGuiCol.Border, tokens.Border);
        style.Color(ImGuiCol.Text, tokens.Text);
        style.Color(ImGuiCol.TextDisabled, textDisabled);
        // 浅色主题的底板是主题主色（橄榄/粉），按钮和输入框用提亮色才跳得出来；
        // 深色主题维持 Omni 默认的 Surface 方案
        style.Color(ImGuiCol.Button, light ? tokens.Accent with { W = 1f } : tokens.Surface);
        style.Color(ImGuiCol.ButtonHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ButtonActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Header, panel);
        style.Color(ImGuiCol.HeaderHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.HeaderActive, tokens.Surface);
        style.Color(ImGuiCol.FrameBg, light ? tokens.Background with { W = 1f } : tokens.Surface);
        style.Color(ImGuiCol.FrameBgHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.FrameBgActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Tab, tokens.Surface);
        style.Color(ImGuiCol.TabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.TabActive, tokens.Accent);
        style.Color(ImGuiCol.TitleBg, titleBackground);
        style.Color(ImGuiCol.TitleBgActive, titleActive);
        style.Color(ImGuiCol.TitleBgCollapsed, titleBackground);
        style.Color(ImGuiCol.ScrollbarBg, board);
        style.Color(ImGuiCol.ScrollbarGrab, tokens.Secondary);
        style.Color(ImGuiCol.ScrollbarGrabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ScrollbarGrabActive, tokens.Accent);
        style.Color(ImGuiCol.CheckMark, tokens.Success);
        style.Color(ImGuiCol.SliderGrab, tokens.Secondary);
        style.Color(ImGuiCol.SliderGrabActive, tokens.Accent);
        style.Color(ImGuiCol.Separator, tokens.Border);
        style.Color(ImGuiCol.ResizeGrip, tokens.Surface);
        style.Color(ImGuiCol.ResizeGripHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ResizeGripActive, tokens.Accent);
        // 表格斑马纹：奇数行透明（露出底板），偶数行用提亮面板
        style.Color(ImGuiCol.TableRowBg, System.Numerics.Vector4.Zero);
        style.Color(ImGuiCol.TableRowBgAlt, panel);
        style.Color(ImGuiCol.TableHeaderBg, panel);

        style.Var(ImGuiStyleVar.WindowPadding, OmniTheme.PopupPadding());
        style.Var(ImGuiStyleVar.WindowRounding, OmniTheme.Scale(tokens.BorderRadius));
        style.Var(ImGuiStyleVar.WindowBorderSize, OmniTheme.BorderThickness());
        style.Var(ImGuiStyleVar.FrameRounding, OmniTheme.Scale(tokens.ButtonRadius));
        style.Var(ImGuiStyleVar.PopupRounding, OmniTheme.Scale(tokens.BorderRadius));
        style.Var(ImGuiStyleVar.ScrollbarRounding, OmniTheme.Scale(tokens.ButtonRadius));
        style.Var(ImGuiStyleVar.GrabRounding, OmniTheme.Scale(tokens.ButtonRadius));
        style.Var(ImGuiStyleVar.FramePadding, OmniTheme.Scale(new System.Numerics.Vector2(6f, 4f)));
    }

    /// <summary>
    /// 窗口内容底板：深色主题用 Background/Surface 混合（通深）；
    /// 浅色主题（LineGreen / LinePeachBloom）用主题主色 Primary 做实板，
    /// 否则整窗口是一片奶油色、没有层次（用户反馈「太亮」「缺深色背景板」）。
    /// </summary>
    private static System.Numerics.Vector4 BoardColor(OmniToolbox.UI.Theme.ThemeTokens tokens) =>
        OmniTheme.UsesDarkPalette
            ? System.Numerics.Vector4.Lerp(tokens.Background with { W = 1f }, tokens.Surface with { W = 1f }, 0.5f)
            : tokens.Primary with { W = 1f };

    /// <summary>内嵌面板（列表框、表头、子卡片）：比底板亮一档，做出层次。</summary>
    private static System.Numerics.Vector4 PanelColor(OmniToolbox.UI.Theme.ThemeTokens tokens) =>
        OmniTheme.UsesDarkPalette
            ? System.Numerics.Vector4.Lerp(tokens.Background with { W = 1f }, tokens.Surface with { W = 1f }, 0.5f)
            : System.Numerics.Vector4.Lerp(tokens.Primary with { W = 1f }, tokens.Background with { W = 1f }, 0.22f);

    /// <summary>
    /// 画窗口底板：软投影 + 实体填充 + 细描边。
    /// 仿 Omni MaterialPainter 的非玻璃路径（该类是 internal，模块用不了，这里用公开接口复刻同等观感）。
    /// </summary>
    private static void FillWindowBoard(
        ImDrawListPtr draw,
        System.Numerics.Vector2 pos,
        System.Numerics.Vector2 size,
        System.Numerics.Vector4 color,
        float radius)
    {
        if (size.X <= 1f || size.Y <= 1f)
            return;

        var tokens = OmniTheme.Tokens;
        var shadowOffset = OmniTheme.Scale(new System.Numerics.Vector2(0f, 2f));
        draw.AddRectFilled(
            pos + shadowOffset,
            pos + size + shadowOffset,
            ImGui.GetColorU32(tokens.Shadow),
            radius);

        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color with { W = 1f }), radius);

        draw.AddRect(
            pos,
            pos + size,
            ImGui.GetColorU32(tokens.Border with { W = MathF.Max(tokens.Border.W, 0.25f) }),
            radius,
            0,
            MathF.Max(OmniTheme.BorderThickness(), 1f));
    }

    private sealed class OmniStyleScope : IDisposable
    {
        private int colors;
        private int vars;

        public void Color(ImGuiCol index, System.Numerics.Vector4 color)
        {
            ImGui.PushStyleColor(index, color);
            this.colors++;
        }

        public void Var(ImGuiStyleVar index, System.Numerics.Vector2 value)
        {
            ImGui.PushStyleVar(index, value);
            this.vars++;
        }

        public void Var(ImGuiStyleVar index, float value)
        {
            ImGui.PushStyleVar(index, value);
            this.vars++;
        }

        public void Dispose()
        {
            if (this.vars > 0) ImGui.PopStyleVar(this.vars);
            if (this.colors > 0) ImGui.PopStyleColor(this.colors);
            this.vars = 0;
            this.colors = 0;
        }
    }

    private void DrawMainWindow()
    {
        var open = this.mainWindowOpen;
        var viewport = ImGui.GetMainViewport();
        var initialSize = OmniTheme.Scale(new System.Numerics.Vector2(1050f, 720f));
        if (this.mainWindowExpandedSize == System.Numerics.Vector2.Zero)
            this.mainWindowExpandedSize = initialSize;

        var collapsedSize = OmniTheme.CollapsedWindowSize(this.mainWindowExpandedSize.X);
        var targetSize = this.mainWindowCollapsed ? collapsedSize : this.mainWindowExpandedSize;
        var minimumSize = this.mainWindowCollapsed
            ? new System.Numerics.Vector2(OmniTheme.Scale(360f), collapsedSize.Y)
            : OmniTheme.Scale(new System.Numerics.Vector2(820f, 540f));

        ImGui.SetNextWindowSizeConstraints(
            minimumSize,
            viewport.WorkSize);

        if (this.mainWindowResizePending)
        {
            ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
            this.mainWindowResizePending = false;
        }
        else if (!this.mainWindowCollapsed)
        {
            var firstUseSize = System.Numerics.Vector2.Min(initialSize, viewport.WorkSize);
            ImGui.SetNextWindowSize(firstUseSize, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(
                viewport.WorkPos + (viewport.WorkSize - firstUseSize) * 0.5f,
                ImGuiCond.FirstUseEver);
        }

        using var style = new OmniStyleScope();
        PushOmniWindowStyle(style);
        style.Var(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        try
        {
            if (ImGui.Begin(
                    "###LKami9GlamourMainWindow",
                    ref open,
                    ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoScrollbar))
            {
                var framePosition = ImGui.GetWindowPos();
                var frameSize = ImGui.GetWindowSize();
                var tokens = OmniTheme.Tokens;
                FillWindowBoard(
                    ImGui.GetWindowDrawList(),
                    framePosition,
                    frameSize,
                    BoardColor(tokens),
                    OmniTheme.Scale(tokens.BorderRadius));
                var chrome = OmniWindowChrome.Draw(
                    framePosition,
                    frameSize,
                    this.mainWindowCollapsed,
                    "LKami9 幻化助手",
                    "###LKami9GlamourMainCollapse",
                    "###LKami9GlamourMainClose");

                if (chrome.CloseClicked)
                {
                    open = false;
                    this.mainWindowCollapsed = false;
                    this.mainWindowResizePending = true;
                }

                if (chrome.ToggleCollapse)
                {
                    if (this.mainWindowCollapsed)
                    {
                        this.mainWindowCollapsed = false;
                        this.mainWindowResizePending = true;
                    }
                    else
                    {
                        this.mainWindowExpandedSize = frameSize;
                        this.mainWindowCollapsed = true;
                        this.mainWindowResizePending = true;
                    }
                }
                else if (!this.mainWindowCollapsed)
                {
                    this.mainWindowExpandedSize = frameSize;
                }

                if (!this.mainWindowCollapsed)
                {
                    var (contentPosition, contentSize) = OmniWindowChrome.GetContentBounds(framePosition, frameSize);
                    ImGui.SetCursorScreenPos(contentPosition);
                    using var contentStyle = new OmniStyleScope();
                    contentStyle.Var(
                        ImGuiStyleVar.WindowPadding,
                        OmniTheme.Scale(new System.Numerics.Vector2(14f, 12f)));
                    ImGui.BeginChild(
                        "###LKami9GlamourContent",
                        contentSize,
                        false,
                        ImGuiWindowFlags.NoScrollbar);
                    ImGui.PushItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X));
                    this.DrawAssistantContent();
                    ImGui.PopItemWidth();
                    ImGui.EndChild();
                }
            }

            this.mainWindowOpen = open;
            ImGui.End();
        }
        finally
        {
            // style 由 OmniStyleScope 自计数回收。
        }
    }

    private unsafe void DrawInspectQuickActions()
    {
        if (!DalamudServices.PlayerState.IsLoaded)
            return;

        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager != null ? manager->GetAddonByName("CharacterInspect") : null;
        if (addon == null ||
            !addon->IsAddonAndNodesReady() ||
            addon->WindowNode == null ||
            !addon->IsVisible)
            return;

        var inspectState = addon->WindowNode->GetNodeState();
        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos + new System.Numerics.Vector2(8f);
        var max = viewport.WorkPos + viewport.WorkSize;
        var overlaySize = OmniTheme.Scale(new System.Numerics.Vector2(272f, 176f));
        var gap = OmniTheme.Scale(8f);
        var inspectRight = inspectState.TopLeft.X + inspectState.Width;
        var inspectCenter = inspectState.TopLeft.X + inspectState.Width * 0.5f;
        var inspectCenterY = inspectState.TopLeft.Y + MathF.Max(0f, (inspectState.Height - overlaySize.Y) * 0.5f);
        var betterAddon = manager->GetAddonByName("OmniBetterGlamourActions");
        var betterVisible = betterAddon != null &&
                            betterAddon->IsVisible &&
                            betterAddon->WindowNode != null;
        var betterPosition = System.Numerics.Vector2.Zero;
        var betterSize = System.Numerics.Vector2.Zero;
        // 与 Omni「更好的幻化管理」保持同一侧：它在左我们也在左，它在右我们也在右
        // （旧逻辑是故意错开，用户要求同侧）；Omni 窗口没开时默认靠左，和它平常的位置一致
        var preferLeft = true;
        if (betterVisible)
        {
            var betterState = betterAddon->WindowNode->GetNodeState();
            betterPosition = betterState.TopLeft;
            betterSize = new System.Numerics.Vector2(betterState.Width, betterState.Height);
            var betterCenterX = betterState.TopLeft.X + betterState.Width * 0.5f;
            preferLeft = betterCenterX < inspectCenter;
        }

        var right = new System.Numerics.Vector2(inspectRight + gap, inspectCenterY);
        var left = new System.Numerics.Vector2(inspectState.TopLeft.X - overlaySize.X - gap, inspectCenterY);
        var above = new System.Numerics.Vector2(
            inspectState.TopLeft.X + (inspectState.Width - overlaySize.X) * 0.5f,
            inspectState.TopLeft.Y - overlaySize.Y - gap);
        var below = new System.Numerics.Vector2(
            inspectState.TopLeft.X + (inspectState.Width - overlaySize.X) * 0.5f,
            inspectState.TopLeft.Y + inspectState.Height + gap);

        // 同侧堆叠位：首选侧被 Omni 窗口占住时，贴到它下面 / 上面，仍保持同一侧
        var stackX = preferLeft ? left.X : right.X;
        var stackBelow = new System.Numerics.Vector2(stackX, betterPosition.Y + betterSize.Y + gap);
        var stackAbove = new System.Numerics.Vector2(stackX, betterPosition.Y - overlaySize.Y - gap);

        Span<System.Numerics.Vector2> candidates = stackalloc System.Numerics.Vector2[6];
        var count = 0;
        candidates[count++] = preferLeft ? left : right;
        if (betterVisible)
        {
            candidates[count++] = stackBelow;
            candidates[count++] = stackAbove;
        }
        candidates[count++] = below;
        candidates[count++] = above;
        candidates[count++] = preferLeft ? right : left;

        System.Numerics.Vector2? chosen = null;
        for (var i = 0; i < count; i++)
        {
            var candidate = candidates[i];
            if (!IsOverlayInside(candidate, overlaySize, min, max))
                continue;
            if (betterVisible &&
                RectanglesOverlap(candidate, overlaySize, betterPosition, betterSize))
                continue;

            chosen = candidate;
            break;
        }

        if (chosen == null)
        {
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates[i];
                if (!IsOverlayInside(candidate, overlaySize, min, max))
                    continue;

                chosen = candidate;
                break;
            }
        }

        var desired = chosen ?? (preferLeft ? left : right);
        var position = System.Numerics.Vector2.Clamp(
            desired,
            min,
            System.Numerics.Vector2.Max(min, max - overlaySize));

        if (this.config.QuickActionsFree)
        {
            // 自由拖拽模式：位置由用户拖动决定（跨重启保留），并夹在视口内
            var freePos = this.config.QuickActionsPos == System.Numerics.Vector2.Zero
                ? position
                : this.config.QuickActionsPos;
            ImGui.SetNextWindowPos(
                System.Numerics.Vector2.Clamp(freePos, min, System.Numerics.Vector2.Max(min, max - overlaySize)),
                ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        }

        ImGui.SetNextWindowSize(overlaySize, ImGuiCond.Always);
        var show = this.config.ShowInspectQuickActions;
        using var style = new OmniStyleScope();
        PushOmniWindowStyle(style);
        try
        {
            if (ImGui.Begin(
                    "LKami9幻化助手###LKami9GlamourInspectActions",
                    ref show,
                    ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar))
            {
                var framePosition = ImGui.GetWindowPos();
                var frameSize = ImGui.GetWindowSize();
                var tokens = OmniTheme.Tokens;
                FillWindowBoard(
                    ImGui.GetWindowDrawList(),
                    framePosition,
                    frameSize,
                    BoardColor(tokens),
                    OmniTheme.Scale(tokens.BorderRadius));

                // 拖拽交互：跟随模式下拖标题栏 → 解除跟随并接着拖动；自由模式下拖标题栏 → 移动窗口
                var titleHeight = OmniTheme.TitleBarHeight();
                var inTitle = ImGui.IsWindowHovered() &&
                              ImGui.GetIO().MousePos.Y < framePosition.Y + titleHeight &&
                              !ImGui.IsAnyItemHovered();

                if (!this.config.QuickActionsFree)
                {
                    // 与主窗口「拖标题栏解除贴靠」同手势：解除跟随，当前拖动继续生效
                    if (inTitle && ImGui.IsMouseDragging(ImGuiMouseButton.Left, OmniTheme.Scale(3f)))
                    {
                        this.config.QuickActionsFree = true;
                        this.config.QuickActionsPos = framePosition;
                        this.quickActionsDragging    = true;
                    }
                }
                else
                {
                    if (this.quickActionsDragging)
                    {
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            var delta = ImGui.GetIO().MouseDelta;
                            if (delta.X != 0f || delta.Y != 0f)
                                this.config.QuickActionsPos = System.Numerics.Vector2.Clamp(
                                    this.config.QuickActionsPos + delta,
                                    min,
                                    System.Numerics.Vector2.Max(min, max - overlaySize));
                        }
                        else
                        {
                            this.quickActionsDragging = false;
                        }
                    }
                    else if (inTitle && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        this.quickActionsDragging = true;
                    }
                }

                ImGui.TextColored(OmniTheme.Tokens.Text, "角色查看");

                // 标题行右侧按钮：自由模式下多一个「跟随」（重新贴住角色查看窗口）
                var hideWidth = ImGui.CalcTextSize("隐藏").X + ImGui.GetStyle().FramePadding.X * 2f;
                var showFollow = this.config.QuickActionsFree;
                var followWidth = showFollow
                    ? ImGui.CalcTextSize("跟随").X + ImGui.GetStyle().FramePadding.X * 2f
                    : 0f;
                var buttonsWidth = followWidth + (showFollow ? ImGui.GetStyle().ItemSpacing.X : 0f) + hideWidth;

                ImGui.SameLine();
                ImGui.SetCursorPosX(
                    MathF.Max(1f, ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - buttonsWidth));
                if (showFollow)
                {
                    if (ImGui.SmallButton("跟随###quickActionsFollow"))
                    {
                        this.config.QuickActionsFree = false;
                        this.quickActionsDragging    = false;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("重新跟随角色查看窗口");
                    ImGui.SameLine();
                }

                if (ImGui.SmallButton("隐藏###hideInspectQuickActions"))
                    show = false;

                ImGui.Separator();

                var spacing = ImGui.GetStyle().ItemSpacing.X;
                var buttonWidth = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
                var buttonSize = new System.Numerics.Vector2(buttonWidth, OmniTheme.Scale(30f));

                if (ImGui.Button("试穿角色###inspectTryOn", buttonSize))
                    this.TryOnInspectedCharacter();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("按角色查看窗口里的幻化本地试穿（只影响自己看到的外观）");
                ImGui.SameLine();
                if (ImGui.Button("保存预设###inspectSave", buttonSize))
                    this.AddPresetFromCharacterInspect();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("把当前角色查看窗口的幻化存成一条预设");

                if (ImGui.Button("复制分享###inspectShare", buttonSize))
                    this.CopyInspectedPresetToClipboard();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("复制 Omni 兼容的分享文本");
                ImGui.SameLine();
                if (ImGui.Button("打开助手###inspectOpen", buttonSize))
                    this.OpenMainWindow();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("打开幻化助手主窗口");

                if (ImGui.Button("清除试穿###inspectClear", new System.Numerics.Vector2(-1f, buttonSize.Y)))
                    this.StopTryOn(clear: true);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("还原本地试穿，恢复角色真实外观");

                if (!string.IsNullOrEmpty(this.presetStatus))
                {
                    var shortStatus = this.presetStatus.Length > 20
                        ? this.presetStatus[..20] + "…"
                        : this.presetStatus;
                    ImGui.TextColored(OmniTheme.Tokens.Text with { W = OmniTheme.Tokens.Text.W * 0.75f }, shortStatus);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(this.presetStatus);
                }
            }

            this.config.ShowInspectQuickActions = show;
            ImGui.End();
        }
        finally
        {
            // style 由 OmniStyleScope 自计数回收。
        }
    }

    private static bool IsOverlayInside(
        System.Numerics.Vector2 position,
        System.Numerics.Vector2 size,
        System.Numerics.Vector2 min,
        System.Numerics.Vector2 max)
    {
        return position.X >= min.X &&
               position.Y >= min.Y &&
               position.X + size.X <= max.X &&
               position.Y + size.Y <= max.Y;
    }

    private static bool RectanglesOverlap(
        System.Numerics.Vector2 aPosition,
        System.Numerics.Vector2 aSize,
        System.Numerics.Vector2 bPosition,
        System.Numerics.Vector2 bSize)
    {
        return aPosition.X < bPosition.X + bSize.X &&
               aPosition.X + aSize.X > bPosition.X &&
               aPosition.Y < bPosition.Y + bSize.Y &&
               aPosition.Y + aSize.Y > bPosition.Y;
    }

    private unsafe void TryOnInspectedCharacter()
    {
        var preset = this.CaptureCharacterInspectPreset();
        if (preset == null)
        {
            this.presetStatus = "没有读到角色查看窗口的数据。";
            return;
        }

        this.activePresetIndex = -1;
        this.activePresetFromGearset = false;
        this.StartTryOnItems(
            this.BuildPresetTryOnItems(preset),
            preset.GlassesID,
            preset.HairstyleID);
        this.presetStatus = "已开始试穿角色查看窗口中的幻化。";
    }

    private void CopyInspectedPresetToClipboard()
    {
        var preset = this.CaptureCharacterInspectPreset();
        if (preset == null)
        {
            this.presetStatus = "没有读到角色查看窗口的数据。";
            return;
        }

        CopyTextToClipboard(this.BuildPresetText(preset));
        this.presetStatus = "已复制角色查看窗口的分享文本。";
    }

    private void DrawInputSection()
    {
        DrawSectionTitle("截图识别", "支持剪贴板或图片文件，OCR 完全离线");

        // 路径输入占满整行（之前把宽度算成"剩余-200"，导致后面的按钮被窗口边缘裁掉）
        if (!this.pathInit)
        {
            this.pathBuffer = this.config.LastImagePath ?? "";
            this.pathInit = true;
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("###imgPath", "图片文件路径（可直接粘路径）", ref this.pathBuffer, 512))
            this.config.LastImagePath = this.pathBuffer;

        // 按钮另起一行，避免被裁切
        using (ImRaii.Disabled(this.busy))
        {
            if (ImGui.Button("从剪贴板识别"))
                _ = this.RunFromClipboard();

            ImGui.SameLine();
            if (ImGui.Button("识别这个文件"))
            {
                if (string.IsNullOrWhiteSpace(this.pathBuffer) || !File.Exists(this.pathBuffer))
                {
                    this.statusIsError = true;
                    this.status = "图片路径无效（检查路径是否正确）。";
                }
                else
                    _ = this.RunRecognize(this.pathBuffer);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("打开截图目录"))
            OpenFolder(WorkDir());

        if (this.busy)
        {
            ImGui.SameLine();
            ImGui.TextColored(OmniTheme.Tokens.Accent, "识别中…");
        }

        if (this.statusIsError)
            ImGui.TextColored(this.ErrorVec, this.status);
        else
            ImGui.TextWrapped(this.status);
        if (!string.IsNullOrEmpty(this.lastTiming))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.lastTiming);
        }

        var showUnknowns = this.config.ShowUnknowns;
        if (ImGui.Checkbox("列出未识别文本（可手动指派成装备）", ref showUnknowns))
            this.config.ShowUnknowns = showUnknowns;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("OCR 认错字导致匹配不上时，把那些片段列出来，\n你可以粘贴正确名称把它补进结果表。");
    }

    /// <summary>石之家幻化的 14 个部位（顺序与石之家一致，叫法与游戏内一致）。戒指占两行。</summary>
    private static readonly string[] ResultSlots =
    [
        "主手", "副手", "头部", "上衣", "手部", "腿部", "脚部", "耳坠", "项链", "手镯",
        "戒指", "戒指", "面部配饰", "时尚配饰",
    ];

    private int CountFilledResultSlots()
    {
        var ringCount = Math.Min(2, this.results.Count(r => r.Slot == "戒指"));
        var nonRingCount = ResultSlots
            .Distinct()
            .Where(slot => slot != "戒指")
            .Count(slot => this.results.Any(r => r.Slot == slot));
        return nonRingCount + ringCount;
    }

    private void DrawResultSection()
    {
        if (this.results.Count == 0)
        {
            DrawSectionTitle("识别结果", "还没有读取到装备");
            ImGui.TextDisabled(
                "在游戏里打开石之家「幻化」界面，按 PrintScreen 截图（或保存成图片文件），"
                + "然后点上面的「从剪贴板识别」，或填路径后点「识别这个文件」。");
            return;
        }

        var warn = this.WarnVec;
        var filled = this.CountFilledResultSlots();
        DrawSectionTitle("识别结果", $"已识别 {this.results.Count} 件，覆盖 {filled}/14 个部位");
        ImGui.SameLine();
        if (ImGui.SmallButton("复制清单"))
            CopyTextToClipboard(BuildClipboardText());
        ImGui.SameLine();
        ImGui.TextDisabled("（14 个部位固定列出，没认到的空着 —— 点行尾「改」可手动填）");

        var needFix = this.results.Count(r => r.NeedsReview) + this.results.Sum(r => r.Dyes.Count(d => d.NeedsReview));
        if (needFix > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(warn, $"? {needFix} 处模糊匹配 —— 点行尾「改」可粘贴正确名称修改");
        }

        if (!ImGui.BeginTable("###glamourTable", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("槽位", ImGuiTableColumnFlags.WidthFixed, 46 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 36 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("装备", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("染剂（颜色）", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("获取方式", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("持有", ImGuiTableColumnFlags.WidthFixed, 56 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        // 固定列出 14 个部位（石之家那 14 个，顺序也一样）：识别到的填进去，没识别到的空着
        //   —— 这样一眼就能看出「缺哪一件」，而不是认不出就整行消失
        var used = new HashSet<int>();
        this.fillSeq = 0;
        var ringRows = this.results.Where(x => x.Slot == "戒指").ToList();
        var ringDrawn = 0;
        foreach (var slot in ResultSlots)
        {
            var idx = -1;
            if (slot == "戒指")
            {
                if (ringDrawn < ringRows.Count) idx = this.results.IndexOf(ringRows[ringDrawn]);
                ringDrawn++;
            }
            else
            {
                idx = this.results.FindIndex(x => x.Slot == slot);
            }

            if (idx >= 0) used.Add(idx);
            this.DrawResultRow(slot, idx, warn);
        }

        // 兜底：槽位不在这 14 个里的（理论上不会出现）也照样画出来
        for (var i = 0; i < this.results.Count; i++)
            if (!used.Contains(i)) this.DrawResultRow(this.results[i].Slot, i, warn);

        ImGui.EndTable();

        DrawEditorPanel(warn);
        DrawFillPanel();
        DrawUnknownSection(warn);
    }

    /// <summary>
    /// 画一行识别结果。<paramref name="idx"/> = -1 表示这个部位没认到 → 整行画成空的（灰的「—」）。
    /// </summary>
    private void DrawResultRow(string slot, int idx, System.Numerics.Vector4 warn)
    {
        var r = idx >= 0 && idx < this.results.Count ? this.results[idx] : null;

        ImGui.TableNextRow();

        // 槽位
        ImGui.TableNextColumn();
        if (r == null) ImGui.TextDisabled(slot);
        else ImGui.TextUnformatted(slot);

        // 图标
        ImGui.TableNextColumn();
        if (r != null) DrawGameIcon(r.IconId, 26f);

        if (r == null)
        {
            // 没认到这个部位：装备/染剂/获取/持有 画「—」，最后一列给个「填」按钮
            for (var c = 0; c < 4; c++)
            {
                ImGui.TableNextColumn();
                ImGui.TextDisabled("—");
            }

            ImGui.TableNextColumn();
            var seq = this.fillSeq++;
            if (ImGui.SmallButton($"填###fill{seq}"))
            {
                this.fillSlot = slot;
                this.fillName = "";
                this.fillHint = "";
                this.fillCandidates.Clear();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"手动给「{slot}」这一格填一件装备\n（查游戏道具表，自动补图标/持有/获取方式）");
            return;
        }

        // 装备名（模糊匹配打问号）
        ImGui.TableNextColumn();
        if (r.NeedsReview) ImGui.TextColored(warn, "?");
        else ImGui.TextDisabled("　");
        ImGui.SameLine();
        ImGui.TextUnformatted(r.Name);
        if (r.NeedsReview && ImGui.IsItemHovered())
            ImGui.SetTooltip($"OCR 识别不确定（匹配分 {r.MatchScore}）\n点行尾「改」粘贴正确名称即可修改");
        if (r.MatchScore is < 100 and > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({r.MatchScore})");
        }

        // 染剂（逐个标问号）
        ImGui.TableNextColumn();
        if (r.Dyes.Count == 0)
        {
            ImGui.TextDisabled("—");
        }
        else
        {
            for (var k = 0; k < r.Dyes.Count; k++)
            {
                if (k > 0) { ImGui.SameLine(); ImGui.TextUnformatted(" /"); ImGui.SameLine(); }
                var d = r.Dyes[k];
                if (d.NeedsReview) { ImGui.TextColored(warn, "?"); ImGui.SameLine(); }

                // 染剂持有：有 xN / 缺
                var own = d.IsEmpty ? (0, "") : DyeOwned(d.Id);
                ImGui.TextUnformatted(d.Name);
                if (!d.IsEmpty)
                {
                    ImGui.SameLine();
                    if (own.Item1 > 0)
                        ImGui.TextDisabled($"有 {own.Item1}");
                    else
                        ImGui.TextColored(this.NotOwnedVec, "缺");

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(this.BuildDyeTooltip(d, own));
                }
                else if (d.NeedsReview && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"染剂识别不确定（匹配分 {d.Score}）\n点行尾「改」可改染剂名");
                }
            }
        }

        // 获取方式
        //   已持有 → 「已拥有」
        //   未持有 → 获取方式 + 是否可交易（不可交易 = 只能自己搞；可交易 = 市场板还能买）
        ImGui.TableNextColumn();
        var srcs = GetSources(r.ItemId);                 // 已按优先级排好，不含版本号
        var isCash = this.cashShopItems.Contains(r.ItemId);
        var trade = r.Untradable ? "不可交易" : "可交易";
        var hasStore = this.storeUrls.TryGetValue(r.ItemId, out var storeUrl);

        if (r.Owned > 0)
        {
            ImGui.TextUnformatted("已拥有");
        }
        else
        {
            var main = isCash ? "商城购买" : (srcs.Count > 0 ? srcs[0] : "来源未知");
            ImGui.TextUnformatted(main);
            ImGui.SameLine();
            ImGui.TextDisabled("· " + trade);
        }

        // 悬停：完整信息
        if (ImGui.IsItemHovered())
        {
            var tip = new System.Text.StringBuilder();
            if (r.Owned > 0)
            {
                tip.AppendLine("已持有：" + (string.IsNullOrEmpty(r.OwnedWhere) ? $"x{r.Owned}" : r.OwnedWhere));
                tip.AppendLine();
            }
            tip.AppendLine(isCash ? "获取方式：商城购买" : "获取方式：");
            if (srcs.Count > 0)
            {
                foreach (var s in srcs.Take(5)) tip.AppendLine("  · " + s);
                if (srcs.Count > 5) tip.AppendLine($"  …还有 {srcs.Count - 5} 条");
                var det = GetSourceDetails(r.ItemId);
                if (det.Count > 0)
                {
                    tip.AppendLine("  详情：");
                    foreach (var d in det.Take(5)) tip.AppendLine("      " + d);
                }
            }
            else if (!isCash)
                tip.AppendLine("  · 来源未知");
            tip.AppendLine();
            tip.AppendLine("获取方式数据：最终幻想XIV中文维基（灰机wiki） CC BY-NC-SA 3.0");

            tip.AppendLine();
            tip.AppendLine("交易：" + trade);
            if (this.patchInfo.TryGetValue(r.ItemId, out var patch)) tip.AppendLine(patch);
            if (hasStore) tip.AppendLine("\n商城商品页（右键此格复制链接）：\n" + storeUrl);
            ImGui.SetTooltip(tip.ToString().TrimEnd());
        }

        // 商城道具：右键复制商品页链接
        if (hasStore && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetClipboardText(storeUrl);
            this.statusIsError = false;
            this.status = "已复制商城商品页链接：" + storeUrl;
        }

        // 持有
        ImGui.TableNextColumn();
        if (r.Owned > 0)
            ImGui.TextColored(this.OwnedVec, $"有 x{r.Owned}");
        else
            ImGui.TextColored(this.NotOwnedVec, "无");
        if (r.Owned > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.IsNullOrEmpty(r.OwnedWhere) ? "已持有" : "持有位置：" + r.OwnedWhere);

        // 修改
        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"改###fix{idx}"))
        {
            this.editingIndex = this.editingIndex == idx ? -1 : idx;
            if (this.editingIndex == idx) PrepareEditor(r);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("修改这一行（粘贴正确的装备/染剂名）");
    }

    // ── 空行「填」面板（给没认到的部位手动配一件）──
    private string? fillSlot;
    private string fillName = "";
    private string fillHint = "";
    private int fillSeq;
    private readonly List<string> fillCandidates = [];

    private void DrawFillPanel()
    {
        if (this.fillSlot == null) return;
        var slot = this.fillSlot;

        ImGui.Separator();
        ImGui.TextColored(this.WarnVec, $"填 「{slot}」");
        ImGui.SameLine();
        if (ImGui.SmallButton("关闭")) { this.fillSlot = null; return; }

        ImGui.TextDisabled("输入/粘贴装备名 → 查游戏道具表 → 填进这个部位（图标/持有/获取方式会自动补上）");

        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("装备名###fillName", ref this.fillName, 128);
        ImGui.SameLine();
        if (ImGui.Button("确认填充")) ApplyFill(slot);

        if (this.fillCandidates.Count > 0)
        {
            ImGui.TextDisabled("候选（点击选用）：");
            for (var i = 0; i < this.fillCandidates.Count; i++)
            {
                if (!ImGui.SmallButton($"{this.fillCandidates[i]}###fillCand{i}")) continue;
                this.fillName = this.fillCandidates[i];
                ApplyFill(slot);
                break;
            }
        }

        if (!string.IsNullOrEmpty(this.fillHint))
            ImGui.TextWrapped(this.fillHint);
    }

    private void ApplyFill(string slot)
    {
        if (string.IsNullOrWhiteSpace(this.fillName)) { this.fillHint = "请输入装备名。"; return; }

        var cands = new List<string>();
        var res = ResolveItem(this.fillName, cands);
        if (res == null)
        {
            this.fillHint = $"没找到「{this.fillName}」对应的装备 —— 检查名称，或换个写法。";
            this.fillCandidates.Clear();
            return;
        }

        var item = res.Value;
        if (this.results.Any(x => x.Slot == slot && slot != "戒指"))
        {
            this.fillHint = $"「{slot}」已经有装备了 —— 点那一行行尾的「改」去修改它。";
            return;
        }

        var r = new SlotResult
        {
            Index = this.results.Count,
            Slot = slot,                       // 用玩家点的那一格（不是数据表派生的槽位）
            ItemId = item.Id,
            IconId = item.Icon,
            Name = item.Name,
            MatchScore = 100,
            NeedsReview = false,
            Owned = CountOwned(item.Id, out var whereNow),
            OwnedWhere = whereNow,
        };
        this.results.Add(r);

        this.fillCandidates.Clear();
        this.fillCandidates.AddRange(cands);
        this.fillName = item.Name;

        var mismatch = !string.Equals(item.Slot, slot, StringComparison.Ordinal);
        this.fillHint = mismatch
            ? $"已填进「{slot}」（注意：游戏数据里它属于「{item.Slot}」）"
            : (cands.Count > 0 ? "已填入最接近的一项（若不是你要的，点候选换一个）" : "已填入");
        SortResults();
    }

    // ── 修改面板（一次编辑一行）──
    private void PrepareEditor(SlotResult r)
    {
        this.editName = r.Name;
        this.editDye1 = r.Dyes.Count > 0 && !r.Dyes[0].IsEmpty ? r.Dyes[0].Name : "";
        this.editDye2 = r.Dyes.Count > 1 && !r.Dyes[1].IsEmpty ? r.Dyes[1].Name : "";
        this.editHint = "";
        this.editCandidates.Clear();
    }

    private void DrawEditorPanel(System.Numerics.Vector4 warn)
    {
        if (this.editingIndex < 0 || this.editingIndex >= this.results.Count) return;
        var r = this.results[this.editingIndex];

        ImGui.Separator();
        ImGui.TextColored(warn, $"改 修改「{r.Slot}」");
        ImGui.SameLine();
        if (ImGui.SmallButton("关闭")) { this.editingIndex = -1; return; }

        ImGui.TextDisabled("粘贴或输入正确的名称后点「确认」（会自动查游戏数据表并更新图标/槽位/持有）");

        // 装备名
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("装备名###fixName", ref this.editName, 128);
        ImGui.SameLine();
        if (ImGui.Button("确认装备")) ApplyItemFix(r, this.editName);

        // 染剂 1 / 2
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("染剂1###fixDye1", ref this.editDye1, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("染剂2###fixDye2", ref this.editDye2, 64);
        ImGui.SameLine();
        if (ImGui.Button("确认染剂")) ApplyDyeFix(r);

        // 候选（点击即用）
        if (this.editCandidates.Count > 0)
        {
            ImGui.TextDisabled("候选（点击选用）：");
            for (var i = 0; i < this.editCandidates.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (ImGui.SmallButton($"{this.editCandidates[i]}###cand{i}"))
                {
                    var pick = this.editCandidates[i];
                    this.editName = pick;
                    ApplyItemFix(r, pick);
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(this.editHint))
            ImGui.TextColored(warn, this.editHint);
    }

    private void ApplyItemFix(SlotResult r, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) { this.editHint = "请输入装备名。"; return; }
        var cands = new List<string>();
        var res = ResolveItem(input, cands);
        if (res == null)
        {
            this.editHint = $"没找到「{input}」对应的装备 —— 检查名称，或换个写法。";
            this.editCandidates.Clear();
            return;
        }
        var item = res.Value;
        r.ItemId = item.Id;
        r.IconId = item.Icon;
        r.Name = item.Name;
        r.Slot = item.Slot;
        r.MatchScore = 100;
        r.NeedsReview = false;
        r.Owned = CountOwned(item.Id, out var whereNow);
        r.OwnedWhere = whereNow;

        this.editCandidates.Clear();
        this.editCandidates.AddRange(cands);
        this.editName = item.Name;
        this.editHint = cands.Count > 0 ? "已填入最接近的一项（若不是你要的，点候选换一个）" : "已更新";
        SortResults();
    }

    private void ApplyDyeFix(SlotResult r)
    {
        var dyes = new List<DyeResult>();
        foreach (var txt in new[] { this.editDye1, this.editDye2 })
        {
            if (string.IsNullOrWhiteSpace(txt))
            {
                dyes.Add(new DyeResult("无染色", 0, true, 100));
                continue;
            }
            var cands = new List<string>();
            var res = ResolveDye(txt, cands);
            if (res == null)
            {
                this.editHint = $"染剂「{txt}」没找到。";
                return;
            }
            dyes.Add(new DyeResult(res.Value.Name, res.Value.Id, false, 100));
        }
        r.Dyes = dyes;
        this.editHint = "染剂已更新";
    }

    // ── 未识别片段（可手动指派成装备）──
    private void DrawUnknownSection(System.Numerics.Vector4 warn)
    {
        if (!this.config.ShowUnknowns || this.unknowns.Count == 0) return;

        ImGui.Separator();
        if (!ImGui.CollapsingHeader($"未识别文本 {this.unknowns.Count} 段（可手动指派成装备）###unknowns"))
            return;

        ImGui.TextDisabled("这些文字没能匹配到游戏道具，可能是 OCR 错字 —— 粘贴正确名称可把它补进结果表。");
        ImGui.TextDisabled("（部位名 主手/副手/头部/上衣/手部/腿部/脚部/耳坠/项链/手镯/戒指/面部配饰/时尚配饰、");
        ImGui.TextDisabled("  以及「无染色」这类染色说明，已经不会再往这里塞了）");

        for (var i = 0; i < this.unknowns.Count; i++)
        {
            var u = this.unknowns[i];
            ImGui.TextDisabled($"[{u.Text}]");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
            ImGui.InputText($"###uf{i}", ref u.EditBuffer, 128);
            ImGui.SameLine();
            if (ImGui.Button($"指派###ub{i}"))
            {
                var cands = new List<string>();
                var res = ResolveItem(u.EditBuffer, cands);
                if (res == null) { u.Hint = "没找到该装备。"; }
                else
                {
                    var item = res.Value;
                    var existing = this.results.FirstOrDefault(x => x.Slot == item.Slot);
                    if (existing != null && existing.ItemId != item.Id)
                    {
                        existing.ItemId = item.Id; existing.IconId = item.Icon; existing.Name = item.Name;
                        existing.MatchScore = 100; existing.NeedsReview = false;
                        existing.Owned = CountOwned(item.Id); existing.Dyes = [];
                    }
                    else
                    {
                        this.results.Add(new SlotResult
                        {
                            Slot = item.Slot, ItemId = item.Id, IconId = item.Icon, Name = item.Name,
                            MatchScore = 100, Owned = CountOwned(item.Id), OwnedWhere = "",
                        });
                    }
                    this.SortResults();
                    u.Hint = "已指派";
                }
            }
            if (!string.IsNullOrEmpty(u.Hint)) { ImGui.SameLine(); ImGui.TextColored(warn, u.Hint); }
        }
    }

    private void SortResults()
    {
        var sorted = this.results.OrderBy(r => Array.IndexOf(SlotOrder, r.Slot)).ToList();
        this.results.Clear();
        this.results.AddRange(sorted);
        for (var i = 0; i < this.results.Count; i++) this.results[i].Index = i;
        this.editingIndex = -1;
    }

    // ── 手动修改的解析（比 OCR 匹配宽松：这是玩家明确意图）──
    private (uint Id, uint Icon, string Name, string Slot)? ResolveItem(string input, List<string> candidates)
    {
        BuildIndexIfNeeded();
        var s = StripNoise(input);
        if (s.Length < 2) return null;

        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
        {
            try
            {
                var row = DalamudServices.DataManager.GetExcelSheet<Item>().GetRow(itemId);
                var slot = row.RowId != 0
                    ? DescribeSlot(row, DalamudServices.DataManager.GetExcelSheet<EquipSlotCategory>())
                    : null;
                if (row.RowId != 0 && slot != null)
                    return (row.RowId, row.Icon, row.Name.ToString(), slot);
            }
            catch { }
        }

        if (this.itemByName!.TryGetValue(s, out var exact))
            return (exact.Id, exact.Icon, s, exact.Slot);

        // 玩家可能粘贴了带前后缀/空格的全文 → 先试"包含"
        var hits = this.itemByName!.Keys
            .Where(k => k.Contains(s) || s.Contains(k))
            .OrderBy(k => Math.Abs(k.Length - s.Length)).Take(8).ToList();

        if (hits.Count == 0)
            hits = this.itemByName!.Keys
                .Where(k => Math.Abs(k.Length - s.Length) <= 2 && Lev(s, k) <= 2)
                .OrderBy(k => Lev(s, k)).Take(8).ToList();

        if (hits.Count == 0) return null;

        candidates.Clear();
        candidates.AddRange(hits);
        var n = hits[0];
        var v = this.itemByName![n];
        return (v.Id, v.Icon, n, v.Slot);
    }

    private (uint Id, string Name)? ResolveDye(string input, List<string> candidates)
    {
        var s = StripNoise(input);
        if (s.Length < 2) return null;
        if (s is "无染色" or "不可染色") return (0u, s);

        if (this.stainByName!.TryGetValue(s, out var exact)) return (exact, s);

        var hits = this.stainByName!.Keys
            .Where(k => k.Contains(s) || s.Contains(k) ||
                        (Math.Abs(k.Length - s.Length) <= 1 && Lev(s, k) <= 1))
            .Take(8).ToList();
        if (hits.Count == 0) return null;

        candidates.Clear();
        candidates.AddRange(hits);
        return (this.stainByName![hits[0]], hits[0]);
    }

    private void DrawGameIcon(uint iconId, float size)
    {
        try
        {
            if (iconId == 0) { ImGui.Dummy(new System.Numerics.Vector2(size, size)); return; }
            var folder = iconId - (iconId % 1000);
            var tex = DalamudServices.TextureProvider
                .GetFromGame($"ui/icon/{folder:D6}/{iconId:D6}_hr1.tex").GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new System.Numerics.Vector2(size, size) * ImGuiHelpers.GlobalScale);
        }
        catch
        {
            ImGui.Dummy(new System.Numerics.Vector2(size, size));
        }
    }

    // ══════════════════════════════════════════════════════════
    //  识别流程
    // ══════════════════════════════════════════════════════════

    private async Task RunFromClipboard()
    {
        if (this.busy) return;
        this.busy = true;
        try
        {
            var dir = WorkDir();
            Directory.CreateDirectory(dir);
            var img = Path.Combine(dir, "clipboard.png");
            this.status = "正在从剪贴板取图…";

            var (ok, outp) = await RunPowerShell(ScriptPath("clip.ps1"), img);
            if (!ok || !File.Exists(img))
            {
                this.statusIsError = true;
                this.status = "剪贴板里没有图片（先截图，再点这个按钮）。" + (outp.Length > 0 ? " " + outp : "");
                return;
            }
            await RecognizeInternal(img);
        }
        finally { this.busy = false; }
    }

    private async Task RunRecognize(string imagePath)
    {
        if (this.busy) return;
        this.busy = true;
        try { await RecognizeInternal(imagePath); }
        finally { this.busy = false; }
    }

    /// <summary>后台线程只做重活：PowerShell OCR + 读文件。结果文本入队，由框架线程应用。</summary>
    private async Task RecognizeInternal(string imagePath)
    {
        var sw = Stopwatch.StartNew();
        this.status = "正在 OCR 识别…";
        this.config.LastImagePath = imagePath;   // 宿主自动持久化

        var json = Path.Combine(WorkDir(), "ocr.json");
        try { File.Delete(json); } catch { }
        // ocr.ps1 需要两个参数：图片路径 + 输出 json 路径
        var (ok, outp) = await RunPowerShell(ScriptPath("ocr.ps1"), imagePath, json);
        if (!ok || !File.Exists(json))
        {
            this.statusIsError = true;
            this.status = "OCR 失败：" + DescribeScriptFailure(outp);
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            this.statusIsError = true;
            this.status = "读取 OCR 结果失败：" + ex.Message;
            return;
        }

        // 解析要读游戏内存（持有量）并改 results / unknowns，只能放在框架线程：
        // 后台线程读 InventoryManager 有 native 竞态风险，且边画边改 List 会炸绘制线程。
        this.pendingOcrJson = text;
        this.pendingOcrSw   = sw;
        this.status         = "正在解析识别结果…";
    }

    private volatile string? pendingOcrJson;
    private Stopwatch? pendingOcrSw;

    /// <summary>框架线程应用 OCR 结果：游戏状态读取与 UI 状态更新都在这里完成。</summary>
    private void ApplyPendingOcr()
    {
        var text = this.pendingOcrJson;
        var sw   = this.pendingOcrSw;
        if (text == null) return;
        this.pendingOcrJson = null;
        this.pendingOcrSw   = null;

        var ocrMs = sw?.ElapsedMilliseconds ?? 0;
        try
        {
            // 纯文本匹配（索引构建 + OCR 关联）已在后台线程完成，这里只补游戏状态
            var parsed = this.MatchOcrText(text, out var unknownList);

            this.dyeOwnedCache.Clear();   // 每次识别重新统计染剂持有量
            foreach (var r in parsed)
            {
                r.Owned      = this.CountOwned(r.ItemId, out var whereOwned);
                r.OwnedWhere = whereOwned;
                r.Source     = string.Join(" / ", this.GetSources(r.ItemId));
            }

            this.results.Clear();
            this.results.AddRange(parsed);
            this.unknowns.Clear();
            this.unknowns.AddRange(unknownList);

            var needFix  = parsed.Count(r => r.NeedsReview);
            var unknownN = this.unknowns.Count;
            this.statusIsError = false;
            this.status = parsed.Count > 0
                ? $"识别完成：{parsed.Count} 件装备" +
                  (needFix > 0 ? $"，其中 {needFix} 件是模糊匹配（打 ?，可点「改」修改）" : "，全部精确匹配") +
                  (unknownN > 0 && this.config.ShowUnknowns ? $"，另有 {unknownN} 段未识别文本可手动指派" : "")
                : "识别完成，但没找到任何装备名 —— 请确认截图里包含装备清单（带装备名的那块）。";
            this.lastTiming = $"(OCR {ocrMs} ms / 匹配 {(sw?.ElapsedMilliseconds ?? 0) - ocrMs} ms)";
        }
        catch (Exception ex)
        {
            this.statusIsError = true;
            this.status = "解析失败：" + ex.Message;
            DalamudServices.PluginLog.Error(ex, "[幻化助手] 解析 OCR JSON 失败");
        }
    }

    private static string WorkDir() => Path.Combine(Path.GetTempPath(), "LKami9GlamourHelper");

    private static string ScriptPath(string name) => Path.Combine(WorkDir(), name);

    private static async Task<(bool Ok, string Output)> RunPowerShell(string script, params string[] scriptArgs)
    {
        try
        {
            var quoted = string.Join(" ", scriptArgs.Select(a => $"\"{a}\""));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {quoted}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 不要强制 UTF-8：PS 5.1 的控制台输出用的是系统代码页（中文系统是 GBK），
                //   强行按 UTF-8 解码会把错误信息变成乱码
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动 PowerShell");
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000))
            {
                // 超时不杀会漏孤儿 powershell 进程（每次超时漏一个）
                try { p.Kill(true); } catch { }
                return (false, "OCR 超时（60 秒）");
            }

            // 脚本自己写的错误日志（UTF-8，不受控制台代码页影响）
            var log = Path.Combine(WorkDir(), "last_error.txt");
            var fileErr = File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8).Trim() : "";
            try { File.Delete(log); } catch { }

            var msg = string.IsNullOrWhiteSpace(fileErr)
                ? (so + " " + se).Trim()
                : fileErr;
            return (p.ExitCode == 0, msg.Length > 500 ? msg[..500] : msg);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  引擎：OCR JSON → 槽位 / 装备 / 染剂
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 后台线程安全：只做纯文本匹配（构建索引 + OCR 关联）。
    /// 不碰游戏内存、不碰 results / unknowns / dyeOwnedCache —— 那些在框架线程的 ApplyPendingOcr 里做。
    /// </summary>
    private List<SlotResult> MatchOcrText(string json, out List<UnknownFragment> unknowns)
    {
        LoadOmniCache();   // 先载入 OmniToolbox 的角色数据缓存（投影台/收藏柜/雇员/部队箱/房屋仓库）
        // 索引预载：这两个只读 Lumina / 内嵌资源、最后整体赋值，在后台线程构建是安全的
        // （避免把整表遍历前移到框架线程造成首次识别卡顿）
        LoadWikiSources();
        LoadItemSources();
        var rows = new List<(double X, double Y, uint Id, uint Icon, string Name, string Slot, int Score)>();
        unknowns = new List<UnknownFragment>();

        using var doc = JsonDocument.Parse(json);
        foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
        {
            // 把这一行展开成「字符 + 坐标」序列（OCR 对中文是一个汉字一个"词"）
            var chars = new List<(char C, double X, double Y)>();
            foreach (var w in line.GetProperty("words").EnumerateArray())
            {
                var t = w.GetProperty("text").GetString() ?? "";
                double wx = w.GetProperty("x").GetDouble(), wy = w.GetProperty("y").GetDouble(), ww = w.GetProperty("w").GetDouble();
                if (t.Length == 0 || ww <= 0) continue;
                var step = ww / t.Length;
                for (int i = 0; i < t.Length; i++) chars.Add((t[i], wx + i * step, wy));
            }
            if (chars.Count == 0) continue;

            var compact = StripNoise(new string(chars.Select(c => c.C).ToArray()));
            if (compact.Length < 2) continue;

            // ① 先判是否装备名（把行首/行尾黏上的部位名剥掉再匹配）
            var score = 0;
            var body = StripSlotLabel(compact);
            var cand = TryItem(body, out score);
            if (cand == null && body != compact)
            {
                cand = TryItem(compact, out score);           // 剥了反而更差 → 用原串再试一次
                if (cand != null) body = compact;
            }
            if (cand != null && score >= 70)
            {
                var v = this.itemByName![cand];
                rows.Add((chars[0].X, chars[0].Y, v.Id, v.Icon, cand, v.Slot, score));
                continue;
            }

            // ② 否则抽染剂词（同一行可能有多颗）
            var before = this.pendingDyes.Count;
            CollectDyes(compact, chars);
            if (this.pendingDyes.Count == before && LooksLikeName(compact) && !IsNoiseFragment(compact)
                && !unknowns.Any(u => u.Text == compact))
                unknowns.Add(new UnknownFragment { Text = compact });
        }

        // 结构约束：幻化每个槽位只能一件（戒指可两件）→ 消除误匹配
        var chosen = new List<(double X, double Y, uint Id, uint Icon, string Name, string Slot, int Score)>();
        foreach (var g in rows.GroupBy(r => r.Slot))
        {
            var ordered = g.OrderByDescending(r => r.Score).ThenBy(r => r.Y);
            chosen.AddRange(g.Key == "戒指" ? ordered.Take(2) : ordered.Take(1));
        }

        // 关联：每个染剂词找加权距离最近的装备（垂直为主，只允许在装备名下方或同行）
        var byRow = new Dictionary<int, List<DyeResult>>();
        for (int i = 0; i < chosen.Count; i++) byRow[i] = [];
        foreach (var d in this.pendingDyes)
        {
            double best = double.MaxValue; int bi = -1;
            for (int i = 0; i < chosen.Count; i++)
            {
                var r = chosen[i];
                var dy = d.Y - r.Y;
                if (dy < -14) continue;
                var dist = dy * 2.0 + Math.Abs(d.X - r.X) * 0.6;
                if (dist < best) { best = dist; bi = i; }
            }
            if (bi >= 0 && best < 300)
                byRow[bi].Add(new DyeResult(d.Name, d.Id, d.IsEmpty, d.Score, d.X));
        }
        this.pendingDyes.Clear();

        // 同一装备的染剂按 **X 坐标升序** = 图上的左右顺序（槽1 / 槽2）
        // 持有量 / 获取方式要读游戏内存和重量级索引，留给框架线程的 ApplyPendingOcr 补
        var list = new List<SlotResult>();
        for (int i = 0; i < chosen.Count; i++)
        {
            var r = chosen[i];
            var dyes = byRow[i].OrderBy(x => x.X).ToList();
            list.Add(new SlotResult
            {
                Index = i,
                Slot = r.Slot,
                ItemId = r.Id,
                IconId = r.Icon,
                Name = r.Name,
                MatchScore = r.Score,
                NeedsReview = r.Score < 100,
                Dyes = dyes,
                Untradable = this.itemByName!.TryGetValue(r.Name, out var metaRow) && metaRow.Untradable,
            });
        }
        return list.OrderBy(r => Array.IndexOf(SlotOrder, r.Slot)).Select((r, k) => { r.Index = k; return r; }).ToList();
    }

    /// <summary>看起来像名字的片段（用于「未识别」栏 —— 过滤掉纯符号/数字/太长的页脚）</summary>
    private static bool LooksLikeName(string s)
    {
        if (s.Length is < 2 or > 14) return false;
        var cjk = s.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return cjk >= 2 && cjk * 2 >= s.Length;      // 至少一半是汉字
    }

    private readonly List<(double X, double Y, uint Id, string Name, bool IsEmpty, int Score)> pendingDyes = [];
    private readonly List<UnknownFragment> unknowns = [];

    // 抽染剂：精确最长优先 → 模糊（等长编辑距离 ≤2）
    private void CollectDyes(string s, List<(char C, double X, double Y)> chars)
    {
        int i = 0;
        while (i < s.Length)
        {
            var hit = false;
            foreach (var w in SpecialLex)
            {
                if (i + w.Length <= s.Length && s.AsSpan(i, w.Length).SequenceEqual(w))
                {
                    this.pendingDyes.Add((chars[i].X, chars[i].Y, 0u, w, true, 100));
                    i += w.Length; hit = true; break;
                }
            }
            if (hit) continue;

            foreach (var d in this.dyeLex!)
            {
                if (i + d.Length <= s.Length && s.AsSpan(i, d.Length).SequenceEqual(d))
                {
                    this.pendingDyes.Add((chars[i].X, chars[i].Y, this.stainByName![d], d, false, 100));
                    i += d.Length; hit = true; break;
                }
            }
            if (hit) continue;

            string? bestDye = null; var bestLen = 0; var bestDist = 9;
            foreach (var len in new[] { 2, 3, 4 })
            {
                if (i + len > s.Length) continue;
                var seg = s.Substring(i, len);
                foreach (var d in this.dyeLex!)
                {
                    if (d.Length != len) continue;
                    var dist = Lev(seg, d);
                    if (dist < bestDist) { bestDist = dist; bestDye = d; bestLen = len; }
                }
            }
            if (bestDye != null && bestDist <= 2)
            {
                // 距离 1 → 92 分，距离 2 → 80 分（都 < 100，UI 会打问号）
                this.pendingDyes.Add((chars[i].X, chars[i].Y, this.stainByName![bestDye], bestDye, false, bestDist == 1 ? 92 : 80));
                i += bestLen; continue;
            }
            i++;
        }
    }

    // 装备名匹配：剥已知后缀 → 精确 → 模糊（短名只允许精确，避免噪音误命中）
    private string? TryItem(string s, out int score)
    {
        score = 0;
        if (s.Length < 2) return null;

        if (this.itemByName!.TryGetValue(s, out var direct))
        {
            score = 100;
            return s;
        }
        var trimmed = s.Replace("（复制品）", "").Replace("(复制品)", "").Trim('（', '）', '(', ')', ' ');
        if (trimmed != s && this.itemByName!.TryGetValue(trimmed, out var t2))
        {
            score = 100;
            return trimmed;
        }

        if (s.Length <= 3) return null;   // 短名只接受精确

        string? best = null; var bestScore = 0;
        foreach (var k in this.itemByName!.Keys)
        {
            if (Math.Abs(k.Length - s.Length) > 1) continue;
            var d = Lev(s, k);
            var sc = d == 1 && k.Length == s.Length ? 92
                   : d == 1 ? 88
                   : d == 2 && k.Length == s.Length ? 78
                   : d == 2 && Math.Abs(k.Length - s.Length) == 1 ? 72
                   : 0;
            if (sc > bestScore) { bestScore = sc; best = k; }
        }
        score = bestScore;
        return best;
    }

    // ══════════════════════════════════════════════════════════
    //  持有检测
    //   ① 实时读：装备栏 + 背包 + 兵装库（自己扫，永远最新）
    //   ② Omni 缓存：投影台(幻化柜) / 收藏柜 / 陆行鸟鞍囊 / 雇员 / 部队箱 / 房屋仓库
    //      —— 这些容器自己扫很麻烦（要走 UI/Agent），直接复用 OmniToolbox 已经抓好的缓存
    // ══════════════════════════════════════════════════════════

    private HashSet<uint> omniCachedOwned = [];
    private Dictionary<uint, string> omniCachedWhere = [];
    private DateTime omniCacheStamp = DateTime.MinValue;

    private static readonly (string Field, bool IdArray)[] CacheFields =
    [
        ("GlamourDresserItems",  false),   // 投影台（幻化柜）
        ("ArmoireItems",         true),    // 收藏柜（纯 ItemID 数组）
        ("SaddlebagItems",       false),   // 陆行鸟鞍囊
        ("RetainerItems",        false),   // 雇员仓库
        ("RetainerMarketItems",  false),   // 雇员上架
    ];

    private static readonly (string Field, string Name)[] RootCacheFields =
    [
        ("FreeCompanyChestItems",           "部队箱"),
        ("HousingInteriorStoreroomItems",   "房屋仓库"),
        ("HousingExteriorStoreroomItems",   "庭院仓库"),
        ("ApartmentStoreroomItems",         "公寓仓库"),
    ];

    /// <summary>读 OmniToolbox 的 InventoryCache.json（文件变了才重新解析）。</summary>
    private void LoadOmniCache()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "OmniToolbox", "InventoryCache.json");
            if (!File.Exists(path)) return;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == this.omniCacheStamp) return;
            this.omniCacheStamp = stamp;

            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var ids = new HashSet<uint>();
            var where = new Dictionary<uint, string>();

            void Add(uint id, string label)
            {
                if (id == 0) return;
                ids.Add(id);
                if (where.TryGetValue(id, out var prev))
                {
                    if (!prev.Contains(label)) where[id] = prev + " / " + label;
                }
                else
                {
                    where[id] = label;
                }
            }

            void Scan(JsonElement node, string field, string label, bool idArray)
            {
                if (!node.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                foreach (var e in arr.EnumerateArray())
                {
                    if (idArray)
                    {
                        if (e.ValueKind == JsonValueKind.Number)
                        {
                            var id = e.GetUInt32();
                            Add(id, label);
                            if (field == "ArmoireItems") this.armoireItemIds.Add(id);   // 收藏柜集合
                        }
                    }
                    else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("ItemID", out var iid))
                    {
                        Add(iid.GetUInt32(), label);
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("Characters", out var chars) && chars.ValueKind == JsonValueKind.Object)
            {
                foreach (var c in chars.EnumerateObject())
                {
                    foreach (var (field, idArray) in CacheFields)
                        Scan(c.Value, field, field switch
                        {
                            "GlamourDresserItems" => "投影台",
                            "ArmoireItems" => "收藏柜",
                            "SaddlebagItems" => "鞍囊",
                            "RetainerItems" => "雇员",
                            _ => "雇员上架",
                        }, idArray);
                }
            }

            foreach (var (field, label) in RootCacheFields)
                Scan(doc.RootElement, field, label, false);

            this.omniCachedOwned = ids;
            this.omniCachedWhere = where;
            DalamudServices.PluginLog.Information($"[幻化助手] Omni 缓存已载入：{ids.Count} 个道具（投影台/收藏柜/雇员/部队箱/房屋仓库）");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 读取 Omni 缓存失败（只用实时扫描）");
        }
    }

    /// <summary>持有检测要扫的容器（含装备栏：身上正穿的装备不在背包也不在兵装库）。</summary>
    private static readonly InventoryType[] OwnedContainerTypes =
    [
        InventoryType.EquippedItems,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    /// <summary>返回持有数量（实时扫描 + Omni 缓存），并输出持有位置。</summary>
    private unsafe int CountOwned(uint itemId, out string where)
    {
        where = "";
        if (itemId == 0) return 0;

        var total = 0;
        var places = new List<string>();

        try
        {
            var mgr = InventoryManager.Instance();
            if (mgr != null)
            {
                var bagCount = 0;
                var equippedCount = 0;
                foreach (var type in OwnedContainerTypes)
                {
                    var cont = mgr->GetInventoryContainer(type);
                    if (cont == null || !cont->IsLoaded) continue;
                    for (var i = 0; i < cont->Size; i++)
                    {
                        var slot = cont->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0) continue;
                        if (slot->ItemId != itemId) continue;
                        var q = (int)slot->Quantity;
                        total += q;
                        if (type == InventoryType.EquippedItems) equippedCount += q; else bagCount += q;
                    }
                }
                if (equippedCount > 0) places.Add($"已装备x{equippedCount}");
                if (bagCount > 0) places.Add($"背包/兵装库x{bagCount}");
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 读取实时背包失败");
        }

        if (this.omniCachedOwned.Contains(itemId))
        {
            total += 1;   // 缓存里只有"有没有"，数量记 1
            if (this.omniCachedWhere.TryGetValue(itemId, out var w)) places.Add(w);
        }

        where = string.Join(" + ", places);
        return total;
    }

    private int CountOwned(uint itemId)
    {
        var n = CountOwned(itemId, out var _);
        return n;
    }

    // ══════════════════════════════════════════════════════════
    //  获取方式（数据来自 OmniToolbox 自带的 LuminaSupplemental.Excel）
    //    副本掉落 / 怪物掉落 / NPC 商店 / 商城 / 特殊来源（分解、宝箱、幻卡包、杂交…）
    //    全部数据都在本地（内嵌 CSV），不发任何网络请求
    // ══════════════════════════════════════════════════════════

    private Dictionary<uint, List<string>> itemSources = [];
    private readonly Dictionary<uint, string> storeUrls = [];   // 商城道具 → 商品页链接
    private readonly Dictionary<uint, string> patchInfo = [];   // 道具 → "X.X 版本加入"（只进悬停）
    private readonly HashSet<uint> cashShopItems = [];          // 商城/特典道具
    private bool sourcesLoaded;

    // 来源优先级：小的排前面（显示时先显示最有用的；分解/无法获得垫底）
    private const int PrioPriority  = 10;   // 商城 / 特典
    private const int PrioDungeon   = 20;   // 副本掉落
    private const int PrioVendor    = 25;   // 金币商店购买
    private const int PrioCraft     = 30;   // 制作
    private const int PrioGather    = 40;   // 采集
    private const int PrioFate      = 50;   // FATE / 雇员 / 杂交
    private const int PrioDeep      = 55;   // 深层迷宫 / 优雷卡 / 博兹雅
    private const int PrioMob       = 60;   // 怪物掉落
    private const int PrioExchange  = 70;   // 用道具换取
    private const int PrioDesynth   = 90;   // 分解 / 精炼
    private const int PrioNone      = 99;   // 已无法获得

    private DateTime sourcesFailedAt = DateTime.MinValue;

    /// <summary>带失败保护地构建获取方式索引：失败不缓存半成品，60 秒后退避重试。</summary>
    private unsafe void LoadItemSources()
    {
        if (this.sourcesLoaded) return;
        if (DateTime.UtcNow - this.sourcesFailedAt < TimeSpan.FromSeconds(60)) return;

        try
        {
            this.BuildItemSourcesCore();
        }
        catch (Exception ex)
        {
            // 回滚可能已写入的附属表，避免半成品状态被后续使用
            this.cashShopItems.Clear();
            this.storeUrls.Clear();
            this.patchInfo.Clear();
            this.sourcesLoaded = false;
            this.sourcesFailedAt = DateTime.UtcNow;
            DalamudServices.PluginLog.Warning(ex, "[幻化助手] 获取方式索引构建失败（60 秒后重试）");
            return;
        }

        this.sourcesLoaded = true;
    }

    private unsafe void BuildItemSourcesCore()
    {
        var sw = Stopwatch.StartNew();
        var raw = new Dictionary<uint, List<(int Prio, string Text)>>();

        void Add(uint id, string text, int prio)
        {
            if (id == 0 || string.IsNullOrEmpty(text)) return;
            if (!raw.TryGetValue(id, out var list)) { list = []; raw[id] = list; }
            if (list.Any(x => x.Text == text)) return;
            list.Add((prio, text));
        }

        var gd = DalamudServices.DataManager.GameData;
        List<T> Load<T>(string res) where T : LuminaSupplemental.Excel.Model.ICsv, new()
        {
            try
            {
                return LuminaSupplemental.Excel.Services.CsvLoader.LoadResource<T>(res, true, out _, out _, gd, null) ?? [];
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Information(ex, $"[幻化助手] 载入 {typeof(T).Name} 失败（跳过）");
                return [];
            }
        }

        string SheetName<T>(uint rowId) where T : struct, Lumina.Excel.IExcelRow<T>
        {
            try
            {
                var row = DalamudServices.DataManager.GetExcelSheet<T>().GetRow(rowId);
                return row switch
                {
                    Lumina.Excel.Sheets.Item i => i.Name.ToString(),
                    Lumina.Excel.Sheets.BNpcName b => b.Singular.ToString(),
                    Lumina.Excel.Sheets.ENpcResident e => e.Singular.ToString(),
                    Lumina.Excel.Sheets.ContentFinderCondition c => c.Name.ToString(),
                    Lumina.Excel.Sheets.Fate f => f.Name.ToString(),
                    _ => rowId.ToString(),
                };
            }
            catch { return rowId.ToString(); }
        }

        // ── ① 游戏本体：制作 / 采集（幻化装备最常见的来源，LuminaSupplemental 没覆盖）──
        try
        {
            foreach (var r in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>())
            {
                var resultId = r.ItemResult.RowId;
                if (resultId != 0) Add(resultId, "制作（生产职业）", PrioCraft);
            }
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 读 Recipe 失败"); }

        try
        {
            foreach (var r in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GatheringItem>())
            {
                try { if (r.Item.RowId != 0) Add(r.Item.RowId, "采集（园艺/采矿/钓鱼）", PrioGather); } catch { }
            }
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 读 GatheringItem 失败"); }

        // ── ② 无法获得 ──
        foreach (var r in Load<LuminaSupplemental.Excel.Model.UnobtainableItem>(
                     LuminaSupplemental.Excel.Services.CsvLoader.UnobtainableItemResourceName))
            Add(r.ItemId, "已无法获得", PrioNone);

        // ── ③ 副本掉落 ──
        foreach (var r in Load<LuminaSupplemental.Excel.Model.DungeonDrop>(
                     LuminaSupplemental.Excel.Services.CsvLoader.DungeonDropItemResourceName))
            Add(r.ItemId, $"副本：{SheetName<Lumina.Excel.Sheets.ContentFinderCondition>(r.ContentFinderConditionId)}", PrioDungeon);

        // ── ④ 怪物掉落（同一件可能几十只怪 → 只列前 3 个 + 计数）──
        var mobByItem = new Dictionary<uint, List<string>>();
        foreach (var r in Load<LuminaSupplemental.Excel.Model.MobDrop>(
                     LuminaSupplemental.Excel.Services.CsvLoader.MobDropResourceName))
        {
            var name = SheetName<Lumina.Excel.Sheets.BNpcName>(r.BNpcNameId);
            if (!mobByItem.TryGetValue(r.ItemId, out var l)) { l = []; mobByItem[r.ItemId] = l; }
            if (!l.Contains(name)) l.Add(name);
        }
        foreach (var (itemId, names) in mobByItem)
        {
            var head = string.Join("、", names.Take(3));
            Add(itemId, names.Count > 3 ? $"怪物掉落：{head} 等 {names.Count} 种" : $"怪物掉落：{head}", PrioMob);
        }

        // ── ⑤ 商城 / 特典（付费道具）—— 优先级最高，因为这是最"确定"的来源 ──
        foreach (var r in Load<LuminaSupplemental.Excel.Model.StoreItem>(
                     LuminaSupplemental.Excel.Services.CsvLoader.StoreItemResourceName))
        {
            var price = r.PriceCentsUSD > 0 ? $"${r.PriceCentsUSD / 100.0:0.00}" : "";
            var set = r.FittingShopItemSetId != 0 ? " · 试穿套装" : "";
            Add(r.ItemId, string.IsNullOrEmpty(price) ? $"商城 / 特典{set}" : $"商城 / 特典（{price}）{set}", PrioPriority);
            this.cashShopItems.Add(r.ItemId);
            if (!string.IsNullOrWhiteSpace(r.StoreUrl)) this.storeUrls[r.ItemId] = r.StoreUrl;
        }

        // ── ⑥ FATE / 雇员探险 / 杂交 ──
        foreach (var r in Load<LuminaSupplemental.Excel.Model.FateItem>(
                     LuminaSupplemental.Excel.Services.CsvLoader.FateItemResourceName))
            Add(r.ItemId, $"FATE：{SheetName<Lumina.Excel.Sheets.Fate>(r.FateId)}", PrioFate);

        foreach (var r in Load<LuminaSupplemental.Excel.Model.RetainerVentureItem>(
                     LuminaSupplemental.Excel.Services.CsvLoader.RetainerVentureItemResourceName))
            Add(r.ItemId, "雇员探险", PrioFate);

        foreach (var r in Load<LuminaSupplemental.Excel.Model.GardeningCrossbreed>(
                     LuminaSupplemental.Excel.Services.CsvLoader.GardeningCrossbreedResourceName))
            Add(r.ItemResultId, "杂交种植", PrioFate);

        // ── ⑦ 特殊来源（深层迷宫 / 优雷卡 / 博兹雅 / 幻卡包 / 宝箱 …）──
        foreach (var r in Load<LuminaSupplemental.Excel.Model.ItemSupplement>(
                     LuminaSupplemental.Excel.Services.CsvLoader.ItemSupplementResourceName))
        {
            var (label, prio) = r.ItemSupplementSource switch
            {
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Desynth           => ("分解获得", PrioDesynth),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Reduction         => ("精炼获得", PrioDesynth),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Loot              => ("掉落", PrioExchange),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Gardening         => ("杂交种植", PrioFate),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.SkybuilderHandIn  => ("重建上交", PrioExchange),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.CardPacks         => ("幻卡包", PrioExchange),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Coffer            => ("宝箱", PrioExchange),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.PalaceOfTheDead   => ("死者宫殿", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.HeavenOnHigh      => ("天之御柱", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.EurekaOrthos      => ("优雷卡·奥尔托斯", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Anemos            => ("优雷卡·常风", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Pagos             => ("优雷卡·恒冰", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Pyros             => ("优雷卡·涌火", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Hydatos           => ("优雷卡·丰水", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Bozja             => ("南方博兹雅战线", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Logogram          => ("记忆晶石", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.PilgrimsTraverse  => ("天佑女王·朝圣者之径", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Oizys             => ("天佑女王·奥伊西斯", PrioDeep),
                LuminaSupplemental.Excel.Model.ItemSupplementSource.Auxesia           => ("天佑女王·奥克塞西亚", PrioDeep),
                _ => (null, 0),
            };
            if (label == null) continue;

            var extra = r.SourceItemId != 0
                ? "（用「" + SheetName<Lumina.Excel.Sheets.Item>(r.SourceItemId) + "」换取）"
                : "";
            Add(r.ItemId, label + extra, prio);
        }

        // ── ⑧ 金币商店（子行表：每行 = 一个商店的 SubrowCollection）──
        try
        {
            var gsi = DalamudServices.DataManager.GameData.Excel
                .GetSubrowSheet<Lumina.Excel.Sheets.GilShopItem>();
            var n = 0;
            foreach (var row in gsi)
            {
                foreach (var sub in row)
                {
                    uint id = 0;
                    try { id = sub.Item.RowId; } catch { }
                    if (id == 0) continue;
                    n++;
                    Add(id, "金币商店可购买", PrioVendor);
                }
            }
            DalamudServices.PluginLog.Information($"[幻化助手] 金币商店：{n} 条");
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 读 GilShopItem 失败"); }

        // ── ⑨ 加入版本 ──
        // 版本号单独存 —— 玩家反馈「哪版加入」不实用，不该占主显示位，只在悬停里作参考
        foreach (var r in Load<LuminaSupplemental.Excel.Model.ItemPatch>(
                     LuminaSupplemental.Excel.Services.CsvLoader.ItemPatchResourceName))
        {
            if (r.StartItemId == 0 || r.PatchNo <= 0) continue;
            for (var id = r.StartItemId; id <= r.EndItemId; id++)
                this.patchInfo[id] = $"{r.PatchNo:0.0} 版本加入";
        }

        // 按优先级排序后定型
        this.itemSources = raw.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(x => x.Prio).Select(x => x.Text).ToList());
        DalamudServices.PluginLog.Information(
            $"[幻化助手] 获取方式索引就绪：{this.itemSources.Count} 个道具，用时 {sw.ElapsedMilliseconds} ms");
    }

    // ══════════════════════════════════════════════════════════
    //  灰机wiki 获取方式（首选数据源）
    //    数据来自 OmniToolbox.Common.dll 的内嵌资源 WikiItemSources.json
    //    原始数据：「最终幻想XIV中文维基」https://ff14.huijiwiki.com/  (CC BY-NC-SA 3.0)
    //    结构: items[{i:物品ID, s:[sources 数组下标]}] + sources[{c:类型, n:标题, d:详情}]
    //    覆盖 51,227 件物品中的 44,350 件，带 NPC 名、坐标、所需材料、价格
    // ══════════════════════════════════════════════════════════

    private Dictionary<uint, List<string>> wikiTitles = [];     // 物品 → 来源标题（主显示）
    private Dictionary<uint, List<string>> wikiDetails = [];    // 物品 → 来源详情（悬停）
    private bool wikiLoaded;

    private DateTime wikiFailedAt = DateTime.MinValue;

    /// <summary>带失败保护地加载 wiki 获取方式：解析失败不缓存半成品，60 秒后退避重试。</summary>
    private void LoadWikiSources()
    {
        if (this.wikiLoaded) return;
        if (DateTime.UtcNow - this.wikiFailedAt < TimeSpan.FromSeconds(60)) return;

        try
        {
            this.BuildWikiSourcesCore();
        }
        catch (Exception ex)
        {
            // 丢弃可能已部分写入的表，下次（60 秒后）重试；期间用本地聚合兜底
            this.wikiTitles = [];
            this.wikiDetails = [];
            this.wikiLoaded = false;
            this.wikiFailedAt = DateTime.UtcNow;
            DalamudServices.PluginLog.Warning(ex, "[幻化助手] 载入 wiki 获取方式失败（60 秒后重试，暂用本地聚合）");
            return;
        }

        this.wikiLoaded = true;
    }

    private void BuildWikiSourcesCore()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "OmniToolbox.Common");
            using var st = asm?.GetManifestResourceStream("OmniToolbox.Data.WikiItemSources.json");
            if (st == null)
            {
                DalamudServices.PluginLog.Information("[幻化助手] 未找到 wiki 资源（OmniToolbox.Common 版本可能不同）");
                return;
            }

            using var reader = new StreamReader(st, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            if (!root.TryGetProperty("sources", out var srcArr) || !root.TryGetProperty("items", out var itemArr))
                return;

            var srcs = new List<(string N, string D)>(srcArr.GetArrayLength());
            foreach (var e in srcArr.EnumerateArray())
            {
                var n = e.TryGetProperty("n", out var nn) ? nn.GetString() ?? "" : "";
                var d = e.TryGetProperty("d", out var dd) ? dd.GetString() ?? "" : "";
                srcs.Add((n, d));
            }

            // 局部构建、结束才整体赋值：后台线程构建时读取方只会看到旧表或新表，不会看到写一半的表
            var titleMap = new Dictionary<uint, List<string>>();
            var detailMap = new Dictionary<uint, List<string>>();
            foreach (var it in itemArr.EnumerateArray())
            {
                if (!it.TryGetProperty("i", out var idEl) || !it.TryGetProperty("s", out var sArr)) continue;
                var id = idEl.GetUInt32();
                var itemTitles = new List<string>();
                var itemDetails = new List<string>();
                foreach (var si in sArr.EnumerateArray())
                {
                    var idx = si.GetInt32();
                    if (idx < 0 || idx >= srcs.Count) continue;
                    var (n, d) = srcs[idx];
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (!itemTitles.Contains(n)) itemTitles.Add(n);
                    if (!string.IsNullOrWhiteSpace(d) && !itemDetails.Contains(d)) itemDetails.Add(d);
                }
                if (itemTitles.Count > 0)
                {
                    titleMap[id] = itemTitles;
                    detailMap[id] = itemDetails;
                }
            }

            this.wikiTitles = titleMap;
            this.wikiDetails = detailMap;

            DalamudServices.PluginLog.Information(
                $"[幻化助手] wiki 获取方式就绪：{this.wikiTitles.Count} 个道具 / {srcs.Count} 条来源，用时 {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 载入 wiki 获取方式失败（退回本地聚合）");
            throw;   // 交给外层 LoadWikiSources 复位标志 + 退避
        }
    }

    private readonly Dictionary<uint, List<string>> sourcesResultCache = [];

    /// <summary>取某道具的获取方式（wiki 优先，本地聚合兜底）。索引加载完成后结果静态，可缓存。</summary>
    private List<string> GetSources(uint itemId)
    {
        if (!this.config.ShowSources) return [];
        if (this.sourcesResultCache.TryGetValue(itemId, out var cached)) return cached;

        LoadWikiSources();
        LoadItemSources();

        var list = new List<string>();
        if (this.wikiTitles.TryGetValue(itemId, out var w)) list.AddRange(w);
        if (this.itemSources.TryGetValue(itemId, out var l))
            foreach (var s in l) if (!list.Contains(s)) list.Add(s);

        this.sourcesResultCache[itemId] = list;
        return list;
    }

    /// <summary>取某道具的获取方式详情（wiki 的 d 字段：商店名 / 所需材料 / 坐标）。</summary>
    private List<string> GetSourceDetails(uint itemId)
    {
        if (!this.config.ShowSources) return [];
        LoadWikiSources();
        return this.wikiDetails.TryGetValue(itemId, out var d) ? d : [];
    }

    // ══════════════════════════════════════════════════════════
    //  写入游戏内「幻化模板」（投影模板）
    //    通过游戏自身的幻化模板 Agent 写入，职业/部位校验由游戏负责。
    //    流程：
    //      ① 幻化模板编辑器必须已打开（AgentMiragePrismMiragePlate 活跃）
    //      ② 逐槽位：agent->Data->SelectedItemIndex = 目标槽
    //                agent->SetSelectedItemData(PrismBox, 投影台下标, 道具ID, 染剂1, 染剂2)
    //      ③ 提交后由游戏自己保存（或玩家点保存）
    //    前置条件（游戏规则，不是我们能绕的）：
    //      · 装备必须已在投影台（幻化柜）里
    //      · 必须在安全区
    // ══════════════════════════════════════════════════════════

    /// <summary>幻化模板的 12 个槽位（顺序与游戏「幻化模板」界面一致）。</summary>
    private static readonly string[] PlateSlotNames =
        ["主手", "副手", "头部", "上衣", "手部", "腿部", "脚部", "耳坠", "项链", "手镯", "戒指1", "戒指2"];

    private int targetPlate = 1;            // 目标模板号 1..20
    private string plateStatus = "";
    private readonly Dictionary<uint, int> prismBoxIndex = [];   // 道具ID → 投影台内下标
    private readonly HashSet<uint> armoireItemIds = [];          // 收藏柜里的道具ID
    private Dictionary<uint, uint>? cabinetIdByItem;             // 道具ID → 收藏柜柜位ID（Cabinet 表行号，不是道具ID！）
    private int plateWriteDone, plateWriteSkipped, plateWriteNoDye;

    /// <summary>一个槽位的写入任务（两阶段：先放道具，等游戏生效后再设染剂）。</summary>
    private struct PlateJob
    {
        public int Slot;          // 幻化模板槽位 0..11
        public uint ItemId;
        public byte Stain0, Stain1;
        public int SourceKind;    // 1 = 投影台，2 = 收藏柜
        public uint SourceId;     // 投影台内下标 / 收藏柜道具ID
        public int Stage;         // 0=已发放入请求 1=设染剂0 2=设染剂1 3=完成
        public int Tries;         // 等游戏生效的重试次数
    }

    private readonly Queue<PlateJob> plateQueue = new();
    private PlateJob? plateCurrent;           // 正在处理的槽位（null = 空闲）
    private int plateOpenTries;               // 自动打开幻化模板界面的等待计数
    private int armoireQueued;                // 本批走了收藏柜的数量
    private int cabinetFallback;              // 柜位ID兜底次数
    private int notInContainer;               // 两个容器都没有的数量
    private bool plateIndexSet;               // 本批次是否已把编辑器切到目标模板
    private int applyStage = -1;              // 应用模板的分帧状态机（-1 = 空闲）
    private int applyTarget = 1;
    private int applyWait;                    // 等待模板数据加载的帧计数

    /// <summary>我们识别的槽位名 → 幻化模板槽位下标（不适用返回 -1）。</summary>
    private static int PlateSlotIndexOf(string slot) => slot switch
    {
        "主手" => 0, "副手" => 1, "头部" => 2, "上衣" => 3,
        "手部" => 4, "腿部" => 5, "脚部" => 6, "耳坠" => 7,
        "项链" => 8, "手镯" => 9, "戒指" => 10,
        _ => -1,
    };

    /// <summary>从 Omni 缓存里建「道具ID → 投影台下标」映射（写模板必须要下标）。</summary>
    private void LoadPrismBoxIndex()
    {
        if (this.prismBoxIndex.Count > 0) return;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "OmniToolbox", "InventoryCache.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("Characters", out var chars)) return;
            foreach (var c in chars.EnumerateObject())
            {
                if (!c.Value.TryGetProperty("GlamourDresserItems", out var arr)) continue;
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("ItemID", out var iid)) continue;
                    var slot = e.TryGetProperty("SlotIndex", out var si) ? si.GetInt32() : -1;
                    if (slot >= 0) this.prismBoxIndex[iid.GetUInt32()] = slot;
                }
            }
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 读投影台下标失败"); }
    }

    /// <summary>建「道具ID → 收藏柜柜位ID」索引（游戏要的是柜位ID，不是道具ID）。</summary>
    private unsafe void LoadCabinetIdsIfNeeded()
    {
        if (this.cabinetIdByItem != null) return;
        var map = new Dictionary<uint, uint>();
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                try
                {
                    var iid = row.Item.RowId;
                    if (iid != 0 && !map.ContainsKey(iid)) map[iid] = row.RowId;
                }
                catch { }
            }
            DalamudServices.PluginLog.Information($"[幻化助手] 收藏柜柜位索引就绪：{map.Count} 个道具");
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 读 Cabinet 表失败");
            this.cabinetIdByItem = null;   // 不缓存半成品，下次重试
            return;
        }
        this.cabinetIdByItem = map;
    }

    /// <summary>把识别结果排进写入队列。</summary>
    private void QueuePlateWrite()
    {
        LoadPrismBoxIndex();
        this.plateQueue.Clear();
        this.plateCurrent = null;
        this.plateWriteDone = 0;
        this.plateWriteSkipped = 0;
        this.plateWriteNoDye = 0;
        this.plateIndexSet = false;
        this.armoireQueued = 0;
        this.cabinetFallback = 0;
        this.notInContainer = 0;

        foreach (var r in this.results)
        {
            var slot = PlateSlotIndexOf(r.Slot);
            if (slot < 0) { this.plateWriteSkipped++; continue; }      // 副手/戒指等特例
            int srcKind; uint srcId;
            if (this.prismBoxIndex.TryGetValue(r.ItemId, out var boxIndex))
            {
                srcKind = 1; srcId = (uint)boxIndex;                   // 投影台（幻化柜）
            }
            else if (this.armoireItemIds.Contains(r.ItemId))
            {
                LoadCabinetIdsIfNeeded();
                if (this.cabinetIdByItem != null && this.cabinetIdByItem.TryGetValue(r.ItemId, out var cabId))
                {
                    srcKind = 2; srcId = cabId;                        // 收藏柜：柜位ID
                }
                else
                {
                    // 兜底：查不到柜位ID 时退回用道具ID（并记日志，便于定位）
                    srcKind = 2; srcId = r.ItemId;
                    this.cabinetFallback++;
                    DalamudServices.PluginLog.Information(
                        $"[幻化助手] 收藏柜：道具 {r.ItemId} 查不到柜位ID，退回用道具ID");
                }
                this.armoireQueued++;
            }
            else
            {
                this.notInContainer++;                                 // 两个容器里都没有
                continue;
            }
            byte s0 = 0, s1 = 0;
            var dyes = r.Dyes.Where(d => !d.IsEmpty).Take(2).ToList();
            if (dyes.Count > 0 && dyes[0].Id < 256) s0 = (byte)dyes[0].Id;
            if (dyes.Count > 1 && dyes[1].Id < 256) s1 = (byte)dyes[1].Id;
            this.plateQueue.Enqueue(new PlateJob
            {
                Slot = slot, ItemId = r.ItemId, Stain0 = s0, Stain1 = s1,
                SourceKind = srcKind, SourceId = srcId,
            });
        }

        var detail = new List<string>();
        if (this.armoireQueued > 0) detail.Add($"收藏柜 {this.armoireQueued} 件");
        if (this.notInContainer > 0) detail.Add($"{this.notInContainer} 件两个容器里都没有");
        if (this.plateWriteSkipped - this.notInContainer > 0) detail.Add($"{this.plateWriteSkipped - this.notInContainer} 件槽位不支持");
        if (this.cabinetFallback > 0) detail.Add($"{this.cabinetFallback} 件柜位ID用兜底值");
        var tail = detail.Count > 0 ? "（" + string.Join("，", detail) + "）" : "";

        this.plateStatus = this.plateQueue.Count > 0
            ? $"准备写入 {this.plateQueue.Count} 件到模板 {this.targetPlate}{tail}"
            : $"没有可写入的槽位{tail} —— 需先把装备放进投影台或收藏柜。";
        DalamudServices.PluginLog.Information($"[幻化助手] 排队完成：{this.plateStatus}");
    }

    /// <summary>
    /// 每帧推进一步（不要在同一帧连续调 Agent）。
    /// 流程：① 选中槽位 + 放道具 → ② 下一帧确认游戏已生效 → ③ 逐槽设染剂。
    /// 染剂用 SetItemStain，需要「染剂道具在背包里的指针」；没有那瓶染剂就跳过该染剂。
    /// </summary>
    private unsafe void TickPlateWrite()
    {
        if (this.plateCurrent == null && this.plateQueue.Count == 0) return;

        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlate.Instance();
        // 就绪判定：Agent 活跃 + Data 可读。
        // addon 可见性只写进日志做诊断，不作为阻断条件（之前拿它当门槛导致把自己堵死）。
        var addonState = "?";
        try
        {
            var atm = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
            var unit = atm != null ? atm->GetAddonByName("MiragePrismMiragePlate") : null;
            addonState = unit == null ? "无" : (unit->IsVisible ? "可见" : "存在但不可见");
        }
        catch { }

        var ready = agent != null && agent->IsAgentActive() && agent->Data != null;

        if (!ready)
        {
            // 界面没开 → 自动尝试打开一次，等 3 秒；失败就提示手动打开，但不阻断
            if (this.plateOpenTries == 0)
            {
                OmenTools.Interop.Game.ExecuteCommand.Implementations.GlamourPlateCommand.Enter();
                DalamudServices.PluginLog.Information($"[幻化助手] 尝试自动打开幻化模板界面（Enter）addon={addonState}");
            }
            this.plateOpenTries++;
            if (this.plateOpenTries > 180)
            {
                this.plateStatus =
                    $"幻化模板界面没打开（addon={addonState}）—— 请在游戏里手动打开「幻化模板」界面（要站在投影台前），再点「写入模板」。";
                DalamudServices.PluginLog.Information($"[幻化助手] 自动打开失败 addon={addonState}，请手动打开");
                this.plateQueue.Clear();
                this.plateCurrent = null;
                this.plateOpenTries = 0;
            }
            else
            {
                this.plateStatus = $"正在打开幻化模板界面…（addon={addonState}）";
            }
            return;
        }

        this.plateOpenTries = 0;

        // 关键：写入是写进「编辑器当前指向的那套模板」，所以必须先切到目标模板
        if (!this.plateIndexSet)
        {
            var target = (uint)Math.Clamp(this.targetPlate - 1, 0, 19);
            agent->Data->SelectedMiragePlateIndex = target;
            this.plateIndexSet = true;
            this.plateStatus = $"已把幻化模板编辑器切到模板 {this.targetPlate}，下一帧开始写入…";
            DalamudServices.PluginLog.Information(
                $"[幻化助手] 编辑器切到模板 {this.targetPlate}（index={target}），addon={addonState}");
            return;   // 等一帧让游戏应用切换
        }

        if (this.plateCurrent == null && this.plateQueue.Count > 0)
            DalamudServices.PluginLog.Information(
                $"[幻化助手] 开始写入：agent 活跃，addon={addonState}，待写 {this.plateQueue.Count} 件，目标模板 {this.targetPlate}");

        // ── 取下一个任务：发放入请求 ──
        if (this.plateCurrent == null)
        {
            var job = this.plateQueue.Dequeue();
            agent->Data->SelectedItemIndex = (uint)job.Slot;
            agent->SetSelectedItemData(
                (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData.ItemSource)job.SourceKind,
                job.SourceId,
                job.ItemId,
                job.Stain0,
                job.Stain1);
            agent->Data->HasChanges = true;
            job.Stage = 0;
            this.plateCurrent = job;
            return;   // 等下一帧看游戏有没有真的写进去
        }

        var cur = this.plateCurrent.Value;
        ref var slotItem = ref agent->Data->CurrentItems[cur.Slot];

        switch (cur.Stage)
        {
            case 0:
                // 等游戏把道具写进这个槽位（最多等 30 帧，避免卡死）
                if (slotItem.ItemId == 0)
                {
                    cur.Tries++;
                    if (cur.Tries > 30)
                    {
                        this.plateWriteSkipped++;
                        this.plateCurrent = null;
                        return;
                    }
                    this.plateCurrent = cur;
                    return;
                }
                cur.Stage = 1;
                this.plateCurrent = cur;
                this.plateWriteDone++;
                DalamudServices.PluginLog.Information(
                    $"[幻化助手] 槽位 {cur.Slot} 道具已进模板（itemId={slotItem.ItemId}）");
                return;

            case 1:   // 染剂槽 1
                if (cur.Stain0 != 0)
                {
                    var (cnt, _, ptr, dyeId) = FindDyeItem(cur.Stain0);
                    if (cnt > 0 && ptr != 0)
                    {
                        slotItem.Flags = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData.ItemFlag)((uint)slotItem.Flags | 1u);
                        agent->SetItemStain((uint)cur.Slot, cur.Stain0, (FFXIVClientStructs.FFXIV.Client.Game.InventoryItem*)ptr, dyeId, 0);
                    }
                    else
                    {
                        this.plateWriteNoDye++;
                    }
                }
                cur.Stage = 2;
                this.plateCurrent = cur;
                return;

            case 2:   // 染剂槽 2
                if (cur.Stain1 != 0)
                {
                    var (cnt2, _, ptr2, dyeId2) = FindDyeItem(cur.Stain1);
                    if (cnt2 > 0 && ptr2 != 0)
                    {
                        slotItem.Flags = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData.ItemFlag)((uint)slotItem.Flags | 0x20u);
                        agent->SetItemStain((uint)cur.Slot, cur.Stain1, (FFXIVClientStructs.FFXIV.Client.Game.InventoryItem*)ptr2, dyeId2, 1);
                    }
                    else
                    {
                        this.plateWriteNoDye++;
                    }
                }
                agent->Data->HasChanges = true;
                agent->CharaView.IsUpdatePending = true;
                this.plateCurrent = null;

                var left = this.plateQueue.Count;
                this.plateStatus = left > 0
                    ? $"写入中… 已处理 {this.plateWriteDone} 件（含染剂），剩 {left} 件"
                    : $"已写入 {this.plateWriteDone} 件到模板 {this.targetPlate}"
                      + (this.plateWriteNoDye > 0 ? $"，另有 {this.plateWriteNoDye} 个染剂因背包里没有对应染剂而跳过" : "")
                      + (this.plateWriteSkipped > 0 ? $"，{this.plateWriteSkipped} 件因不在投影台而跳过" : "")
                      + "。请在游戏里点「保存」，或直接点「应用模板」。";
                return;
        }
    }

    /// <summary>请求应用（穿上）第 N 套幻化模板 —— 真正的指令由每帧 TickApplyPlate 分帧发出。</summary>
    private void ApplyPlate(int plateIndex)
    {
        this.applyTarget = Math.Clamp(plateIndex, 1, 20);
        this.applyStage = 0;
        this.plateStatus = $"准备应用幻化模板 {this.applyTarget}…";
        DalamudServices.PluginLog.Information($"[幻化助手] 请求应用模板 {this.applyTarget}");
    }

    /// <summary>
    /// 分帧发送应用指令 —— Enter / ApplyPlate / Exit 每条占一帧。
    /// 挤在同一帧发会掉指令（这是之前"应用失效"的原因）。
    /// </summary>
    private unsafe void TickApplyPlate()
    {
        if (this.applyStage < 0) return;
        try
        {
            switch (this.applyStage)
            {
                // 投影需要先进入幻化模板上下文：Enter → ApplyPlate → Exit。
                //   拆成多帧发送，避免与写入争用同一帧的指令队列。
                // 投影台以外模板数据可能未加载，此时直接投影会被游戏忽略 → 先请求加载。
                case 0:
                    var mm = FFXIVClientStructs.FFXIV.Client.Game.MirageManager.Instance();
                    var loaded = mm != null && mm->GlamourPlatesLoaded;
                    DalamudServices.PluginLog.Information($"[幻化助手] 应用流程开始：GlamourPlatesLoaded={loaded}");
                    if (!loaded)
                    {
                        OmenTools.Interop.Game.ExecuteCommand.Implementations.GlamourPlateCommand.Request();
                        this.applyWait = 0;
                        this.plateStatus = "正在加载幻化模板数据…";
                        this.applyStage = 10;
                    }
                    else
                    {
                        this.applyStage = 1;
                    }
                    break;

                // 等模板数据加载（最多约 2 秒），加载好或超时都继续走投影
                case 10:
                    this.applyWait++;
                    var mm2 = FFXIVClientStructs.FFXIV.Client.Game.MirageManager.Instance();
                    if ((mm2 != null && mm2->GlamourPlatesLoaded) || this.applyWait > 120)
                    {
                        DalamudServices.PluginLog.Information(
                            $"[幻化助手] 模板数据加载{(mm2 != null && mm2->GlamourPlatesLoaded ? "完成" : "超时")}（等了 {this.applyWait} 帧），开始投影");
                        this.applyStage = 1;
                    }
                    break;

                // 附加调用游戏原生的 OpenForGearset
                case 1:
                    // 附加尝试：原生 OpenForGearset（仅作补充，命令路径仍为主）
                    try
                    {
                        var plateAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlate.Instance();
                        if (plateAgent != null && plateAgent->IsAgentActive())
                        {
                            plateAgent->OpenForGearset(-1, this.applyTarget - 1, 0);
                            DalamudServices.PluginLog.Information(
                                $"[幻化助手] 附带调用 OpenForGearset(gearset=-1, 模板={this.applyTarget - 1})");
                        }
                    }
                    catch (Exception ex)
                    {
                        DalamudServices.PluginLog.Information(ex, "[幻化助手] OpenForGearset 调用异常（忽略）");
                    }

                    // 继续走命令路径
                    OmenTools.Interop.Game.ExecuteCommand.Implementations.GlamourPlateCommand.Enter();
                    this.plateStatus = "进入幻化模板上下文…";
                    this.applyStage = 4;
                    break;

                case 4:
                    this.applyStage = 5;      // 等一帧让上下文生效
                    break;

                case 5:
                    OmenTools.Interop.Game.ExecuteCommand.Implementations.GlamourPlateCommand
                        .ApplyPlate((uint)(this.applyTarget - 1));
                    DalamudServices.PluginLog.Information($"[幻化助手] 已发送 ApplyPlate({this.applyTarget - 1})");
                    this.applyStage = 6;
                    break;

                case 6:
                    OmenTools.Interop.Game.ExecuteCommand.Implementations.GlamourPlateCommand.Exit();
                    this.applyStage = -1;
                    this.plateStatus = $"已发送投影指令（模板 {this.applyTarget}）。";
                    DalamudServices.PluginLog.Information($"[幻化助手] 模板 {this.applyTarget} 应用流程完成（Enter→Apply→Exit）");
                    break;
            }
        }
        catch (Exception ex)
        {
            this.applyStage = -1;
            this.plateStatus = "应用模板失败：" + ex.Message;
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 应用模板异常");
        }
    }

    /// <summary>幻化模板编辑器是否已就绪（Agent 活跃 + Data 可读）。</summary>
    private unsafe bool IsPlateEditorReady()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlate.Instance();
            return agent != null && agent->IsAgentActive() && agent->Data != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 识别结果操作区（原「试穿与模板」标签页合并至此）：
    /// 写入 / 应用游戏幻化模板 + 本地试穿，都作用于当前识别结果。
    /// </summary>
    private void DrawResultActionsSection()
    {
        if (this.results.Count == 0) return;

        ImGui.Spacing();
        DrawSectionTitle("装备操作", "写入游戏模板，或先本地试穿看看效果");

        // 第一行：目标模板 + 写入 + 应用
        ImGui.SetNextItemWidth(OmniTheme.Scale(72f));
        if (ImGui.InputInt("模板###plateNo", ref this.targetPlate))
            this.targetPlate = Math.Clamp(this.targetPlate, 1, 20);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("幻化模板编号 1 ~ 20（输入后按回车确认）");

        ImGui.SameLine();
        if (ImGui.Button("写入模板###writePlate")) QueuePlateWrite();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把识别到的装备 + 染剂写入目标模板\n（需要先在游戏里打开投影台的「幻化模板」界面）");

        ImGui.SameLine();
        if (ImGui.Button("应用模板###applyPlate")) ApplyPlate(this.targetPlate);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("投影（穿上）目标模板");

        // 第二行：本地试穿 + 取消
        if (ImGui.Button("试穿这套###tryOnResults")) StartTryOnFromResults();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立刻穿到你自己身上看效果（只影响本地显示，别人看不到、也不改真实幻化）");

        ImGui.SameLine();
        using (ImRaii.Disabled(!this.tryOnActive))
        {
            if (ImGui.Button("取消试穿###stopTryOn")) StopTryOn(clear: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("恢复试穿前的样子");
        }

        if (!string.IsNullOrEmpty(this.plateStatus))
            ImGui.TextWrapped(this.plateStatus);
        if (!string.IsNullOrEmpty(this.tryOnStatus))
            ImGui.TextDisabled(this.tryOnStatus);

        // 容器校验（游戏规则：写入模板需要装备在投影台或收藏柜）
        this.LoadPrismBoxIndexIfNeeded();
        if (this.prismBoxIndex.Count > 0 || this.armoireItemIds.Count > 0)
        {
            var inDresser = this.results.Count(r => this.prismBoxIndex.ContainsKey(r.ItemId));
            var inArmoire = this.results.Count(r => !this.prismBoxIndex.ContainsKey(r.ItemId) && this.armoireItemIds.Contains(r.ItemId));
            var canWrite = inDresser + inArmoire;
            var noSlot = this.results.Count(r => PlateSlotIndexOf(r.Slot) < 0);   // 面部配饰/时尚配饰：游戏幻化模板没有这两个槽
            ImGui.TextDisabled(
                $"容器校验：识别到 {this.results.Count} 件"
                + (noSlot > 0 ? $"（其中 {noSlot} 件是面部配饰/时尚配饰，幻化模板没有这两个槽，不参与写入）" : "")
                + $" —— 投影台 {inDresser} 件 + 收藏柜 {inArmoire} 件 = 可写入 {canWrite} 件"
                + (canWrite < this.results.Count - noSlot
                    ? $"（另 {this.results.Count - noSlot - canWrite} 件两个容器里都没有，需先放进投影台或收藏柜）"
                    : "（全部可写入）"));
        }
    }

    private void LoadPrismBoxIndexIfNeeded()
    {
        if (this.prismBoxIndex.Count == 0) LoadPrismBoxIndex();
    }

    // ══════════════════════════════════════════════════════════
    //  ④ 本地试穿（不需要投影台、不需要持有装备）
    //
    //    ★ 槽位号用的是**内置「Parts」/ 角色查看窗口的槽位号**，不是游戏幻化模板槽位号！
    //      0 武器  1 副手  2 头部  3 身体  4 手部  5 (空)  6 腿部  7 脚部
    //      8 耳饰  9 项链  10 手镯  11 右戒指  12 左戒指
    //      证据（两条独立来源互相印证）：
    //        ① 内置 BetterGlamourManagement.Parts：
    //             Head=2 Body=3 Hands=4 Legs=6 Feet=7 Ears=8 Neck=9 Wrists=10 RingRight=11 RingLeft=12
    //           InspectSlots = {0,1,2,3,4,6,7,8,9,10,11,12}（不含 5）
    //        ② 玩家自己存的预设 + Item 表逐条核对：
    //             16622 头花→2 / 50268 礼裙→3 / 32326 套袖→4 / 50264 短裙→6 / 14976 礼鞋→7
    //             9293 耳环→8 / 9292 项链→9 / 9294 手镯→10 / 9295 戒指→11、12
    //        两条完全吻合 → 以此为准。（早先按"游戏幻化模板槽位号"写是错的，腿以下整体差一格。）
    //
    //    试穿走 OmniToolbox.Items.ItemPreviewService（OmniToolbox.Common 的公开类）：
    //      自己 new 一个实例 + 每帧 Tick + PreviewEquipment(itemId, slot, stain0, stain1)
    //      → 立刻在自己身上生效，不需要投影台、不需要持有装备、不需要重载插件。
    // ══════════════════════════════════════════════════════════

    /// <summary>识别槽位名 → 槽位号（0~12，见上面注释；无对应返回 -1，例如副手）。</summary>
    private static int OmniSlotOf(string slot) => slot switch
    {
        "主手" => 0,
        "头部" => 2, "上衣" => 3, "手部" => 4, "腿部" => 6, "脚部" => 7,
        "耳坠" => 8, "项链" => 9, "手镯" => 10,
        "戒指" => 11,          // 第二枚戒指 → 12
        _ => -1,
    };

    /// <summary>取一行的两个染剂 ID（同一套 Stain 体系，可直接搬）。</summary>
    private static (byte S0, byte S1) DyePair(SlotResult r)
    {
        byte s0 = 0, s1 = 0;
        var dyes = r.Dyes.Where(d => !d.IsEmpty).Take(2).ToList();
        if (dyes.Count > 0 && dyes[0].Id < 256) s0 = (byte)dyes[0].Id;
        if (dyes.Count > 1 && dyes[1].Id < 256) s1 = (byte)dyes[1].Id;
        return (s0, s1);
    }

    /// <summary>识别结果 → 槽位条目（同槽位去重；第二枚戒指落到 12 号槽）。</summary>
    private List<(int Slot, uint ItemId, byte S0, byte S1)> BuildSlotItems()
    {
        var items = new List<(int Slot, uint ItemId, byte S0, byte S1)>();
        var placed = new HashSet<string>();

        foreach (var r in this.results)
        {
            var slot = OmniSlotOf(r.Slot);
            if (slot < 0 || r.ItemId == 0) continue;             // 副手不参与（内置按装备自判武器槽）
            if (slot == 11) { if (!placed.Add("戒指")) continue; }   // 第二枚戒指留给下面
            else if (!placed.Add(r.Slot)) continue;

            var (s0, s1) = DyePair(r);
            items.Add((slot, r.ItemId, s0, s1));
        }

        var rings = this.results.Where(r => r.Slot == "戒指" && r.ItemId != 0).ToList();
        if (rings.Count > 1)
        {
            var (s0, s1) = DyePair(rings[1]);
            items.Add((12, rings[1].ItemId, s0, s1));
        }
        return items;
    }

    // ── 本地试穿：用内置同款的 ItemPreviewService（公开 API）────────────────

    /// <summary>把当前识别结果排进试穿队列（逐帧补，参见 OnFrameworkTick）。</summary>
    private void StartTryOnFromResults()
    {
        var items = this.BuildSlotItems();
        this.activePresetIndex = -1;
        this.activePresetFromGearset = false;
        this.StartTryOnItems(items, 0, 0);
    }

    private void StartTryOnPreset(int presetIndex, bool fromGearset)
    {
        if (presetIndex < 0 || presetIndex >= this.config.Presets.Count)
            return;

        var preset = this.config.Presets[presetIndex];

        this.activePresetIndex = presetIndex;
        this.activePresetFromGearset = fromGearset;
        this.StartTryOnItems(
            this.BuildPresetTryOnItems(preset),
            preset.GlassesID,
            preset.HairstyleID);
    }

    private List<(int Slot, uint ItemId, byte S0, byte S1)> BuildPresetTryOnItems(LKami9GlamourPreset preset) =>
        preset.Items
            .Where(x => x.ItemID != 0)
            .GroupBy(x => x.Slot)
            .Select(x => x.Last())
            .OrderBy(x => x.Slot)
            .Select(x => (x.Slot, x.ItemID, x.Stain0, x.Stain1))
            .ToList();

    private void StartTryOnItems(
        IReadOnlyList<(int Slot, uint ItemId, byte S0, byte S1)> items,
        uint glassesId,
        uint hairstyleId)
    {
        if (items.Count == 0 && glassesId == 0 && hairstyleId == 0)
        {
            this.tryOnStatus = "没有可试穿的部位。";
            return;
        }

        try
        {
            this.previewService ??= new OmniToolbox.Items.ItemPreviewService();
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 创建试穿服务失败");
            this.tryOnStatus = "试穿服务不可用（看 /xllog）。";
            return;
        }

        this.tryOnItems.Clear();
        this.tryOnItems.AddRange(items);
        this.tryOnSkipped.Clear();
        this.tryOnLastIndex = -1;
        this.tryOnRetry = 0;
        this.tryOnHeadDone = false;
        this.tryOnGlassesId = glassesId;
        this.tryOnHairstyleId = hairstyleId;
        this.tryOnActive = true;
        var sourceName = this.activePresetIndex >= 0 && this.activePresetIndex < this.config.Presets.Count
            ? DisplayPresetName(this.config.Presets[this.activePresetIndex])
            : "当前识别结果";
        this.tryOnStatus = $"正在试穿「{sourceName}」（{items.Count} 件）…";
    }

    /// <summary>停止试穿并恢复原样。</summary>
    private void StopTryOn(bool clear)
    {
        var wasActive = this.tryOnActive;
        this.tryOnActive = false;
        this.tryOnItems.Clear();
        this.tryOnSkipped.Clear();
        this.tryOnLastIndex = -1;
        this.tryOnRetry = 0;
        this.tryOnHeadDone = false;
        this.tryOnGlassesId = 0;
        this.tryOnHairstyleId = 0;
        this.activePresetIndex = -1;
        this.activePresetFromGearset = false;
        if (!clear) return;

        try
        {
            this.previewService?.ClearPreviewResidue();
            if (wasActive) this.tryOnStatus = "已取消试穿。";
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 取消试穿失败"); }
    }

    /// <summary>
    /// 每帧推进试穿：每帧只处理一件（同帧多次调模型接口会互相覆盖）。
    /// 采用「找第一件还没生效的」写法 —— 游戏每帧会重置模型、换装备/切地图也会清掉，
    /// 这样能自动补回来（和内置的行为一致），直到玩家点「取消试穿」。
    /// </summary>
    private void OnFrameworkTick(Dalamud.Plugin.Services.IFramework framework)
    {
        this.ApplyPendingOcr();   // OCR 结果回到框架线程才应用（读游戏内存 + 改 UI 状态）
        this.TickPlateWrite();
        this.TickApplyPlate();
        this.TickGearsetBinding();

        var svc = this.previewService;
        if (svc == null) return;

        try
        {
            svc.Tick(framework);            // 内置同款：切地图/换人时自动清残留
            if (!this.tryOnActive || this.tryOnItems.Count == 0) return;

            // ① 装备：找第一件「还没生效」的补上
            var idx = -1;
            for (var i = 0; i < this.tryOnItems.Count; i++)
            {
                if (this.tryOnSkipped.Contains(i)) continue;
                var t = this.tryOnItems[i];
                if (svc.IsEquipmentPreviewApplied(t.ItemId, t.Slot, t.S0, t.S1)) continue;
                idx = i;
                break;
            }

            if (idx >= 0)
            {
                var it = this.tryOnItems[idx];
                var ok = svc.PreviewEquipment(it.ItemId, it.Slot, it.S0, it.S1);

                if (this.tryOnLastIndex == idx) this.tryOnRetry++;
                else { this.tryOnLastIndex = idx; this.tryOnRetry = 0; }

                if (!ok || this.tryOnRetry >= 6)
                {
                    DalamudServices.PluginLog.Information(
                        $"[幻化助手] 试穿跳过 道具 {it.ItemId}（槽位 {it.Slot}，ok={ok}）");
                    this.tryOnSkipped.Add(idx);
                    this.tryOnRetry = 0;
                }
                return;
            }

            // ② 全部生效 → 眼镜/发型。游戏换区、换角或重载模型时也会被重置，
            //    所以这里和 Omni 内置实现一样，每帧校验并补回。
            if (!svc.IsGlassesPreviewApplied(this.tryOnGlassesId))
            {
                svc.PreviewGlasses(this.tryOnGlassesId);
                this.tryOnHeadDone = false;
                return;
            }

            if (this.tryOnHairstyleId != 0)
            {
                if (!svc.IsHairstylePreviewApplied(this.tryOnHairstyleId))
                {
                    svc.PreviewHairstyle(this.tryOnHairstyleId);
                    this.tryOnHeadDone = false;
                    return;
                }
            }
            else if (!this.tryOnHeadDone)
            {
                svc.RestoreHairstylePreview();
            }

            if (!this.tryOnHeadDone)
            {
                this.tryOnHeadDone = true;

                var n = this.tryOnItems.Count - this.tryOnSkipped.Count;
                this.tryOnStatus = $"试穿中（{n} 件）。只改本地显示；点「取消试穿」可还原。";
                DalamudServices.PluginLog.Information($"[幻化助手] 试穿已应用：{n} 件");
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 试穿推进异常，已停止");
            this.StopTryOn(clear: false);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  染剂持有检测（含「染剂合并」处理）
    //    ★ FF14 把染剂合并过：同一个颜色可能对应多个道具，而且 Stain 表的
    //      Item 字段本身是**数组**（长度 2）。必须把所有非零项都查一遍，
    //      否则只认 Item[0] 会漏掉「特制XX染剂」。
    //    例：煤玉黑 -> [#13115 煤玉黑染剂, #8733 特制煤玉黑染剂]
    //        素雪白 -> [#52254 通用染剂]                （合并成通用染剂）
    //        追加染剂 1/2 = #52255 / #52256
    // ══════════════════════════════════════════════════════════

    private Dictionary<uint, List<uint>>? dyeItemsByStain;   // 染剂ID → 可能的道具ID列表
    private readonly Dictionary<uint, (int Count, string Detail)> dyeOwnedCache = [];

    /// <summary>建「染剂ID → 道具ID列表」索引（把 Stain.Item 数组里所有非零项都收进来）。</summary>
    private unsafe void LoadDyeItemsIfNeeded()
    {
        if (this.dyeItemsByStain != null) return;
        var map = new Dictionary<uint, List<uint>>();
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
            foreach (var s in sheet)
            {
                if (s.RowId == 0) continue;
                var ids = new List<uint>();
                try
                {
                    foreach (var iref in s.Item)
                    {
                        var iid = iref.RowId;
                        if (iid != 0) ids.Add(iid);
                    }
                }
                catch { }
                map[s.RowId] = ids;
            }
            DalamudServices.PluginLog.Information($"[幻化助手] 染剂道具索引就绪：{map.Count} 个染剂");
        }
        catch (Exception ex) { DalamudServices.PluginLog.Information(ex, "[幻化助手] 读 Stain 表失败"); }
        this.dyeItemsByStain = map;
    }

    /// <summary>
    /// 查某染剂的可染色道具在背包/幻化柜等处总共持有多少，
    /// 并返回可用于写入模板的那个道具指针（没有则 0）。
    /// </summary>
    private unsafe (int Count, string Detail, nint ItemPtr, uint ItemId) FindDyeItem(uint stainId)
    {
        LoadDyeItemsIfNeeded();
        var candidates = this.dyeItemsByStain!.TryGetValue(stainId, out var l) ? l : [];
        if (candidates.Count == 0) return (0, "该颜色没有对应染剂道具", 0, 0);

        var nameOf = new Func<uint, string>(id =>
        {
            try { return DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(id).Name.ToString(); }
            catch { return "#" + id; }
        });

        var total = 0;
        var details = new List<string>();
        nint foundPtr = 0;
        uint foundId = 0;

        // ① 实时扫背包（能拿到指针，写入模板要用）
        var mgr = InventoryManager.Instance();
        if (mgr != null)
        {
            foreach (var type in new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
            })
            {
                var cont = mgr->GetInventoryContainer(type);
                if (cont == null || !cont->IsLoaded) continue;
                for (var i = 0; i < cont->Size; i++)
                {
                    var slot = cont->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (!candidates.Contains(slot->ItemId)) continue;
                    total += (int)slot->Quantity;
                    var nm = nameOf(slot->ItemId);
                    if (!details.Any(d => d.StartsWith(nm))) details.Add($"{nm} x{slot->Quantity}");
                    if (foundPtr == 0) { foundPtr = (nint)slot; foundId = slot->ItemId; }
                }
            }
        }

        // ② Omni 缓存（雇员/幻化柜等 —— 那里有染剂但拿不到指针）
        if (this.omniCachedOwned.Count > 0)
        {
            foreach (var id in candidates)
            {
                if (!this.omniCachedOwned.Contains(id)) continue;
                var nm = nameOf(id);
                if (!details.Any(d => d.StartsWith(nm))) details.Add($"{nm} x?（别处）");
                total += 1;
            }
        }

        return (total, details.Count > 0 ? string.Join(" / ", details) : "", foundPtr, foundId);
    }

    /// <summary>染剂列用：持有数量（带缓存，避免每帧重扫背包）。</summary>
    private (int Count, string Detail) DyeOwned(uint stainId)
    {
        if (this.dyeOwnedCache.TryGetValue(stainId, out var v)) return v;
        var r = FindDyeItem(stainId);
        v = (r.Count, r.Detail);
        this.dyeOwnedCache[stainId] = v;
        return v;
    }

    /// <summary>
    /// 本套幻化里有几处需要**同一个染剂道具**（按道具集合判定，不按颜色 —— 因为「通用染剂」
    /// 一瓶能染多种颜色，按颜色算会重复计数）。
    /// </summary>
    private int CountSharedDye(uint stainId)
    {
        LoadDyeItemsIfNeeded();
        if (this.dyeItemsByStain == null || !this.dyeItemsByStain.TryGetValue(stainId, out var mine) || mine.Count == 0)
            return 1;
        var key = string.Join(",", mine.OrderBy(x => x));

        var n = 0;
        foreach (var r in this.results)
        {
            foreach (var dd in r.Dyes)
            {
                if (dd.IsEmpty) continue;
                if (!this.dyeItemsByStain.TryGetValue(dd.Id, out var other) || other.Count == 0) continue;
                if (string.Join(",", other.OrderBy(x => x)) == key) n++;
            }
        }
        return Math.Max(n, 1);
    }

    /// <summary>染剂悬停提示：色号 / 对应染剂道具（含合并后的通用·追加·特制）/ 持有数量。</summary>
    private string BuildDyeTooltip(DyeResult d, (int Count, string Detail) own)
    {
        LoadDyeItemsIfNeeded();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"颜色：{d.Name}（色号 {d.Id}）");
        if (d.NeedsReview) sb.AppendLine($"识别把握：{d.Score} 分（不确定，可点行尾「改」修正）");

        var items = this.dyeItemsByStain!.TryGetValue(d.Id, out var l) ? l : [];
        if (items.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"所需染剂道具（持有任意一瓶即可染出这个颜色）：");
            foreach (var id in items)
            {
                string nm;
                try { nm = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(id).Name.ToString(); }
                catch { nm = "未知"; }
                var uni = nm.Contains("通用染剂") || nm.Contains("追加染剂");
                sb.AppendLine($"  · {nm}  #{id}" + (uni ? "（通用/追加染剂：一瓶可染多种颜色）" : ""));
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("（这个颜色在游戏数据里没有对应染剂道具）");
        }

        sb.AppendLine();
        sb.AppendLine(own.Count > 0 ? $"持有：{own.Detail}  共 {own.Count}" : "持有：没有 —— 需要先获得上面任一瓶染剂");

        // 同一个染剂道具被多处共用（尤其是「通用染剂」一瓶能染多种颜色）—— 别按颜色重复算
        var shared = CountSharedDye(d.Id);
        if (shared > 1)
            sb.AppendLine($"本套幻化里有 {shared} 处用得上这个染剂 —— 同一瓶可重复使用，不需要买 {shared} 瓶。");

        if (d.IsEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("这一格是「无染色」（装备可染色但没设颜色），写入模板时会保持无染色。");
        }

        return sb.ToString().TrimEnd();
    }
    /// <summary>把脚本的失败原因翻译成人话</summary>
    private static string DescribeScriptFailure(string outp)
    {
        if (outp.Contains("NOLANG"))
            return "系统里没有中文 OCR 语言包。请到「设置 > 时间和语言 > 语言 > 中文(简体) > 语言选项」添加「光学字符识别」。";
        if (outp.Contains("NOIMAGE"))
            return "剪贴板里没有图片。先截图（或复制图片），再点这个按钮。";
        if (string.IsNullOrWhiteSpace(outp))
            return $"（脚本无输出，请看 {Path.Combine(WorkDir(), "last_error.txt")}）";
        return outp;
    }

    // ══════════════════════════════════════════════════════════
    //  索引 / 工具
    // ══════════════════════════════════════════════════════════

    private void BuildIndexIfNeeded()
    {
        if (this.itemByName != null) return;

        var map = new Dictionary<string, (uint Id, uint Icon, string Slot, bool Untradable)>();
        var slotSheet = DalamudServices.DataManager.GetExcelSheet<EquipSlotCategory>();
        var nFace = 0;
        var nFashion = 0;

        foreach (var it in DalamudServices.DataManager.GetExcelSheet<Item>())
        {
            if (it.RowId == 0) continue;
            var n = it.Name.ToString();
            if (string.IsNullOrWhiteSpace(n)) continue;

            var slot = DescribeSlot(it, slotSheet);
            if (slot == null) continue;                       // 坐骑/宠物/情感动作… 不进索引

            if (!map.ContainsKey(n))
                map[n] = (it.RowId, it.Icon, slot, it.IsUntradable);
            if (slot == "面部配饰") nFace++;
            if (slot == "时尚配饰") nFashion++;

            // 面部配饰的道具全名是「面部配饰：椭圆眼镜」，而石之家/OCR 上只会写「椭圆眼镜」
            //   → 再登记一个去掉前缀的别名，否则永远匹配不上、全掉进「未识别文本」
            var colon = n.IndexOf('：');
            if (colon > 0 && colon < n.Length - 1)
            {
                var alias = n[(colon + 1)..];
                if (!string.IsNullOrWhiteSpace(alias) && !map.ContainsKey(alias))
                    map[alias] = (it.RowId, it.Icon, slot, it.IsUntradable);
            }
        }
        this.itemByName = map;

        var stains = new Dictionary<string, uint>();
        var stainNames = new Dictionary<uint, string>();
        foreach (var s in DalamudServices.DataManager.GetExcelSheet<Stain>())
        {
            if (s.RowId == 0) continue;
            var n = s.Name.ToString();
            if (string.IsNullOrWhiteSpace(n) || stains.ContainsKey(n)) continue;
            stains[n] = s.RowId;
            stainNames[s.RowId] = n;
        }
        this.stainByName = stains;
        this.stainNameById = stainNames;
        this.dyeLex = stains.Keys.Where(k => k.Length >= 2).OrderByDescending(k => k.Length).ToList();

        DalamudServices.PluginLog.Information(
            $"[幻化助手] 索引就绪：装备 {map.Count}（面部配饰 {nFace} / 时尚配饰 {nFashion}） / 染剂 {stains.Count}");
    }

    /// <summary>
    /// 道具 → 部位名（和游戏内叫法一致的那 14 个）。返回 null = 不是幻化部位（坐骑/宠物/情感动作…）。
    /// 判定顺序：装备槽优先（老式眼镜这种 ESC=头部 的仍算头部，和内置一致）；
    ///           装备槽为空时才看道具行为 → 面部配饰 / 时尚配饰。
    /// </summary>
    private static string? DescribeSlot(Item it, ExcelSheet<EquipSlotCategory> slotSheet)
    {
        if (it.EquipSlotCategory.RowId != 0)
        {
            var s = slotSheet.GetRow(it.EquipSlotCategory.RowId);
            if (s.MainHand != 0) return "主手";
            if (s.OffHand != 0) return "副手";
            if (s.Head != 0) return "头部";
            if (s.Body != 0) return "上衣";
            if (s.Gloves != 0) return "手部";
            if (s.Legs != 0) return "腿部";
            if (s.Feet != 0) return "脚部";
            if (s.Ears != 0) return "耳坠";
            if (s.Neck != 0) return "项链";
            if (s.Wrists != 0) return "手镯";
            if (s.FingerL != 0 || s.FingerR != 0) return "戒指";
        }

        // 装备槽为空：看道具行为（通用 Action 行 ID，与内置 BetterGlamourManagement.ItemActionKind 同源）
        try
        {
            var action = it.ItemAction.Value.Action.RowId;
            if (action == 37312) return "面部配饰";     // 面部配饰（Glasses）
            if (action == 20086) return "时尚配饰";     // 时尚配饰（Ornament，阳伞那类）
        }
        catch { }

        return null;
    }

    /// <summary>石之家幻化列出的 14 个部位名（与游戏内一致）。</summary>
    private static readonly string[] SlotLabels =
    [
        "面部配饰", "时尚配饰",
        "主手", "副手", "头部", "上衣", "手部", "腿部", "脚部", "耳坠", "项链", "手镯", "戒指",
    ];

    private static bool IsSlotLabel(string s) => SlotLabels.Contains(s);

    /// <summary>
    /// 石之家常写成「头部 白色三色堇头花 煤玉黑」——OCR 拼一行后部位名会黏在装备名前，
    /// 直接去匹配装备名必然失败。这里把行首/行尾的部位名剥掉，再拿去匹配。
    /// </summary>
    private static string StripSlotLabel(string s)
    {
        foreach (var label in SlotLabels)
        {
            if (s.Length > label.Length && s.StartsWith(label)) return s[label.Length..];
            if (s.Length > label.Length && s.EndsWith(label)) return s[..^label.Length];
        }
        return s;
    }

    /// <summary>石之家/游戏界面上的按钮、标签文字 —— 不是装备名，别往「未识别文本」里塞。</summary>
    private static readonly string[] UiNoiseLex =
    [
        "生成图片", "保存图片", "查看大图", "分享", "复制链接", "复制", "编辑", "删除", "返回",
        "更多", "收起", "展开", "染色", "染剂", "幻化", "投影", "装备", "部位", "外观", "套装",
        "全部", "筛选", "搜索", "登录", "评论", "点赞", "收藏", "投稿", "浏览",
    ];

    /// <summary>明显不是装备名的碎片（部位标签、界面按钮、染色说明、纯数字）—— 不往「未识别文本」里塞。</summary>
    private static bool IsNoiseFragment(string s)
    {
        if (IsSlotLabel(s)) return true;
        foreach (var w in SpecialLex)
            if (s.Contains(w)) return true;
        if (s.Contains("染色") || s.Contains("染剂") || s.Contains("颜色")) return true;
        foreach (var w in UiNoiseLex)
            if (s == w) return true;
        if (s.All(c => !char.IsLetter(c))) return true;
        return false;
    }

    private static string StripNoise(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                continue;

            // 只归一化明确的正字变体，不能删除「口 / O / 0 / Z」：
            // 游戏里真实存在「秘银口罩」「熊口战盔」等名称。
            sb.Append(c switch
            {
                'Ø' => 'O',
                '囗' => '口',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static int Lev(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                   d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[n, m];
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        foreach (var r in this.results)
        {
            var dye = r.Dyes.Count == 0 ? "" : "  " + string.Join(" / ", r.Dyes.Select(d => d.Name));
            var src = string.IsNullOrEmpty(r.Source) ? "" : "   获取：" + r.Source;
            sb.AppendLine($"{r.Slot}：{r.Name}{dye}   [{(r.Owned > 0 ? "已持有 x" + r.Owned : "未持有")}]{src}");
        }
        return sb.ToString();
    }

    private static void CopyTextToClipboard(string text)
    {
        try { ImGui.SetClipboardText(text); } catch { }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  ⑤ 幻化预设（整合 Omni「更好的幻化管理」）
    // ══════════════════════════════════════════════════════════

    private static readonly (int Slot, string Name)[] PresetSlots =
    [
        (0, "主手"), (1, "副手"), (2, "头部"), (3, "上衣"), (4, "手部"),
        (6, "腿部"), (7, "脚部"), (8, "耳坠"), (9, "项链"), (10, "手镯"),
        (11, "戒指1"), (12, "戒指2"),
    ];

    private static readonly int[] InspectPresetSlots = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12];

    private void DrawPresetSection()
    {
        ImGui.Spacing();
        DrawSectionTitle("幻化预设", "保存、编辑、绑定套装、本地试穿与分享");

        using (ImRaii.Disabled(this.results.Count == 0))
        {
            if (ImGui.Button("当前识别结果存为预设"))
                this.AddPresetFromResults();
        }
        if (ImGui.IsItemHovered() && this.results.Count == 0)
            ImGui.SetTooltip("先识别一张图，或使用右侧按钮从角色查看窗口读取。");

        ImGui.SameLine();
        if (ImGui.Button("从角色查看窗口保存"))
            this.AddPresetFromCharacterInspect();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("先打开游戏的角色查看窗口，再点这里。会读取对方/自己的幻化、染剂、发型和眼镜。");

        ImGui.SameLine();
        if (ImGui.Button("导入 Omni 已有预设"))
            this.ImportOmniPresets();

        var autoGearset = this.config.AutoApplyBoundPreset;
        if (ImGui.Checkbox("切换套装时自动试穿绑定预设###autoGearsetPreset", ref autoGearset))
            this.config.AutoApplyBoundPreset = autoGearset;
        if (!this.config.AutoApplyBoundPreset)
            this.lastGearsetIndex = int.MinValue;

        this.NormalizePresetSelection();
        if (this.config.Presets.Count == 0)
        {
            ImGui.TextDisabled("还没有预设。可从识别结果保存、从角色查看窗口读取，或导入 Omni 幻化管理中的现有预设。");
            if (!string.IsNullOrEmpty(this.presetStatus))
                ImGui.TextWrapped(this.presetStatus);
            return;
        }

        var presetListHeight =
            this.config.Presets.Count * ImGui.GetTextLineHeightWithSpacing() +
            ImGui.GetStyle().FramePadding.Y * 2f +
            ImGui.GetStyle().ItemSpacing.Y;
        using (var listStyle = new OmniStyleScope())
        {
            listStyle.Color(ImGuiCol.FrameBg, PanelColor(OmniTheme.Tokens));
            if (ImGui.BeginListBox(
                    "###presetList",
                    new System.Numerics.Vector2(OmniTheme.Scale(260f), presetListHeight)))
            {
                var clipper = new ImGuiListClipper();
                clipper.Begin(this.config.Presets.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        ImGui.PushID(i);
                        var picked = ImGui.Selectable(
                            this.GetPresetListLabel(this.config.Presets[i], i),
                            i == this.selectedPresetIndex);
                        ImGui.PopID();
                        if (!picked)
                            continue;

                        this.SelectPreset(i);
                    }
                }
                clipper.End();

                ImGui.EndListBox();
            }
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        if (this.selectedPresetIndex < 0 || this.selectedPresetIndex >= this.config.Presets.Count)
        {
            ImGui.EndGroup();
            return;
        }

        var selected = this.config.Presets[this.selectedPresetIndex];
        ImGui.SetNextItemWidth(OmniTheme.Scale(300f));
        if (ImGui.InputText("预设名###presetName", ref this.presetNameBuffer, 80))
            selected.Name = this.presetNameBuffer;

        this.DrawPresetGearsetSelector(selected);

        var actionHeight = OmniTheme.Scale(30f);
        var actionSpacing = ImGui.GetStyle().ItemSpacing.X;
        var actionWidth = MathF.Max(
            OmniTheme.Scale(72f),
            (ImGui.GetContentRegionAvail().X - actionSpacing * 3f) / 4f);
        var actionSize = new System.Numerics.Vector2(actionWidth, actionHeight);

        if (ImGui.Button("试穿 / 应用###presetTryOn", actionSize))
            this.StartTryOnPreset(this.selectedPresetIndex, false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在本地试穿这条预设（只影响自己看到的外观）");
        ImGui.SameLine();

        using (ImRaii.Disabled(!this.tryOnActive))
        {
            if (ImGui.Button("解除试穿###presetStopTryOn", actionSize))
            {
                this.StopTryOn(clear: true);
                this.presetStatus = "已解除试穿，恢复真实外观。";
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("恢复试穿前的样子（同角色快捷栏的「清除试穿」）");
        ImGui.SameLine();

        if (ImGui.Button("复制分享文本###presetCopy", actionSize))
            this.CopyPresetText(selected);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("复制 Omni 兼容的分享文本");
        ImGui.SameLine();
        if (ImGui.Button("删除预设###presetDelete", actionSize))
            this.DeleteSelectedPreset();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("删除这条预设（不影响游戏内幻化模板）");

        this.DrawPresetItemTable(selected);
        this.DrawPresetSpecialRows(selected);
        ImGui.EndGroup();

        if (!string.IsNullOrEmpty(this.presetStatus))
            ImGui.TextWrapped(this.presetStatus);
    }

    private void NormalizePresetSelection()
    {
        if (this.config.Presets.Count == 0)
        {
            this.selectedPresetIndex = -1;
            this.presetEditSlot = -1;
            return;
        }

        if (this.selectedPresetIndex < 0 || this.selectedPresetIndex >= this.config.Presets.Count)
            this.SelectPreset(0);
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= this.config.Presets.Count)
            return;

        this.selectedPresetIndex = index;
        this.presetEditSlot = -1;
        this.presetItemCandidates.Clear();
        var preset = this.config.Presets[index];
        this.presetNameBuffer = preset.Name;
        this.presetGlassesInput = GetGlassesName(preset.GlassesID);
        this.presetHairstyleInput = GetHairstyleName(preset.HairstyleID);
    }

    private void AddPresetFromResults()
    {
        var preset = new LKami9GlamourPreset
        {
            Name = $"幻化 {DateTime.Now:yyyy-MM-dd HH:mm}",
        };

        foreach (var (slot, itemId, stain0, stain1) in this.BuildPresetItemsFromResults())
        {
            preset.Items.Add(new LKami9GlamourPresetItem
            {
                Slot = slot,
                ItemID = itemId,
                Stain0 = stain0,
                Stain1 = stain1,
            });
        }

        if (preset.Items.Count == 0)
        {
            this.presetStatus = "当前识别结果里没有可保存到幻化预设的部位。";
            return;
        }

        this.AddPreset(preset, "已把当前识别结果保存为预设。");
    }

    private List<(int Slot, uint ItemId, byte Stain0, byte Stain1)> BuildPresetItemsFromResults()
    {
        var items = new List<(int Slot, uint ItemId, byte Stain0, byte Stain1)>();
        var usedSlots = new HashSet<int>();
        var ringIndex = 0;

        foreach (var result in this.results)
        {
            if (result.ItemId == 0)
                continue;

            var slot = result.Slot switch
            {
                "主手" => 0,
                "副手" => 1,
                "头部" => 2,
                "上衣" => 3,
                "手部" => 4,
                "腿部" => 6,
                "脚部" => 7,
                "耳坠" => 8,
                "项链" => 9,
                "手镯" => 10,
                "戒指" => 11 + Math.Min(ringIndex++, 1),
                _ => -1,
            };

            if (slot < 0 || !usedSlots.Add(slot))
                continue;

            var (stain0, stain1) = DyePair(result);
            items.Add((slot, result.ItemId, stain0, stain1));
        }

        return items;
    }

    private unsafe void AddPresetFromCharacterInspect()
    {
        var preset = this.CaptureCharacterInspectPreset();
        if (preset == null)
        {
            this.presetStatus = "没有读到角色查看窗口的数据。请先在游戏里打开角色查看窗口。";
            return;
        }

        preset.Name = $"查看 {DateTime.Now:yyyy-MM-dd HH:mm}";
        this.AddPreset(preset, "已从角色查看窗口保存为预设。");
    }

    private unsafe LKami9GlamourPreset? CaptureCharacterInspectPreset()
    {
        if (!InventoryType.Examine.TryGetItems(
                item => item.ItemId != 0 && Array.IndexOf(InspectPresetSlots, item.Slot) >= 0,
                out var inspectItems))
            return null;

        var preset = new LKami9GlamourPreset();
        foreach (var item in inspectItems)
        {
            preset.Items.Add(new LKami9GlamourPresetItem
            {
                Slot = item.Slot,
                ItemID = item.GlamourId != 0 ? item.GlamourId : item.ItemId,
                Stain0 = item.Stains[0],
                Stain1 = item.Stains[1],
            });
        }

        var uiState = UIState.Instance();
        if (uiState != null)
        {
            preset.GlassesID = uiState->Inspect.GlassesIds[0];
            preset.HairstyleID = this.ResolveInspectHairstyleId(uiState->Inspect.CustomizeData);
        }

        return preset.Items.Count == 0 ? null : preset;
    }

    private unsafe uint ResolveInspectHairstyleId(CustomizeData customizeData)
    {
        foreach (var hairMakeType in DalamudServices.DataManager.GetExcelSheet<HairMakeType>())
        {
            if (hairMakeType.Tribe.RowId != customizeData.Tribe || hairMakeType.Gender != customizeData.Sex)
                continue;

            foreach (var rowId in hairMakeType.CharaMakeStruct[0].SubMenuParam)
            {
                var hairstyle = DalamudServices.DataManager
                    .GetExcelSheet<CharaMakeCustomize>()
                    .GetRow(rowId);
                if (hairstyle.RowId != 0 && hairstyle.FeatureID == customizeData.Hairstyle)
                    return hairstyle.RowId;
            }

            break;
        }

        return 0;
    }

    private void AddPreset(LKami9GlamourPreset preset, string message)
    {
        if (this.config.Presets.Count >= 100)
        {
            this.presetStatus = "预设数量已达上限（100 条）。请先删除不用的预设。";
            return;
        }

        this.config.Presets.Add(preset);
        this.SelectPreset(this.config.Presets.Count - 1);
        this.presetStatus = message;
    }

    private void DeleteSelectedPreset()
    {
        if (this.selectedPresetIndex < 0 || this.selectedPresetIndex >= this.config.Presets.Count)
            return;

        var removedIndex = this.selectedPresetIndex;
        if (this.activePresetIndex == removedIndex)
            this.StopTryOn(clear: true);

        this.config.Presets.RemoveAt(removedIndex);
        this.selectedPresetIndex = Math.Min(removedIndex, this.config.Presets.Count - 1);
        this.presetEditSlot = -1;
        this.presetStatus = "已删除预设。";
        this.NormalizePresetSelection();

        if (this.activePresetIndex > removedIndex)
            this.activePresetIndex--;
        else if (this.selectedPresetIndex < 0)
            this.activePresetIndex = -1;
    }

    private void CopyPresetText(LKami9GlamourPreset preset)
    {
        CopyTextToClipboard(this.BuildPresetText(preset));
        this.presetStatus = "已复制分享文本。";
    }

    private void ImportOmniPresets()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "OmniToolbox", "TreeHouse", "BetterGlamourManagement.json");
            if (!File.Exists(path))
            {
                this.presetStatus = "没有找到 Omni 幻化管理的配置文件。";
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.TryGetProperty("Config", out var configNode))
                root = configNode;
            if (!root.TryGetProperty("Presets", out var presetsNode) || presetsNode.ValueKind != JsonValueKind.Array)
            {
                this.presetStatus = "Omni 配置文件里没有 Presets。";
                return;
            }

            var imported = 0;
            foreach (var source in presetsNode.EnumerateArray())
            {
                var preset = ReadOmniPreset(source);
                if (preset == null || this.config.Presets.Any(existing => PresetFingerprint(existing) == PresetFingerprint(preset)))
                    continue;
                if (this.config.Presets.Count >= 100)
                    break;

                this.config.Presets.Add(preset);
                imported++;
            }

            if (imported > 0)
            {
                this.SelectPreset(this.config.Presets.Count - 1);
                this.presetStatus = $"已从 Omni 导入 {imported} 条预设。";
            }
            else
            {
                this.presetStatus = "没有发现新的 Omni 预设（可能已经全部导入过）。";
            }
        }
        catch (Exception ex)
        {
            this.presetStatus = "导入 Omni 预设失败：" + ex.Message;
            DalamudServices.PluginLog.Information(ex, "[幻化助手] 导入 Omni 幻化预设失败");
        }
    }

    private static LKami9GlamourPreset? ReadOmniPreset(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return null;

        var preset = new LKami9GlamourPreset
        {
            Name = source.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            GearsetIndex = source.TryGetProperty("GearsetIndex", out var gearset) ? gearset.GetInt32() : -1,
            GlassesID = source.TryGetProperty("GlassesID", out var glasses) ? glasses.GetUInt32() : 0,
            HairstyleID = source.TryGetProperty("HairstyleID", out var hairstyle) ? hairstyle.GetUInt32() : 0,
        };

        if (!source.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            return preset.Items.Count == 0 && preset.GlassesID == 0 && preset.HairstyleID == 0 ? null : preset;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var slot = item.TryGetProperty("Slot", out var slotNode) ? slotNode.GetInt32() : -1;
            if (Array.FindIndex(PresetSlots, x => x.Slot == slot) < 0)
                continue;

            preset.Items.Add(new LKami9GlamourPresetItem
            {
                Slot = slot,
                ItemID = item.TryGetProperty("ItemID", out var itemId) ? itemId.GetUInt32() : 0,
                Stain0 = item.TryGetProperty("Stain0", out var stain0) ? stain0.GetByte() : (byte)0,
                Stain1 = item.TryGetProperty("Stain1", out var stain1) ? stain1.GetByte() : (byte)0,
            });
        }

        return preset.Items.Count == 0 && preset.GlassesID == 0 && preset.HairstyleID == 0 ? null : preset;
    }

    private static string PresetFingerprint(LKami9GlamourPreset preset) =>
        preset.Name + "|" +
        string.Join(",", preset.Items
            .OrderBy(x => x.Slot)
            .Select(x => $"{x.Slot}:{x.ItemID}:{x.Stain0}:{x.Stain1}")) +
        $"|{preset.GlassesID}:{preset.HairstyleID}";

    private unsafe void DrawPresetGearsetSelector(LKami9GlamourPreset preset)
    {
        var current = preset.GearsetIndex >= 0 && TryGetGearsetName(preset.GearsetIndex, out var currentName)
            ? $"{preset.GearsetIndex + 1}. {currentName}"
            : "不绑定套装";
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo("绑定套装###presetGearset", current))
            return;

        if (ImGui.Selectable("不绑定套装", preset.GearsetIndex < 0))
            this.SetPresetGearset(preset, -1);

        for (var index = 0; index < 100; index++)
        {
            if (!TryGetGearsetName(index, out var name))
                continue;

            if (ImGui.Selectable($"{index + 1}. {name}###presetGearset{index}", preset.GearsetIndex == index))
                this.SetPresetGearset(preset, index);
        }

        ImGui.EndCombo();
    }

    private unsafe bool TryGetGearsetName(int index, out string name)
    {
        name = "";
        var module = RaptureGearsetModule.Instance();
        var gearset = module != null ? module->GetGearset(index) : null;
        if (gearset == null ||
            gearset->Id != index ||
            !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
            return false;

        name = gearset->NameString;
        return !string.IsNullOrWhiteSpace(name);
    }

    private void SetPresetGearset(LKami9GlamourPreset preset, int gearsetIndex)
    {
        if (gearsetIndex >= 0)
        {
            foreach (var other in this.config.Presets)
            {
                if (!ReferenceEquals(other, preset) && other.GearsetIndex == gearsetIndex)
                    other.GearsetIndex = -1;
            }
        }

        preset.GearsetIndex = gearsetIndex;
        this.lastGearsetIndex = int.MinValue;
        this.presetStatus = gearsetIndex >= 0
            ? $"已绑定到套装 {gearsetIndex + 1}。切换该套装时会自动试穿。"
            : "已取消套装绑定。";
    }

    private unsafe void TickGearsetBinding()
    {
        if (!this.config.AutoApplyBoundPreset ||
            !DService.Instance().ClientState.IsLoggedIn ||
            DService.Instance().ObjectTable.LocalPlayer == null)
            return;

        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            this.lastGearsetIndex = int.MinValue;
            return;
        }

        var current = module->CurrentGearsetIndex;
        if (current == this.lastGearsetIndex)
            return;

        this.lastGearsetIndex = current;
        var presetIndex = this.config.Presets.FindIndex(x => x.GearsetIndex == current);
        if (presetIndex >= 0)
        {
            this.StartTryOnPreset(presetIndex, true);
        }
        else if (this.activePresetFromGearset)
        {
            this.StopTryOn(clear: true);
            this.activePresetFromGearset = false;
        }
    }

    private void DrawPresetItemTable(LKami9GlamourPreset preset)
    {
        if (!ImGui.BeginTable("###presetItems", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("部位", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(58f));
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(44f));
        ImGui.TableSetupColumn("装备", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("染剂", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(44f));
        ImGui.TableHeadersRow();

        foreach (var (slot, slotName) in PresetSlots)
        {
            var item = preset.Items.Find(x => x.Slot == slot);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slotName);

            ImGui.TableNextColumn();
            if (item is { ItemID: not 0 })
                DrawGameIcon(GetPresetItemIcon(item.ItemID), OmniTheme.Scale(40f));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item is { ItemID: not 0 } ? GetPresetItemName(item.ItemID) : "无");

            ImGui.TableNextColumn();
            if (item is null || item.ItemID == 0)
                ImGui.TextDisabled("—");
            else
                ImGui.TextUnformatted(PresetDyeText(item));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"改###presetItemEdit{slot}"))
                this.PreparePresetItemEditor(preset, slot, item);
        }

        ImGui.EndTable();

        if (this.presetEditSlot >= 0)
            this.DrawPresetItemEditor(preset);
    }

    private void PreparePresetItemEditor(LKami9GlamourPreset preset, int slot, LKami9GlamourPresetItem? item)
    {
        this.presetEditSlot = slot;
        this.presetItemCandidates.Clear();
        this.presetItemInput = item is { ItemID: not 0 } ? GetPresetItemName(item.ItemID) : "";
        this.presetDye0Input = item is { Stain0: not 0 } ? GetDyeName(item.Stain0) : "";
        this.presetDye1Input = item is { Stain1: not 0 } ? GetDyeName(item.Stain1) : "";
    }

    private void DrawPresetItemEditor(LKami9GlamourPreset preset)
    {
        var slotName = PresetSlots.FirstOrDefault(x => x.Slot == this.presetEditSlot).Name;
        ImGui.Separator();
        ImGui.TextColored(this.WarnVec, $"编辑预设部位：{slotName}");
        ImGui.SameLine();
        if (ImGui.SmallButton("关闭"))
        {
            this.presetEditSlot = -1;
            return;
        }

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("装备名/ID###presetItemInput", ref this.presetItemInput, 128);
        ImGui.SameLine();
        if (ImGui.Button("确认装备"))
            this.ApplyPresetItemInput(preset);
        ImGui.SameLine();
        if (ImGui.Button("清空"))
        {
            preset.Items.RemoveAll(x => x.Slot == this.presetEditSlot);
            this.presetItemInput = "";
            this.presetDye0Input = "";
            this.presetDye1Input = "";
            this.presetItemCandidates.Clear();
            this.presetStatus = "已清空该部位。";
        }

        if (this.presetItemCandidates.Count > 0)
        {
            ImGui.TextDisabled("候选：");
            for (var i = 0; i < this.presetItemCandidates.Count; i++)
            {
                if (!ImGui.SmallButton($"{this.presetItemCandidates[i]}###presetItemCandidate{i}"))
                    continue;
                this.presetItemInput = this.presetItemCandidates[i];
                this.ApplyPresetItemInput(preset);
                break;
            }
        }

        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("染剂 1###presetDye0", ref this.presetDye0Input, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("染剂 2###presetDye1", ref this.presetDye1Input, 64);
        ImGui.SameLine();
        if (ImGui.Button("确认染剂"))
            this.ApplyPresetDyeInput(preset);
    }

    private void ApplyPresetItemInput(LKami9GlamourPreset preset)
    {
        var candidates = new List<string>();
        var resolved = this.ResolveItem(this.presetItemInput, candidates);
        if (resolved == null)
        {
            this.presetStatus = $"没有找到装备「{this.presetItemInput}」。";
            this.presetItemCandidates.Clear();
            return;
        }

        var item = resolved.Value;
        var entry = preset.Items.Find(x => x.Slot == this.presetEditSlot);
        if (entry == null)
        {
            entry = new LKami9GlamourPresetItem { Slot = this.presetEditSlot };
            preset.Items.Add(entry);
        }

        entry.ItemID = item.Id;
        this.presetItemInput = item.Name;
        this.presetItemCandidates.Clear();
        this.presetItemCandidates.AddRange(candidates);

        var expected = PresetSlotToResultSlot(this.presetEditSlot);
        this.presetStatus = item.Slot == expected
            ? "已更新预设部位。"
            : $"已填入，但游戏数据里它属于「{item.Slot}」，当前预设部位是「{expected}」。";
    }

    private void ApplyPresetDyeInput(LKami9GlamourPreset preset)
    {
        var entry = preset.Items.Find(x => x.Slot == this.presetEditSlot);
        if (entry == null || entry.ItemID == 0)
        {
            this.presetStatus = "请先给这个部位选择装备。";
            return;
        }

        var dye0 = this.ResolvePresetDye(this.presetDye0Input);
        var dye1 = this.ResolvePresetDye(this.presetDye1Input);
        if (!dye0.Ok || !dye1.Ok)
        {
            this.presetStatus = "有染剂名称无法识别。";
            return;
        }

        entry.Stain0 = dye0.Id;
        entry.Stain1 = dye1.Id;
        this.presetStatus = "已更新预设染剂。";
    }

    private (bool Ok, byte Id) ResolvePresetDye(string input)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            input.Trim() is "无" or "无染色" or "不可染色")
            return (true, (byte)0);

        var candidates = new List<string>();
        var dye = this.ResolveDye(input, candidates);
        return dye == null || dye.Value.Id > byte.MaxValue
            ? (false, (byte)0)
            : (true, (byte)dye.Value.Id);
    }

    private void DrawPresetSpecialRows(LKami9GlamourPreset preset)
    {
        ImGui.Spacing();
        DrawSectionTitle("发型 / 眼镜", "只影响 Omni 式本地试穿，不写入游戏幻化模板");

        if (!ImGui.BeginTable("###presetSpecial", 3,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("项目", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(56f));
        ImGui.TableSetupColumn("值", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(220f));
        ImGui.TableHeadersRow();

        this.DrawPresetSpecialRow(
            "发型",
            ref this.presetHairstyleInput,
            t => this.ResolveHairstyleInput(t),
            id =>
            {
                preset.HairstyleID = id;
                this.presetHairstyleInput = GetHairstyleName(id);
                this.presetStatus = "已更新预设发型。";
            },
            "留空或输入 0 = 不动发型");

        this.DrawPresetSpecialRow(
            "眼镜",
            ref this.presetGlassesInput,
            t => this.ResolveGlassesInput(t),
            id =>
            {
                preset.GlassesID = id;
                this.presetGlassesInput = GetGlassesName(id);
                this.presetStatus = "已更新预设眼镜。";
            },
            "留空或输入 0 = 不戴");

        ImGui.EndTable();
    }

    private void DrawPresetSpecialRow(
        string label,
        ref string input,
        Func<string, uint?> resolve,
        Action<uint> apply,
        string hint)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText($"###preset{label}", ref input, 96);

        ImGui.TableNextColumn();
        var buttonWidth = OmniTheme.Scale(96f);
        if (ImGui.Button($"确认{label}###confirm{label}", new System.Numerics.Vector2(buttonWidth, 0)))
        {
            var id = resolve(input);
            if (id == null)
                this.presetStatus = label switch
                {
                    "发型" => "没有找到这个发型。",
                    _ => "没有找到这个眼镜/面部配饰。",
                };
            else
                apply(id.Value);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(hint);
    }

    private static string PresetSlotToResultSlot(int slot) => slot switch
    {
        0 => "主手",
        1 => "副手",
        2 => "头部",
        3 => "上衣",
        4 => "手部",
        6 => "腿部",
        7 => "脚部",
        8 => "耳坠",
        9 => "项链",
        10 => "手镯",
        11 or 12 => "戒指",
        _ => "",
    };

    private string BuildPresetText(LKami9GlamourPreset preset)
    {
        var sb = new StringBuilder();
        sb.AppendLine("发型：" + GetHairstyleName(preset.HairstyleID));
        sb.AppendLine("武器：" + FormatPresetWeapon(preset));
        AppendPresetPair(sb, preset, 2, "头部", 8, "耳饰");
        AppendPresetPair(sb, preset, 3, "身体", 9, "项链");
        AppendPresetPair(sb, preset, 4, "手部", 10, "手镯");
        AppendPresetPair(sb, preset, 6, "腿部", 11, "右戒指");
        AppendPresetPair(sb, preset, 7, "脚部", 12, "左戒指");
        sb.AppendLine("眼镜：" + GetGlassesName(preset.GlassesID));
        return sb.ToString().TrimEnd();
    }

    private string FormatPresetWeapon(LKami9GlamourPreset preset)
    {
        var main = FormatPresetItem(preset.Items.Find(x => x.Slot == 0));
        var off = preset.Items.Find(x => x.Slot == 1);
        return off is { ItemID: not 0 }
            ? $"主手：{main}；副手：{FormatPresetItem(off)}"
            : main;
    }

    private void AppendPresetPair(
        StringBuilder builder,
        LKami9GlamourPreset preset,
        int leftSlot,
        string leftName,
        int rightSlot,
        string rightName)
    {
        builder.AppendLine(
            $"{leftName}：{FormatPresetItem(preset.Items.Find(x => x.Slot == leftSlot))}    " +
            $"{rightName}：{FormatPresetItem(preset.Items.Find(x => x.Slot == rightSlot))}");
    }

    private string FormatPresetItem(LKami9GlamourPresetItem? item)
    {
        if (item is null || item.ItemID == 0)
            return "无";

        var name = GetPresetItemName(item.ItemID);
        if (item.Stain0 != 0)
            name += " " + GetDyeName(item.Stain0);
        if (item.Stain1 != 0)
            name += " " + GetDyeName(item.Stain1);
        return name;
    }

    private string PresetDyeText(LKami9GlamourPresetItem item)
    {
        var parts = new List<string>();
        if (item.Stain0 != 0)
            parts.Add(GetDyeName(item.Stain0));
        if (item.Stain1 != 0)
            parts.Add(GetDyeName(item.Stain1));
        return parts.Count == 0 ? "无染色" : string.Join(" / ", parts);
    }

    private static string DisplayPresetName(LKami9GlamourPreset preset) =>
        string.IsNullOrWhiteSpace(preset.Name) ? "未命名预设" : preset.Name;

    private string GetPresetListLabel(LKami9GlamourPreset preset, int index)
    {
        var active = this.activePresetIndex == index;
        if (preset.CachedListLabel.Length == 0 ||
            !ReferenceEquals(preset.CachedLabelName, preset.Name) ||
            preset.CachedLabelGearset != preset.GearsetIndex ||
            preset.CachedLabelActive != active)
        {
            var gearset = preset.GearsetIndex >= 0 ? $" [套装 {preset.GearsetIndex + 1}]" : "";
            var activeText = active ? " [试穿中]" : "";
            preset.CachedListLabel = string.Concat($"{index + 1}. ", DisplayPresetName(preset), gearset, activeText);
            preset.CachedLabelName = preset.Name;
            preset.CachedLabelGearset = preset.GearsetIndex;
            preset.CachedLabelActive = active;
        }

        return preset.CachedListLabel;
    }

    private string GetPresetItemName(uint itemId)
    {
        if (itemId == 0)
            return "无";
        if (this.presetItemNameCache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            var name = DalamudServices.DataManager.GetExcelSheet<Item>().GetRow(itemId).Name.ToString();
            name = string.IsNullOrWhiteSpace(name) ? $"道具 #{itemId}" : name;
            this.presetItemNameCache[itemId] = name;
            return name;
        }
        catch
        {
            var fallback = $"道具 #{itemId}";
            this.presetItemNameCache[itemId] = fallback;
            return fallback;
        }
    }

    private uint GetPresetItemIcon(uint itemId)
    {
        if (itemId == 0)
            return 0;
        if (this.presetItemIconCache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            var icon = DalamudServices.DataManager.GetExcelSheet<Item>().GetRow(itemId).Icon;
            this.presetItemIconCache[itemId] = icon;
            return icon;
        }
        catch
        {
            this.presetItemIconCache[itemId] = 0;
            return 0;
        }
    }

    private string GetDyeName(byte stainId)
    {
        if (stainId == 0)
            return "无染色";

        this.BuildIndexIfNeeded();
        return this.stainNameById != null && this.stainNameById.TryGetValue(stainId, out var name)
            ? name
            : $"染剂 #{stainId}";
    }

    private void EnsureGlassesIndex()
    {
        if (this.glassesByName != null && this.glassesNameById != null)
            return;

        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<uint, string>();
        foreach (var row in DalamudServices.DataManager.GetExcelSheet<Glasses>())
        {
            if (row.RowId == 0)
                continue;

            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            byName.TryAdd(name, row.RowId);
            byId.TryAdd(row.RowId, name);
        }

        this.glassesByName = byName;
        this.glassesNameById = byId;
    }

    private string GetGlassesName(uint glassesId)
    {
        if (glassesId == 0)
            return "无";

        this.EnsureGlassesIndex();
        return this.glassesNameById!.TryGetValue(glassesId, out var name)
            ? name
            : $"眼镜 #{glassesId}";
    }

    private uint? ResolveGlassesInput(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || text == "无")
            return 0;
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        this.EnsureGlassesIndex();
        if (this.glassesByName!.TryGetValue(text, out var exact))
            return exact;

        var contains = this.glassesByName.Keys
            .Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => Math.Abs(x.Length - text.Length))
            .Take(1)
            .FirstOrDefault();
        return contains != null ? this.glassesByName[contains] : null;
    }

    private void EnsureHairstyleIndex()
    {
        if (this.hairstyleByName != null && this.hairstyleNameById != null)
            return;

        var validRows = new HashSet<uint>();
        foreach (var hairMakeType in DalamudServices.DataManager.GetExcelSheet<HairMakeType>())
        {
            foreach (var rowId in hairMakeType.CharaMakeStruct[0].SubMenuParam)
            {
                if (rowId != 0)
                    validRows.Add(rowId);
            }
        }

        var unlockNames = new Dictionary<uint, string>();
        foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
        {
            try
            {
                if (!item.ItemAction.IsValid)
                    continue;

                var action = item.ItemAction.Value;
                if (action.Action.RowId == 2633 && action.Data[0] > 0)
                    unlockNames.TryAdd((uint)action.Data[0], item.Name.ToString());
            }
            catch { }
        }

        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<uint, string>();
        foreach (var row in DalamudServices.DataManager.GetExcelSheet<CharaMakeCustomize>())
        {
            if (!validRows.Contains(row.RowId) || row.FeatureID > byte.MaxValue)
                continue;

            var name = "";
            try
            {
                var hint = row.HintItem.ValueNullable;
                if (hint is { } item)
                    name = item.Name.ToString();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(name) && row.UnlockLink != 0)
                unlockNames.TryGetValue(row.UnlockLink, out name);
            if (string.IsNullOrWhiteSpace(name))
                name = $"发型 {row.FeatureID}";

            byId.TryAdd(row.RowId, name);
            byName.TryAdd(name, row.RowId);
            byName.TryAdd($"发型 {row.FeatureID}", row.RowId);
        }

        this.hairstyleByName = byName;
        this.hairstyleNameById = byId;
    }

    private string GetHairstyleName(uint hairstyleId)
    {
        if (hairstyleId == 0)
            return "无";

        try
        {
            var row = DalamudServices.DataManager.GetExcelSheet<CharaMakeCustomize>().GetRow(hairstyleId);
            var hint = row.HintItem.ValueNullable;
            if (hint is { } item && !string.IsNullOrWhiteSpace(item.Name.ToString()))
                return item.Name.ToString();
            if (row.RowId != 0)
                return $"发型 {row.FeatureID}";
        }
        catch { }

        return $"发型 #{hairstyleId}";
    }

    private uint? ResolveHairstyleInput(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || text == "无")
            return 0;

        this.EnsureHairstyleIndex();
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            if (this.hairstyleNameById!.ContainsKey(numeric))
                return numeric;

            var byFeature = this.hairstyleByName!.TryGetValue($"发型 {numeric}", out var featureId)
                ? featureId
                : (uint?)null;
            return byFeature;
        }

        if (this.hairstyleByName!.TryGetValue(text, out var exact))
            return exact;

        var contains = this.hairstyleByName.Keys
            .Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => Math.Abs(x.Length - text.Length))
            .Take(1)
            .FirstOrDefault();
        return contains != null ? this.hairstyleByName[contains] : null;
    }

    // ══════════════════════════════════════════════════════════
    //  内嵌 OCR 脚本（纯 ASCII，避免编码问题）
    // ══════════════════════════════════════════════════════════

    private const string ScriptVersion = "v3";

    private void EnsureOcrScript()
    {
        var dir = WorkDir();
        Directory.CreateDirectory(dir);

        var ver = Path.Combine(dir, "version.txt");
        var cur = File.Exists(ver) ? File.ReadAllText(ver).Trim() : "";
        if (cur == ScriptVersion && File.Exists(ScriptPath("ocr.ps1")) && File.Exists(ScriptPath("clip.ps1")))
            return;

        // ASCII only -> write with BOM so PowerShell 5.1 parses it reliably
        var enc = new UTF8Encoding(true);
        File.WriteAllText(ScriptPath("ocr.ps1"), OcrScript, enc);
        File.WriteAllText(ScriptPath("clip.ps1"), ClipScript, enc);
        File.WriteAllText(ver, ScriptVersion, new UTF8Encoding(false));
    }

    private const string ClipScript = """
$ErrorActionPreference = 'Stop'
$out = $args[0]
$log = Join-Path $env:TEMP 'LKami9GlamourHelper\last_error.txt'
try {
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing
  $img = [System.Windows.Forms.Clipboard]::GetImage()
  if ($null -eq $img) { Write-Output 'NOIMAGE'; exit 2 }
  $dir = Split-Path $out -Parent
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  $img.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $img.Dispose()
  Write-Output 'OK'
  exit 0
} catch {
  [System.IO.File]::WriteAllText($log, ($_.Exception.ToString() + "`n" + $_.ScriptStackTrace), (New-Object System.Text.UTF8Encoding($false)))
  Write-Output ('ERR ' + $_.Exception.Message)
  exit 1
}
""";

    private const string OcrScript = """
$ErrorActionPreference = 'Stop'
$img = $args[0]
$out = $args[1]
$log = Join-Path $env:TEMP 'LKami9GlamourHelper\last_error.txt'
try {
  if (-not $img) { Write-Output 'NOIMGARG'; exit 4 }
  if (-not (Test-Path -LiteralPath $img)) { Write-Output ('NOFILE ' + $img); exit 5 }
  if (-not $out) { Write-Output 'NOOUTARG'; exit 6 }

  $types = @(
    '[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]',
    '[Windows.Media.Ocr.OcrResult, Windows.Media.Ocr, ContentType=WindowsRuntime]',
    '[Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]',
    '[Windows.Storage.FileAccessMode, Windows.Storage, ContentType=WindowsRuntime]',
    '[Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]',
    '[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]',
    '[Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]'
  )
  foreach ($t in $types) { try { Invoke-Expression $t | Out-Null } catch {} }
  Add-Type -AssemblyName System.Runtime.WindowsRuntime

  $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  if ($null -eq $asTask) { Write-Output 'NOASTASK'; exit 7 }

  function Await($op, $t) {
    if ($null -eq $op) { throw ('Async operation is null for type ' + $t) }
    $m = $asTask.MakeGenericMethod($t)
    $task = $m.Invoke($null, @($op))
    $task.Wait(30000) | Out-Null
    return $task.Result
  }

  $engine = $null
  foreach ($l in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) {
    if ($l.LanguageTag -like 'zh*') { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l); break }
  }
  if (-not $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
  if (-not $engine) { Write-Output 'NOLANG'; exit 3 }

  $file    = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($img)) ([Windows.Storage.StorageFile])
  $stream  = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
  $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
  $bitmap  = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
  $result  = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])

  $lines = @()
  foreach ($line in $result.Lines) {
    $words = @()
    $lx = [double]::MaxValue; $ly = [double]::MaxValue; $rx = 0.0; $by = 0.0
    foreach ($w in $line.Words) {
      $r = $w.BoundingRect
      $words += [pscustomobject]@{
        text = $w.Text
        x = [math]::Round($r.X, 1); y = [math]::Round($r.Y, 1)
        w = [math]::Round($r.Width, 1); h = [math]::Round($r.Height, 1) }
      if ($r.X -lt $lx) { $lx = $r.X }
      if ($r.Y -lt $ly) { $ly = $r.Y }
      if (($r.X + $r.Width) -gt $rx) { $rx = $r.X + $r.Width }
      if (($r.Y + $r.Height) -gt $by) { $by = $r.Y + $r.Height }
    }
    $lines += [pscustomobject]@{
      text = $line.Text
      x = [math]::Round($lx, 1); y = [math]::Round($ly, 1)
      w = [math]::Round($rx - $lx, 1); h = [math]::Round($by - $ly, 1)
      words = $words }
  }
  $payload = [pscustomobject]@{
    image  = (Split-Path $img -Leaf)
    width  = $decoder.PixelWidth
    height = $decoder.PixelHeight
    lang   = $engine.RecognizerLanguage.LanguageTag
    lines  = $lines }
  [System.IO.File]::WriteAllText($out, ($payload | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
  Write-Output ('OK ' + $lines.Count)
  exit 0
} catch {
  [System.IO.File]::WriteAllText($log, ($_.Exception.ToString() + "`n--- script stack ---`n" + $_.ScriptStackTrace + "`n--- args ---`nimg=$img`nout=$out"), (New-Object System.Text.UTF8Encoding($false)))
  Write-Output ('ERR ' + $_.Exception.Message)
  exit 1
}
""";
}

/// <summary>一行识别结果</summary>
public sealed class SlotResult
{
    public int Index { get; set; }
    public string Slot { get; set; } = "";
    public uint ItemId { get; set; }
    public uint IconId { get; set; }
    public string Name { get; set; } = "";
    public int MatchScore { get; set; }

    /// <summary>低于 100 分（模糊匹配）时置位 —— UI 上打问号提示玩家修改。</summary>
    public bool NeedsReview { get; set; }

    public List<DyeResult> Dyes { get; set; } = [];
    public int Owned { get; set; }

    /// <summary>持有位置说明（背包/兵装库/装备栏/投影台/收藏柜…），用于提示框。</summary>
    public string OwnedWhere { get; set; } = "";

    /// <summary>不可交易（市场板买不到、只能自己搞）。</summary>
    public bool Untradable { get; set; }

    public string Source { get; set; } = "";
}

public sealed class DyeResult
{
    public DyeResult(string name, uint id, bool isEmpty, int score, double x = 0)
    {
        this.Name = name;
        this.Id = id;
        this.IsEmpty = isEmpty;
        this.Score = score;
        this.NeedsReview = score < 100 && !isEmpty;
        this.X = x;
    }

    public string Name { get; set; }
    public uint Id { get; set; }
    public bool IsEmpty { get; }
    public int Score { get; }

    /// <summary>图上坐标（用于排「槽1 / 槽2」的顺序，即左右顺序）。</summary>
    public double X { get; }

    /// <summary>低于 100 分（模糊匹配）时置位 —— UI 上打问号。</summary>
    public bool NeedsReview { get; }
}

/// <summary>没能识别成装备的文本片段（供玩家手动指派）</summary>
/// <remarks>这些必须是**字段**而不是属性 —— ImGui.InputText 需要 ref 变量，属性不能传 ref。</remarks>
public sealed class UnknownFragment
{
    public string Text = "";
    public string EditBuffer = "";
    public string Hint = "";
}

[Serializable]
public sealed class LKami9GlamourPreset
{
    public string Name { get; set; } = "";
    public int GearsetIndex { get; set; } = -1;
    public uint GlassesID { get; set; }
    public uint HairstyleID { get; set; }
    public List<LKami9GlamourPresetItem> Items { get; set; } = [];

    internal string CachedListLabel = "";
    internal string? CachedLabelName;
    internal int CachedLabelGearset = int.MinValue;
    internal bool CachedLabelActive;
}

[Serializable]
public sealed class LKami9GlamourPresetItem
{
    public int Slot { get; set; }
    public uint ItemID { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}

public sealed class LKami9GlamourHelperConfig
{
    public string LastImagePath { get; set; } = "";
    public int OcrTimeoutMs { get; set; } = 30000;
    public bool ShowSources { get; set; } = true;

    /// <summary>把没识别上的片段也列出来供手动指派（默认开）。</summary>
    public bool ShowUnknowns { get; set; } = true;

    /// <summary>Omni「更好的幻化管理」等价预设。</summary>
    public List<LKami9GlamourPreset> Presets { get; set; } = [];

    /// <summary>切换套装时自动试穿绑定的预设。</summary>
    public bool AutoApplyBoundPreset { get; set; } = true;

    /// <summary>在游戏角色查看窗口旁显示快捷操作栏。</summary>
    public bool ShowInspectQuickActions { get; set; } = true;

    /// <summary>角色快捷栏解除跟随（在标题栏上拖动即可解除），之后可自由拖拽移动。</summary>
    public bool QuickActionsFree { get; set; }

    /// <summary>自由拖拽时的窗口位置，跨重启保留。</summary>
    public System.Numerics.Vector2 QuickActionsPos { get; set; }
}
