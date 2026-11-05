using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace LKami9.SpotDiff;

/// <summary>
/// LKami9 图标找茬（在线模块版 · 独立窗口）
///
/// 两种玩法：
///   经典找茬 —— 左右两幅图，右图改掉几处，找齐全部差异过关；
///   找不同   —— 整屏同一个图标，只有一格不同，找到就进下一关。
///
/// 素材全部来自游戏本体的图标贴图（TextureProvider.GetFromGameIcon / 图标 tex），
/// 可按挑战模式筛选来源（装备 / 食物 / 鱼类 / 技能 / 生产 / 采集）。
/// 不依赖任何外部图片、不写图片文件、不联网。
///
/// 差异类型：换近似图标 / 水平镜像 / 旋转 180°，翻转类会先做像素对称校验，
/// 保证每个差异都真的看得出来。
/// </summary>
[ModuleApiVersion(1)]
public sealed class LKami9SpotDiff : OmniModuleBase
{
    private const string WindowId   = "###LKami9SpotDiffMain";
    private const string CollapseId = "##LKami9SpotDiffCollapse";
    private const string CloseId    = "##LKami9SpotDiffClose";
    private const string BoardId    = "###LKami9SpotDiffBoard";

    private Settings config = new();

    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Interface,
        Author   = "LKami9",
        Commands = [],
    };

    public override bool HasSettings => true;

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"]       = "FF14找茬",
            ["Description"] = "用游戏内图标玩两种玩法：左右两幅图找差异，或整屏相同图标里找出唯一不同的那个。素材全部取自游戏本体，纯本地、不联网。",

            ["WindowTitle"]  = "FF14找茬",
            ["SettingsHint"] = "主界面在独立窗口中（点下方「打开窗口」按钮）。素材取自游戏内图标（物品/技能），不依赖外部图片。",

            ["GameMode"]         = "玩法",
            ["GameModeClassic"]  = "经典找茬",
            ["GameModeOdd"]      = "找不同",

            ["OpenWindow"]  = "打开窗口",
            ["CloseWindow"] = "关闭窗口",
            ["WindowState"]  = "窗口状态：{0}",
            ["StateOpen"]    = "已打开",
            ["StateClosed"]  = "已关闭",

            ["StartButton"]  = "开始游戏",
            ["HistoryButton"] = "历史记录",
            ["HistoryEmpty"]  = "还没有记录。关掉窗口就会记一局。",
            ["HistoryRow"]    = "{0}　{1} 分　第 {2} 关　点错 {3} 次",
            ["AgainButton"]  = "再来一局",
            ["HintButton"]   = "提示（-10 秒）",
            ["PauseButton"]  = "暂停",
            ["ResumeButton"] = "继续",
            ["PhasePaused"]  = "已暂停 · 计时停止，点击「继续」回到游戏",

            ["PhaseIdle"]    = "点「开始游戏」开一局。左右两幅图有 {0} 处不同。",
            ["PhaseIdleOdd"] = "点「开始游戏」开一局。整屏都是同一个图标，找出唯一不同的那个，找到就进下一关。",
            ["PhasePlaying"] = "第 {0} 关 · 找齐剩下 {1} 处差异",
            ["PhaseCleared"] = "过关！本关得分 {0}，进入第 {1} 关。",
            ["PhaseFailed"]  = "时间到。本局 {0} 分，最高 {1} 分。",
            ["PhaseEnded"]   = "本局已结束。得分 {0}，最高 {1} 分。",
            ["PoolFailed"]   = "读取游戏图标失败，无法开始。",

            ["StatLine"]   = "第 {0} 关　得分 {1}　最高 {2}　点错 {3} 次",
            ["TimeLeft"]   = "剩余 {0:mm\\:ss}",
            ["TimeUrgent"] = "剩余 {0:mm\\:ss}（快没时间了）",
            ["TimeInfinite"] = "不限时",

            ["GridSize"]   = "每边格子数",
            ["DiffCount"]  = "每关差异数",
            ["Minutes"]    = "时间（分钟）",
            ["InfiniteTime"] = "无限时间",
            ["IconSource"]        = "挑战模式",
            ["SrcEquip"]          = "装备大师",
            ["SrcFood"]           = "食神",
            ["SrcFish"]           = "海王",
            ["SrcOther"]          = "简单模式",
            ["SrcPve"]            = "PVE高手",
            ["SrcPvp"]            = "PVP高手",
            ["SrcCraft"]          = "生产高手",
            ["SrcGather"]         = "采集高手",
            ["SrcSummaryAll"]     = "全部",
            ["SrcSummaryItems"]   = "物品",
            ["SrcSummarySkills"]  = "技能",
            ["SrcSummaryCount"]   = "{0} 类",
            ["SrcSummaryNone"]    = "未选",
            ["SrcSelectAll"]      = "全选",
            ["SrcSelectNone"]     = "全不选",
            ["SrcEmpty"]          = "至少要选一类图标来源。",
            ["ResetButton"] = "重置记录",
            ["ResetDone"]   = "最高分已清零。",
            ["ShowBoard"]   = "显示格子底纹",
            ["SoundToggle"]     = "音效反馈",
            ["SoundChatToggle"] = "用聊天提示音通道（编号 1~16）",
            ["SoundPreview"]    = "试听",
            ["SeHitLabel"]      = "命中音效",
            ["SeMissLabel"]     = "点错音效",
            ["SeClearLabel"]    = "过关音效",
            ["SeHintLabel"]     = "提示音效",
            ["SeIdHint"]        = "编号：聊天通道 1~16；通用通道常用 23（开窗）/ 24（关窗）",

            ["BoardHint"]      = "左键点差异处。点错扣 5 秒。",
            ["FeedbackGain"]    = "+{0}",
            ["FeedbackPenalty"] = "-{0} 秒",
            ["FeedbackPenaltyScore"] = "-{0} 分",
        });

    private sealed class Settings
    {
        public bool WindowOpen { get; set; }
        public int  GridSize   { get; set; } = 6;
        public int  DiffCount  { get; set; } = 3;
        public int  Minutes    { get; set; } = 2;
        public int  Seconds    { get; set; }   // 旧字段，仅用于迁移到 Minutes
        public bool InfiniteTime { get; set; }   // true = 不限时，不会因为时间到而结束
        public bool ShowBoard  { get; set; } = true;
        public bool Sound      { get; set; } = true;
        public int  IconSourceMask { get; set; } = SrcItemsAll;   // 位标志，见 Src* 常量
        public int  IconSource { get; set; }                      // 旧字段（0物品/1技能/2混合），仅用于迁移
        public int  GameMode   { get; set; }   // 0 = 经典找茬（左右对比），1 = 找唯一不同（单图）
        public bool SoundChat  { get; set; } = true;   // true = 聊天提示音通道（1~16），false = 通用 UI 音效
        public int  SeHit      { get; set; } = 1;
        public int  SeMiss     { get; set; } = 24;
        public int  SeClear    { get; set; } = 3;
        public int  SeHint     { get; set; } = 9;
        public int  ConfigVersion { get; set; }
    }

    private enum Phase
    {
        Idle,
        Playing,
        Cleared,
        Failed,
        Ended,
    }

    private sealed class Cell
    {
        public uint IconId;
        public uint Category;
        public int  Variant;   // 0 = 正常，1 = 水平镜像，2 = 旋转 180°
    }

    private sealed class Level
    {
        public int    Grid;
        public bool   Odd;        // true = 找唯一不同（单图），false = 经典左右对比
        public Cell[] Base  = [];
        public Cell[] Right = [];
        public bool[] IsDiff = [];
        public bool[] Found  = [];
        public int    DiffTotal;
    }

    private const int ScorePerHit          = 120;
    private const int WrongPenaltySeconds  = 5;
    private const int WrongPenaltyScore    = 20;

    // 图标来源（位标志）：装备 / 食物 / 鱼类 / 其他物品 / PVE技能 / PVP技能 / 生产技能 / 采集技能
    private const int SrcEquip  = 1 << 0;
    private const int SrcFood   = 1 << 1;
    private const int SrcFish   = 1 << 2;
    private const int SrcOther  = 1 << 3;
    private const int SrcPve    = 1 << 4;
    private const int SrcPvp    = 1 << 5;
    private const int SrcCraft  = 1 << 6;
    private const int SrcGather = 1 << 7;

    private const int SrcItemsAll  = SrcEquip | SrcFood | SrcFish | SrcOther;
    private const int SrcSkillsAll = SrcPve | SrcPvp | SrcCraft | SrcGather;
    private const int SrcAll       = SrcItemsAll | SrcSkillsAll;

    private const int ModeClassic = 0;
    private const int ModeOdd     = 1;

    // 每类图标给一个独立的分类号段：「换图标」差异会优先跨分类取，视觉差异更明显
    private const uint CatEquipBase  = 1000;
    private const uint CatFoodBase   = 2000;
    private const uint CatFishBase   = 3000;
    private const uint CatOtherBase  = 4000;
    private const uint CatPveBase    = 10000;
    private const uint CatPvpBase    = 15000;
    private const uint CatCraftBase  = 20000;
    private const uint CatGatherBase = 25000;

    // 采集三职 + 大地使者 的 ClassJobCategory 行号
    private static readonly uint[] GatherJobCategories = [17, 18, 19, 32, 202];

    // 找「近似图标」时的 ID 距离档位，由近到远逐级放宽
    private static readonly long[] SimilarRadii = [24, 96, 512, long.MaxValue];

    // 建池缓存的规则版本：改动分桶逻辑时 +1，让旧缓存失效
    private const int PoolCacheVersion = 1;

    // 镜像/旋转后的像素平均差低于这个值，说明图标基本对称，翻转后看不出区别，不能拿来做差异
    private const double SymmetricThreshold = 6.0;

    private static readonly Vector4 FoundColor = new(0.32f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 WrongColor = new(1.00f, 0.38f, 0.38f, 1f);

    private sealed class Feedback
    {
        public int      Index;
        public bool     Correct;
        public DateTime Until;
        public string   Text = string.Empty;
    }

    private readonly List<Feedback> feedbacks = [];
    private string statsCache = string.Empty;
    private int    statsKey   = -1;
    private readonly Random rng = new();
    private readonly List<(uint IconId, uint Category)> iconPool = [];
    private readonly Dictionary<uint, ISharedImmediateTexture> iconCache = [];
    private readonly HashSet<uint> brokenIcons = [];                        // 取不到贴图的 ID，不再重复尝试
    private readonly Dictionary<uint, List<uint>> iconByCategory = [];      // 分类 → 图标 ID，找「近似图标」用
    private readonly Dictionary<uint, byte>       symCache       = [];      // 对称性缓存：bit0 镜像看得出，bit1 旋转180看得出
    private readonly Dictionary<uint, uint>       artHashCache   = [];      // 图标像素指纹，用于排除「换 ID 不换图」

    private Level? level;
    private Phase  phase = Phase.Idle;
    private int    levelNo = 1;
    private int    score;
    private int    best;
    private int    wrongClicks;
    private int    lastGain;
    private DateTime deadline = DateTime.MinValue;
    private DateTime nextLevelAt = DateTime.MinValue;   // 找唯一不同：找到后延迟切下一关
    private double remainSeconds;
    private bool   paused;
    private double pausedRemain;
    private int    hintIndex = -1;
    private DateTime hintUntil = DateTime.MinValue;

    private bool focusRequested;
    private bool wasOpen;
    private bool windowDrawLogged;
    private bool collapsed;
    private bool resizePending;
    private Vector2 expandedSize = new(900f, 570f);

    private readonly List<RunRecord> history = [];
    private bool showHistory;

    private string HistoryFilePath =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LKami9SpotDiff.history.json");

    private string BestFilePath =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LKami9SpotDiff.best.json");

    #region 生命周期

    protected override void OnStart(FeatureLifetime activation)
    {
        var uiBuilder = DalamudServices.PluginInterface.UiBuilder;
        var framework = DalamudServices.Framework;

        activation.OnStopping(() => uiBuilder.Draw -= this.DrawWindow);
        uiBuilder.Draw += this.DrawWindow;

        activation.OnStopping(() => framework.Update -= this.OnFrameworkUpdate);
        framework.Update += this.OnFrameworkUpdate;

        this.MigrateConfig();
        this.LoadBest();
        this.LoadHistory();

        DalamudServices.PluginLog.Information(
            $"[LKami9SpotDiff] enabled: windowOpen={this.config.WindowOpen}, best={this.best}");
    }

    private void MigrateConfig()
    {
        if (this.config.ConfigVersion >= 6) return;

        if (this.config.ConfigVersion < 1)
        {
            this.config.GridSize  = Math.Clamp(this.config.GridSize, 4, 10);
            this.config.DiffCount = Math.Clamp(this.config.DiffCount, 2, 8);
            this.config.Seconds   = Math.Clamp(this.config.Seconds, 10, 3600);
        }

        // v5：时间单位从「秒」改成「分钟」，旧配置按分钟四舍五入折算一次
        if (this.config.ConfigVersion < 5 && this.config.Seconds > 0)
            this.config.Minutes = Math.Clamp((this.config.Seconds + 30) / 60, 1, 120);

        // v6：图标来源从「物品/技能/混合」三选一改成八类位标志
        if (this.config.ConfigVersion < 6)
        {
            this.config.IconSourceMask = this.config.IconSource switch
            {
                0 => SrcItemsAll,
                1 => SrcSkillsAll,
                _ => SrcAll,
            };
        }

        this.config.Minutes = Math.Clamp(this.config.Minutes, 1, 120);

        // v2/v3：窗口此前没有任何入口（无命令、默认关闭），升级后主动打开一次并置顶。
        // 只在真正从老版本升上来时做，别在每次跨版本迁移时把玩家关上的窗口又弹出来
        if (this.config.ConfigVersion < 2 && !this.config.WindowOpen)
        {
            this.config.WindowOpen = true;
            this.resizePending     = true;
            this.focusRequested    = true;
        }

        if (this.config.ConfigVersion < 4)
        {
            if (this.config.SeHit   <= 0) this.config.SeHit   = 1;
            if (this.config.SeMiss  <= 0) this.config.SeMiss  = 24;
            if (this.config.SeClear <= 0) this.config.SeClear = 3;
            if (this.config.SeHint  <= 0) this.config.SeHint  = 9;
        }

        this.config.ConfigVersion = 6;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.phase != Phase.Playing || this.paused) return;

        // 找唯一不同：找到后延迟 0.42 秒切下一关（这段时间里棋盘还停在旧图上，看得见对勾）
        if (this.nextLevelAt != DateTime.MinValue && DateTime.UtcNow >= this.nextLevelAt)
        {
            this.nextLevelAt = DateTime.MinValue;
            this.levelNo++;
            this.BeginLevel();
            return;
        }

        var left = (this.deadline - DateTime.UtcNow).TotalSeconds;
        this.remainSeconds = left > 0 ? left : 0;

        // 不限时：永远不判负，只统计分数
        if (this.config.InfiniteTime) return;

        if (this.remainSeconds <= 0)
        {
            this.phase = Phase.Failed;
            this.level = null;
            this.nextLevelAt = DateTime.MinValue;
            this.RecordRun();
        }
    }

    public override bool ResetSettings()
    {
        var before = Snapshot();

        this.config.WindowOpen = false;
        this.config.GridSize   = 6;
        this.config.DiffCount  = 3;
        this.config.Seconds    = 0;
        this.config.Minutes    = 2;
        this.config.InfiniteTime = false;
        this.config.ShowBoard  = true;
        this.config.GameMode   = ModeClassic;
        this.config.IconSourceMask = SrcItemsAll;
        this.collapsed         = false;
        this.resizePending     = true;

        return before != Snapshot();

        (bool, int, int, int, int, bool, bool, int, int, bool, bool) Snapshot() => (
            this.config.WindowOpen,
            this.config.GridSize,
            this.config.DiffCount,
            this.config.Seconds,
            this.config.Minutes,
            this.config.InfiniteTime,
            this.config.ShowBoard,
            this.config.GameMode,
            this.config.IconSourceMask,
            this.collapsed,
            this.resizePending);
    }

    #endregion

    #region 设置页

    public override bool DrawSettings()
    {
        var changed = false;
        var scale   = OmniTheme.Scale(1f);

        ImGui.TextUnformatted(OmniLoc.Get(this, "WindowTitle"));
        ImGui.TextDisabled(OmniLoc.Get(this, "SettingsHint"));
        ImGui.Spacing();

        if (OmniControls.SmallButton(
                OmniLoc.Get(this, this.config.WindowOpen ? "CloseWindow" : "OpenWindow"), false))
        {
            this.config.WindowOpen = !this.config.WindowOpen;
            if (this.config.WindowOpen)
            {
                this.resizePending  = true;
                this.focusRequested = true;
            }
            changed = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "HistoryButton"), this.showHistory))
            this.showHistory = !this.showHistory;

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "ResetButton"), false))
        {
            this.best = 0;
            this.SaveBest();
            changed = true;
        }

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(OmniLoc.Get(this, "GameMode"));
        ImGui.SameLine();
        if (OmniControls.RadioButton(OmniLoc.Get(this, "GameModeClassic"), this.config.GameMode == ModeClassic)
            && this.config.GameMode != ModeClassic)
        {
            this.config.GameMode = ModeClassic;
            changed              = true;
        }

        ImGui.SameLine();
        if (OmniControls.RadioButton(OmniLoc.Get(this, "GameModeOdd"), this.config.GameMode == ModeOdd)
            && this.config.GameMode != ModeOdd)
        {
            this.config.GameMode = ModeOdd;
            changed              = true;
        }

        var grid = this.config.GridSize;
        if (OmniControls.SliderInt(OmniLoc.Get(this, "GridSize"), ref grid, 4, 10, "%d", 220f * scale))
        {
            this.config.GridSize = Math.Clamp(grid, 4, 10);
            changed              = true;
        }

        if (this.config.GameMode == ModeClassic)
        {
            var diffs = this.config.DiffCount;
            if (OmniControls.SliderInt(OmniLoc.Get(this, "DiffCount"), ref diffs, 2, 8, "%d", 220f * scale))
            {
                this.config.DiffCount = Math.Clamp(diffs, 2, 8);
                changed               = true;
            }
        }

        // 时间：同一行的「无限时间」开关 + 分钟输入
        var infinite = this.config.InfiniteTime;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "InfiniteTime"), ref infinite))
        {
            this.config.InfiniteTime = infinite;
            changed                  = true;
        }

        if (!this.config.InfiniteTime)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(OmniLoc.Get(this, "Minutes"));
            ImGui.SameLine();

            ImGui.SetNextItemWidth(OmniTheme.Scale(70f));
            var minutes = this.config.Minutes;
            if (ImGui.InputInt("##MinutesInput", ref minutes))
            {
                this.config.Minutes = Math.Clamp(minutes, 1, 120);
                changed             = true;
            }
        }

        // 图标来源：折叠成一行，点开后在弹层里逐类勾选
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(OmniLoc.Get(this, "IconSource"));
        ImGui.SameLine();

        if (OmniControls.SmallButton(this.BuildSourceSummary(this.config.IconSourceMask & SrcAll), false))
            ImGui.OpenPopup("##IconSourcePopup");

        if (ImGui.BeginPopup("##IconSourcePopup"))
        {
            if (OmniControls.SmallButton(OmniLoc.Get(this, "SrcSelectAll"), false))
            {
                this.config.IconSourceMask = SrcAll;
                changed                    = true;
            }

            ImGui.SameLine();
            if (OmniControls.SmallButton(OmniLoc.Get(this, "SrcSelectNone"), false))
            {
                this.config.IconSourceMask = 0;
                changed                    = true;
            }

            changed |= this.DrawSourceCheck("SrcEquip",  SrcEquip);
            changed |= this.DrawSourceCheck("SrcFood",   SrcFood);
            changed |= this.DrawSourceCheck("SrcFish",   SrcFish);
            changed |= this.DrawSourceCheck("SrcOther",  SrcOther);
            changed |= this.DrawSourceCheck("SrcPve",    SrcPve);
            changed |= this.DrawSourceCheck("SrcPvp",    SrcPvp);
            changed |= this.DrawSourceCheck("SrcCraft",  SrcCraft);
            changed |= this.DrawSourceCheck("SrcGather", SrcGather);

            ImGui.EndPopup();
        }

        if ((this.config.IconSourceMask & SrcAll) == 0)
            ImGui.TextDisabled(OmniLoc.Get(this, "SrcEmpty"));

        var showBoard = this.config.ShowBoard;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "ShowBoard"), ref showBoard))
        {
            this.config.ShowBoard = showBoard;
            changed               = true;
        }

        var sound = this.config.Sound;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "SoundToggle"), ref sound))
        {
            this.config.Sound = sound;
            changed           = true;
        }

        var soundChat = this.config.SoundChat;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "SoundChatToggle"), ref soundChat))
        {
            this.config.SoundChat = soundChat;
            changed               = true;
        }

        changed |= this.DrawSoundRow("SeHitLabel",   this.config.SeHit,   v => this.config.SeHit   = v);
        changed |= this.DrawSoundRow("SeMissLabel",  this.config.SeMiss,  v => this.config.SeMiss  = v);
        changed |= this.DrawSoundRow("SeClearLabel", this.config.SeClear, v => this.config.SeClear = v);
        changed |= this.DrawSoundRow("SeHintLabel",  this.config.SeHint,  v => this.config.SeHint  = v);

        ImGui.TextDisabled(OmniLoc.Get(this, "SeIdHint"));

        ImGui.Spacing();
        ImGui.TextDisabled(string.Format(
            OmniLoc.Get(this, "WindowState"),
            OmniLoc.Get(this, this.config.WindowOpen ? "StateOpen" : "StateClosed")));
        ImGui.TextDisabled(string.Format(
            OmniLoc.Get(this, "StatLine"), this.levelNo, this.score, this.best, this.wrongClicks));

        if (this.showHistory) this.DrawHistory();

        return changed;
    }

    #endregion

    private string BuildStatsLine()
    {
        var key = HashCode.Combine(this.levelNo, this.score, this.best, this.wrongClicks);
        if (key == this.statsKey) return this.statsCache;

        this.statsKey   = key;
        this.statsCache = string.Format(
            OmniLoc.Get(this, "StatLine"), this.levelNo, this.score, this.best, this.wrongClicks);
        return this.statsCache;
    }

    private void DrawHistory()
    {
        ImGui.Spacing();

        if (this.history.Count == 0)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, "HistoryEmpty"));
            return;
        }

        foreach (var run in this.history)
            ImGui.TextDisabled(string.Format(
                OmniLoc.Get(this, "HistoryRow"),
                run.At.ToString("MM-dd HH:mm"), run.Score, run.Level, run.Wrong));
    }

    private bool DrawSourceCheck(string locKey, int flag)
    {
        var on = (this.config.IconSourceMask & flag) != 0;
        if (!OmniControls.Checkbox(OmniLoc.Get(this, locKey), ref on)) return false;

        this.config.IconSourceMask = on
            ? this.config.IconSourceMask | flag
            : this.config.IconSourceMask & ~flag;

        return true;
    }

    private string BuildSourceSummary(int mask)
    {
        if (mask == SrcAll)       return OmniLoc.Get(this, "SrcSummaryAll");
        if (mask == SrcItemsAll)  return OmniLoc.Get(this, "SrcSummaryItems");
        if (mask == SrcSkillsAll) return OmniLoc.Get(this, "SrcSummarySkills");

        var count = 0;
        for (var i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0) count++;

        return count == 0
            ? OmniLoc.Get(this, "SrcSummaryNone")
            : string.Format(OmniLoc.Get(this, "SrcSummaryCount"), count);
    }

    private bool DrawSoundRow(string labelKey, int current, Action<int> apply)
    {
        var changed = false;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(OmniLoc.Get(this, labelKey));
        ImGui.SameLine(OmniTheme.Scale(110f));

        ImGui.SetNextItemWidth(OmniTheme.Scale(70f));
        var value = current;
        if (ImGui.InputInt($"##{labelKey}", ref value))
        {
            value = Math.Clamp(value, 1, 255);
            apply(value);
            changed = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "SoundPreview"), false))
            Sfx.Play((uint)Math.Max(1, current), this.config.SoundChat);

        return changed;
    }

    #region 窗口

    private unsafe void DrawWindow()
    {
        // 关窗 = 结束本局；开窗 = 直接开始新的一局
        if (!this.config.WindowOpen)
        {
            if (this.wasOpen)
            {
                this.wasOpen = false;
                this.AbortRun();
            }
            return;
        }

        if (!this.wasOpen)
        {
            this.wasOpen = true;
            this.StartCampaign();
        }


        var viewport = ImGui.GetMainViewport();
        var open     = this.config.WindowOpen;

        // 折叠态：约束也要一起放松，否则会被 MainWindowMinSize 撑回原尺寸（这就是折叠后仍是一整块的 bug）
        var collapsedSize = new Vector2(OmniTheme.Scale(420f), OmniTheme.TitleBarHeight() + OmniTheme.Scale(8f));
        var targetSize    = this.collapsed ? collapsedSize : OmniTheme.ClampWindowSize(this.expandedSize);

        if (this.collapsed)
        {
            ImGui.SetNextWindowSizeConstraints(collapsedSize, viewport.WorkSize);
            ImGui.SetNextWindowSize(collapsedSize, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(OmniTheme.MainWindowMinSize(), viewport.WorkSize);

            if (this.resizePending)
            {
                ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
                this.resizePending = false;
            }
            else
            {
                ImGui.SetNextWindowSize(targetSize, ImGuiCond.FirstUseEver);
            }
        }

        ImGui.SetNextWindowPos(
            OmniTheme.ClampWindowPosition(
                viewport.WorkPos + viewport.WorkSize - targetSize,
                targetSize),
            ImGuiCond.FirstUseEver);

        if (this.focusRequested)
        {
            this.focusRequested = false;
            ImGui.SetNextWindowFocus();
        }

        if (ImGui.Begin(WindowId, ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoDocking  | ImGuiWindowFlags.NoScrollbar))
        {
            var framePos  = ImGui.GetWindowPos();
            var frameSize = ImGui.GetWindowSize();

            var dragged = OmniWindowChrome.DragTitleBar(WindowId, framePos, frameSize);
            if (dragged != framePos)
            {
                ImGui.SetWindowPos(dragged);
                framePos = dragged;
            }

            var tokens = OmniTheme.Tokens;
            var radius = OmniTheme.Scale(tokens.BorderRadius);
            var draw   = ImGui.GetWindowDrawList();

            draw.AddRectFilled(framePos, framePos + frameSize,
                ImGui.GetColorU32(Opaque(tokens.Background)), radius);

            var chrome = OmniWindowChrome.Draw(
                framePos, frameSize, this.collapsed,
                OmniLoc.Get(this, "WindowTitle"), CollapseId, CloseId, true);

            if (chrome.CloseClicked)
            {
                this.windowDrawLogged  = false;
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

            if (!this.windowDrawLogged)
            {
                this.windowDrawLogged = true;
                DalamudServices.PluginLog.Information(
                    $"[LKami9SpotDiff] window drawn: pos={framePos}, size={frameSize}, collapsed={this.collapsed}, phase={this.phase}");
            }

            if (!this.collapsed)
            {
                var (contentPos, contentSize) = OmniWindowChrome.GetContentBounds(framePos, frameSize);
                if (contentSize.X > 1f && contentSize.Y > 1f)
                {
                    ImGui.SetCursorScreenPos(contentPos);
                    ImGui.BeginChild(BoardId, contentSize, false,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

                    this.DrawContent();

                    ImGui.EndChild();
                }
            }
        }

        ImGui.End();

        if (!open) this.config.WindowOpen = false;
    }

    private void DrawContent()
    {
        var tokens = OmniTheme.Tokens;

        ImGui.TextUnformatted(this.BuildStatsLine());

        if (this.phase == Phase.Playing)
        {
            var urgent = !this.config.InfiniteTime && this.remainSeconds <= 10;
            var timeText = this.config.InfiniteTime
                ? OmniLoc.Get(this, "TimeInfinite")
                : string.Format(
                    OmniLoc.Get(this, urgent ? "TimeUrgent" : "TimeLeft"),
                    TimeSpan.FromSeconds(Math.Max(0, this.remainSeconds)));

            // 右对齐：光标此时在行首，可用宽度就是整行宽度
            var right = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(timeText).X - OmniTheme.Scale(4f);
            if (right > 0f) ImGui.SameLine(right);

            ImGui.TextColored(
                urgent ? tokens.Accent : tokens.Text with { W = tokens.Text.W * 0.75f },
                timeText);
        }

        ImGui.Spacing();

        switch (this.phase)
        {
            case Phase.Playing when this.level != null:
                if (this.paused)
                    ImGui.TextColored(tokens.Accent, OmniLoc.Get(this, "PhasePaused"));

                this.DrawBoard(this.level);
                ImGui.Spacing();

                if (ImGui.Button(OmniLoc.Get(this, this.paused ? "ResumeButton" : "PauseButton")))
                    this.TogglePause();
                ImGui.SameLine();
                if (ImGui.Button(OmniLoc.Get(this, "HintButton"))) this.UseHint();
                break;

            case Phase.Cleared:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseCleared"), this.lastGain, this.levelNo));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            case Phase.Failed:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseFailed"), this.score, this.best));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            case Phase.Ended:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseEnded"), this.score, this.best));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            default:
                if (this.config.GameMode == ModeOdd)
                    ImGui.TextUnformatted(OmniLoc.Get(this, "PhaseIdleOdd"));
                else
                    ImGui.TextUnformatted(string.Format(
                        OmniLoc.Get(this, "PhaseIdle"), this.config.DiffCount));

                if (ImGui.Button(OmniLoc.Get(this, "StartButton"))) this.StartCampaign();
                break;
        }

        if (this.poolFailed)
        {
            ImGui.Spacing();
            ImGui.TextColored(tokens.Accent,
                (this.config.IconSourceMask & SrcAll) == 0
                    ? OmniLoc.Get(this, "SrcEmpty")
                    : OmniLoc.Get(this, "PoolFailed"));
        }

    }

    #endregion

    #region 棋盘

    private void DrawBoard(Level lv)
    {
        var tokens = OmniTheme.Tokens;
        var scale  = OmniTheme.Scale(1f);

        var avail = ImGui.GetContentRegionAvail();
        var gap   = 16f * scale;

        // 棋盘下面还有一排按钮（暂停 / 提示）和一截间距，必须预留，否则单图模式会把按钮顶出窗口
        var reserve = ImGui.GetFrameHeight() + 14f * scale;
        var maxH    = MathF.Max(160f * scale, avail.Y - reserve);

        float panelW, boardW;

        if (lv.Odd)
        {
            // 单图模式：正方形取可用空间中较短的一边，水平居中
            panelW = MathF.Max(160f * scale, MathF.Min(avail.X, maxH));
            boardW = panelW;

            var pad = MathF.Max(0f, (avail.X - boardW) * 0.5f);
            if (pad > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
        }
        else
        {
            panelW = MathF.Min((avail.X - gap) * 0.5f, maxH);
            panelW = MathF.Max(panelW, 120f * scale);
            boardW = panelW * 2f + gap;
        }

        var boardH     = panelW;
        var rightOffset = lv.Odd ? 0f : panelW + gap;

        ImGui.Dummy(new Vector2(boardW, boardH));
        var origin = ImGui.GetItemRectMin();
        var draw   = ImGui.GetWindowDrawList();

        var cellW = panelW / lv.Grid;

        var panels = lv.Odd ? 1 : 2;

        if (this.config.ShowBoard)
        {
            for (var panel = 0; panel < panels; panel++)
            {
                var px = origin.X + panel * (panelW + gap);
                draw.AddRectFilled(
                    new Vector2(px, origin.Y),
                    new Vector2(px + panelW, origin.Y + boardH),
                    ImGui.GetColorU32(tokens.Surface with { W = 1f }),
                    6f * scale);
            }
        }

        var mouseInside = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
                       && ImGui.IsMouseHoveringRect(origin, origin + new Vector2(boardW, boardH));

        for (var row = 0; row < lv.Grid; row++)
        {
            for (var col = 0; col < lv.Grid; col++)
            {
                var idx = row * lv.Grid + col;

                for (var panel = 0; panel < panels; panel++)
                {
                    // 单图模式只有一块面板，画的是「被改过的那张」（Right）；画 Base 就永远看不出差异
                    var cell = lv.Odd
                        ? lv.Right[idx]
                        : (panel == 0 ? lv.Base[idx] : lv.Right[idx]);

                    var px   = origin.X + panel * (panelW + gap) + col * cellW;
                    var py   = origin.Y + row * cellW;

                    if (lv.Found[idx] && panel == (lv.Odd ? 0 : 1))
                    {
                        var rMin = new Vector2(px + 2f, py + 2f);
                        var rMax = new Vector2(px + cellW - 2f, py + cellW - 2f);

                        draw.AddRectFilled(rMin, rMax, ImGui.GetColorU32(FoundColor with { W = 0.22f }), 4f * scale);
                        draw.AddRect(rMin, rMax, ImGui.GetColorU32(FoundColor), 4f * scale,
                            ImDrawFlags.None, 3.5f * scale);

                        // 左上角常驻对勾角标，不依赖飘字
                        var badge = new Vector2(px + cellW * 0.19f, py + cellW * 0.19f);
                        var br    = cellW * 0.14f;
                        draw.AddCircleFilled(badge, br, ImGui.GetColorU32(FoundColor), 20);

                        var ink = ImGui.GetColorU32(new Vector4(0.04f, 0.16f, 0.06f, 1f));
                        draw.AddLine(badge + new Vector2(-br * 0.5f, 0f),
                                     badge + new Vector2(-br * 0.12f, br * 0.45f), ink, 2.4f * scale);
                        draw.AddLine(badge + new Vector2(-br * 0.12f, br * 0.45f),
                                     badge + new Vector2(br * 0.55f, -br * 0.45f), ink, 2.4f * scale);
                    }

                    var size = cellW * 0.86f;
                    var pad  = (cellW - size) * 0.5f;

                    this.DrawIcon(cell.IconId, new Vector2(px + pad, py + pad), new Vector2(size, size), cell.Variant);
                }
            }
        }

        for (var i = this.feedbacks.Count - 1; i >= 0; i--)
        {
            var fb  = this.feedbacks[i];
            var now = DateTime.UtcNow;
            if (now > fb.Until)
            {
                this.feedbacks.RemoveAt(i);
                continue;
            }

            var row = fb.Index / lv.Grid;
            var col = fb.Index % lv.Grid;
            var px  = origin.X + rightOffset + col * cellW;
            var py  = origin.Y + row * cellW;

            var left  = (float)(fb.Until - now).TotalMilliseconds;
            var total = fb.Correct ? 1000f : 800f;
            var k     = Math.Clamp(left / total, 0f, 1f);

            var color = fb.Correct ? FoundColor : WrongColor;
            var a     = color with { W = 0.35f + 0.65f * k };
            var thick = (3f + 3f * k) * scale;

            draw.AddRect(
                new Vector2(px + 1f, py + 1f),
                new Vector2(px + cellW - 1f, py + cellW - 1f),
                ImGui.GetColorU32(a), 3f * scale, ImDrawFlags.None, thick);

            if (fb.Correct)
            {
                var c  = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);
                var r  = cellW * 0.22f;
                draw.AddLine(c + new Vector2(-r, 0f), c + new Vector2(-r * 0.25f, r * 0.7f), ImGui.GetColorU32(a), thick);
                draw.AddLine(c + new Vector2(-r * 0.25f, r * 0.7f), c + new Vector2(r, -r * 0.7f), ImGui.GetColorU32(a), thick);
            }
            else
            {
                var c = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);
                var r = cellW * 0.2f;
                draw.AddLine(c + new Vector2(-r, -r), c + new Vector2(r, r), ImGui.GetColorU32(a), thick);
                draw.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), ImGui.GetColorU32(a), thick);
            }

            if (fb.Correct)
                draw.AddText(new Vector2(px + cellW * 0.15f, py - 6f * scale),
                    ImGui.GetColorU32(a), fb.Text);
        }

        if (this.level != null && this.hintIndex >= 0 && DateTime.UtcNow < this.hintUntil)
        {
            var row = this.hintIndex / lv.Grid;
            var col = this.hintIndex % lv.Grid;
            var px  = origin.X + rightOffset + col * cellW;
            var py  = origin.Y + row * cellW;
            var c   = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);

            var t     = (float)ImGui.GetTime();
            var pulse = 0.55f + 0.45f * MathF.Sin(t * 7f);
            var hint  = new Vector4(1f, 0.86f, 0.25f, 1f);

            draw.AddRectFilled(
                new Vector2(px + 1f, py + 1f),
                new Vector2(px + cellW - 1f, py + cellW - 1f),
                ImGui.GetColorU32(hint with { W = 0.16f + 0.20f * pulse }), 4f * scale);

            draw.AddRect(
                new Vector2(px + 2f, py + 2f),
                new Vector2(px + cellW - 2f, py + cellW - 2f),
                ImGui.GetColorU32(hint), 4f * scale, ImDrawFlags.None, (2.5f + 2.5f * pulse) * scale);

            draw.AddCircle(c, cellW * (0.32f + 0.10f * pulse),
                ImGui.GetColorU32(hint), 40, 3f * scale);

            // 上方跳动的箭头
            var bob = MathF.Sin(t * 9f) * cellW * 0.07f;
            var tip = new Vector2(c.X, py + cellW * 0.26f + bob);
            draw.AddTriangleFilled(
                new Vector2(tip.X, tip.Y + cellW * 0.13f),
                new Vector2(tip.X - cellW * 0.10f, tip.Y),
                new Vector2(tip.X + cellW * 0.10f, tip.Y),
                ImGui.GetColorU32(hint));
        }

        if (this.phase == Phase.Playing && !this.paused && mouseInside
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            this.HandleClick(origin, panelW, gap, cellW, lv);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + boardH + 6f * scale));

    }

    private void HandleClick(Vector2 origin, float panelW, float gap, float cellW, Level lv)
    {
        // 找唯一不同：找到后有一小段展示动画，这期间不再接受点击，避免连点误伤下一关
        if (this.nextLevelAt != DateTime.MinValue) return;

        var m    = ImGui.GetMousePos();
        var relX = m.X - origin.X;

        // 经典模式是左右两块面板，点在右半区先换算到单块面板的坐标系
        if (!lv.Odd && relX > panelW + gap * 0.5f)
            relX -= panelW + gap;

        if (relX < 0f || relX >= panelW) return;

        var col = (int)(relX / cellW);
        var row = (int)((m.Y - origin.Y) / cellW);
        if (col < 0 || col >= lv.Grid || row < 0 || row >= lv.Grid) return;

        var idx = row * lv.Grid + col;

        if (lv.IsDiff[idx] && !lv.Found[idx])
        {
            lv.Found[idx] = true;
            this.feedbacks.Add(new Feedback
            {
                Index   = idx,
                Correct = true,
                Until   = DateTime.UtcNow.AddMilliseconds(1000),
                Text    = string.Format(OmniLoc.Get(this, "FeedbackGain"), ScorePerHit),
            });
            this.PlayHit();
            this.score   += ScorePerHit;
            if (this.score > this.best) this.best = this.score;   // 「最高」要跟得上当前局，否则显示很怪

            if (lv.Odd)
            {
                // 单图模式是连续关卡：展示 0.42 秒后自动进下一关，不再走「过关」结算界面
                this.SaveBest();
                this.nextLevelAt = DateTime.UtcNow.AddMilliseconds(420);
            }
            else
            {
                this.CheckCleared(lv);
            }
        }
        else if (!lv.IsDiff[idx])
        {
            this.PlayMiss();
            this.wrongClicks++;
            this.score = Math.Max(0, this.score - WrongPenaltyScore);
            if (!this.config.InfiniteTime) this.deadline = this.deadline.AddSeconds(-WrongPenaltySeconds);
            this.feedbacks.Add(new Feedback
            {
                Index   = idx,
                Correct = false,
                Until   = DateTime.UtcNow.AddMilliseconds(800),
                Text    = this.config.InfiniteTime
                    ? string.Format(OmniLoc.Get(this, "FeedbackPenaltyScore"), WrongPenaltyScore)
                    : string.Format(OmniLoc.Get(this, "FeedbackPenalty"), WrongPenaltySeconds),
            });
        }
    }

    private void CheckCleared(Level lv)
    {
        for (var i = 0; i < lv.Found.Length; i++)
            if (lv.IsDiff[i] && !lv.Found[i]) return;

        this.PlayClear();
        var remain = this.config.InfiniteTime
            ? 0
            : Math.Max(0, (int)(this.deadline - DateTime.UtcNow).TotalSeconds);
        this.lastGain = lv.DiffTotal * ScorePerHit + remain * 4;
        this.score   += remain * 4;

        if (this.score > this.best)
        {
            this.best = this.score;
            this.SaveBest();
        }

        this.levelNo++;
        this.phase  = Phase.Cleared;
        this.level  = null;
    }

    private void PlayHit()  => this.PlaySe(this.config.SeHit);
    private void PlayMiss() => this.PlaySe(this.config.SeMiss);
    private void PlayClear() => this.PlaySe(this.config.SeClear);
    private void PlayHint() => this.PlaySe(this.config.SeHint);

    private void PlaySe(int id)
    {
        if (!this.config.Sound || id <= 0) return;
        Sfx.Play((uint)id, this.config.SoundChat);
    }

    private void TogglePause()
    {
        if (this.phase != Phase.Playing) return;

        if (this.paused)
        {
            if (!this.config.InfiniteTime)
                this.deadline = DateTime.UtcNow.AddSeconds(this.pausedRemain);
            this.paused = false;
        }
        else
        {
            // 不限时的时候没有 deadline 可存，暂停只冻结画面
            this.pausedRemain = this.config.InfiniteTime
                ? 0
                : Math.Max(0, (this.deadline - DateTime.UtcNow).TotalSeconds);
            this.paused = true;
        }
    }

    private void AbortRun()
    {
        if (this.phase == Phase.Playing || this.phase == Phase.Cleared)
        {
            if (this.score > this.best) this.best = this.score;
            this.SaveBest();
            this.RecordRun();
        }

        this.paused = false;
        this.level  = null;
        this.phase  = Phase.Ended;
        this.feedbacks.Clear();
    }

    private void UseHint()
    {
        if (this.level == null) return;

        for (var i = 0; i < this.level.IsDiff.Length; i++)
        {
            if (!this.level.IsDiff[i] || this.level.Found[i]) continue;
            this.PlayHint();
            this.hintIndex = i;
            this.hintUntil = DateTime.UtcNow.AddSeconds(3.5);
            if (!this.config.InfiniteTime) this.deadline = this.deadline.AddSeconds(-10);
            return;
        }
    }

    #endregion

    #region 关卡生成

    private void StartCampaign()
    {
        this.EnsureIconPool();

        // 「采集技能」这类来源图标很少，门槛按玩法放宽，别一开局就失败
        var minNeeded = this.config.GameMode == ModeOdd ? 8 : 40;
        if (this.iconPool.Count < minNeeded)
        {
            this.poolFailed = true;
            this.phase      = Phase.Idle;
            return;
        }

        // 长局会积累上千张贴图句柄，超过阈值就清一次，避免一直涨
        if (this.iconCache.Count > 1500) this.iconCache.Clear();

        this.poolFailed  = false;
        this.levelNo     = 1;
        this.score       = 0;
        this.wrongClicks = 0;
        this.hintIndex   = -1;
        this.nextLevelAt = DateTime.MinValue;
        this.runOdd      = this.config.GameMode == ModeOdd;
        this.deadline    = this.NextDeadline();

        this.BeginLevel();
    }

    private void BeginLevel()
    {
        // 不限时的「找不同」是无限连关，贴图句柄会一直涨，换关时清一次
        if (this.iconCache.Count > 2000) this.iconCache.Clear();

        var odd   = this.runOdd;
        var step  = odd ? 3 : 2;   // 找唯一不同每关都很快，格子放大得慢一点
        var grid  = Math.Clamp(this.config.GridSize + (this.levelNo - 1) / step, 4, 10);
        var diffs = Math.Clamp(this.config.DiffCount + (this.levelNo - 1) / 2, 2, Math.Min(8, grid * grid / 6));

        // 素材池小的时候（只勾了采集技能之类）自动缩小格子，硬撑会生成失败
        if (!odd && this.iconPool.Count > 0)
        {
            var maxGrid = (int)MathF.Sqrt(MathF.Max(9f, this.iconPool.Count - diffs - 8));
            grid        = Math.Min(grid, Math.Max(4, maxGrid));
        }

        var level = odd ? this.BuildOddLevel(grid) : this.BuildClassicLevel(grid, diffs);
        if (level == null)
        {
            // 生成不出来（素材池不够 / 找不到可用的差异）：正在打就收摊，别把玩家卡在找不到目标的旧局面上
            var wasPlaying = this.phase == Phase.Playing;

            this.level       = null;
            this.nextLevelAt = DateTime.MinValue;

            if (wasPlaying)
            {
                this.phase = Phase.Failed;
                this.RecordRun();
            }

            return;
        }

        this.level         = level;
        this.phase         = Phase.Playing;
        this.hintIndex     = -1;
        this.nextLevelAt   = DateTime.MinValue;

        // 单图模式是连着打的一整局：倒计时不随关卡重置，时间耗尽即本局结束
        if (!odd)
        {
            this.deadline      = this.NextDeadline();
            this.remainSeconds = this.config.Minutes * 60;
        }
    }

    /// <summary>
    /// 不限时的时候给一个很远的期限，这样所有「往 deadline 上加减」的代码都不用特殊处理。
    /// </summary>
    private DateTime NextDeadline()
    {
        return this.config.InfiniteTime
            ? DateTime.UtcNow.AddYears(50)
            : DateTime.UtcNow.AddMinutes(this.config.Minutes);
    }

    /// <summary>
    /// 找唯一不同：整屏同一个图标，只有一个格子被换掉 / 翻转 / 镜像。
    /// </summary>
    private Level? BuildOddLevel(int grid)
    {
        var n = grid * grid;
        if (this.iconPool.Count < 2) return null;

        var level = new Level
        {
            Grid   = grid,
            Odd    = true,
            Right  = new Cell[n],
            IsDiff = new bool[n],
            Found  = new bool[n],
        };

        // 单图模式只绘制 Right，Base 直接指向同一份数据，省掉一整份格子对象
        level.Base = level.Right;

        // 偶尔会抽到「换的图标长得一样」或「图标太对称不该翻转」，重抽几次而不是直接判死
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var basePick = this.iconPool[this.rng.Next(this.iconPool.Count)];

            for (var i = 0; i < n; i++)
                level.Right[i] = new Cell { IconId = basePick.IconId, Category = basePick.Category };

            var oddIdx = this.rng.Next(n);
            var cell   = level.Right[oddIdx];

            // 换近似图标占大头（形状相近但确实不同），旋转/镜像作点缀，且必须确认图标不对称
            var roll = this.rng.Next(100);
            var done = false;

            if (roll < 65)
                done = this.ApplySimilarIcon(cell, basePick.IconId, basePick.Category);
            else if (roll < 90)
                done = this.ApplyTransform(cell, basePick.IconId, false);
            else
                done = this.ApplyTransform(cell, basePick.IconId, true);

            // 兜底：上面的方式都不适用（找不到同类近似图标 / 图标太对称），退回任一不同图标
            if (!done)
                done = this.ApplySimilarIcon(cell, basePick.IconId, basePick.Category, true)
                    || this.ApplyTransform(cell, basePick.IconId, false);

            // 关键校验：这一格必须真的和底图不一样，否则整关无解（曾经因为画错面板造成全屏一样的死关）
            if (!done || (cell.IconId == basePick.IconId && cell.Variant == 0)) continue;

            Array.Clear(level.IsDiff, 0, n);
            Array.Clear(level.Found,  0, n);

            level.IsDiff[oddIdx] = true;
            level.DiffTotal      = 1;

            return level;
        }

        return null;
    }

    /// <summary>
    /// 换一张「近似图标」：优先同 UI 分类、图标 ID 接近的（通常是同模型的品质/染色变体）。
    /// loose = true 时放宽到池内任意图标。
    /// </summary>
    private bool ApplySimilarIcon(Cell cell, uint baseIconId, uint category, bool loose = false)
    {
        if (this.iconByCategory.TryGetValue(category, out var list) && list.Count > 1)
        {
            var baseHash = this.ArtHash(baseIconId);

            foreach (var radius in SimilarRadii)
            {
                if (!loose && radius == long.MaxValue) break;

                var candidates = new List<uint>(8);
                foreach (var id in list)
                {
                    if (id == baseIconId) continue;
                    if (Math.Abs((long)id - baseIconId) <= radius) candidates.Add(id);
                }

                // 逐个试：跳过「换了 ID 但画的是同一张图」的候选，否则那一格看起来毫无变化。
                // 只试有限次，避免一个分类里全是同图时把贴图读爆
                var tries = 0;
                while (candidates.Count > 0 && tries++ < 8)
                {
                    var pick = this.rng.Next(candidates.Count);
                    var id   = candidates[pick];

                    if (baseHash == 0 || this.ArtHash(id) != baseHash)
                    {
                        cell.IconId   = id;
                        cell.Category = category;
                        return true;
                    }

                    candidates.RemoveAt(pick);
                }
            }
        }

        if (!loose) return false;

        // 任意图标兜底：跳过自己，形状差异明显但至少保证有解
        var baseArt = this.ArtHash(baseIconId);
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var pick = this.iconPool[this.rng.Next(this.iconPool.Count)];
            if (pick.IconId == baseIconId) continue;
            if (baseArt != 0 && this.ArtHash(pick.IconId) == baseArt) continue;

            cell.IconId   = pick.IconId;
            cell.Category = pick.Category;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 图标贴图的像素指纹：ID 不同但画的是同一张图的图标，指纹相同。
    /// 只用在「找不同」的差异格上（每关最多查几次），不做整池预计算——那要读上万张贴图。
    /// </summary>
    private uint ArtHash(uint iconId)
    {
        if (this.artHashCache.TryGetValue(iconId, out var cached)) return cached;

        uint hash = 0;
        try
        {
            var tex = this.LoadIconTex(iconId);
            if (tex != null)
            {
                hash = 2166136261u;
                var data = tex.ImageData;
                var len  = Math.Min(data.Length, tex.Header.Width * tex.Header.Height * 4);
                for (var i = 0; i < len; i++)
                    hash = (hash ^ data[i]) * 16777619u;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, $"[LKami9SpotDiff] art hash failed: {iconId}");
        }

        this.artHashCache[iconId] = hash;
        return hash;
    }

    /// <summary>
    /// 用翻转（镜像 / 旋转 180°）制造差异，前提是图标真的不对称。
    /// </summary>
    private bool ApplyTransform(Cell cell, uint baseIconId, bool mirror)
    {
        if (!this.IsTransformVisible(baseIconId, mirror)) return false;

        cell.IconId   = baseIconId;
        cell.Variant  = mirror ? 1 : 2;
        return true;
    }

    private bool IsTransformVisible(uint iconId, bool mirror)
    {
        if (!this.symCache.TryGetValue(iconId, out var flags))
        {
            flags                = this.ComputeSymmetry(iconId);
            this.symCache[iconId] = flags;
        }

        return mirror ? (flags & 1) != 0 : (flags & 2) != 0;
    }

    /// <summary>
    /// 按图标 ID 读贴图（读取已解压的像素数据）。找不到文件返回 null，不抛。
    /// </summary>
    private Lumina.Data.Files.TexFile? LoadIconTex(uint iconId)
    {
        try
        {
            var path = $"ui/icon/{iconId / 1000 * 1000:D6}/{iconId:D6}.tex";
            return DalamudServices.DataManager.GetFile<Lumina.Data.Files.TexFile>(path);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, $"[LKami9SpotDiff] load icon tex failed: {iconId}");
            return null;
        }
    }

    /// <summary>
    /// 直接读图标贴图像素，算镜像 / 旋转 180° 后的平均差。
    /// Lumina 的 ImageData 已经是解压后的 4 字节/像素，DXT 图标也能直接比较。
    /// 返回 bit0 = 镜像看得出差别，bit1 = 旋转 180° 看得出差别。
    /// </summary>
    private byte ComputeSymmetry(uint iconId)
    {
        byte flags = 3;   // 读不到贴图时按「看得出」处理，别把整关卡死

        try
        {
            var tex  = this.LoadIconTex(iconId);
            var w    = tex?.Header.Width  ?? 0;
            var h    = tex?.Header.Height ?? 0;
            var data = tex?.ImageData;
            if (w <= 1 || h <= 1 || data == null || data.Length < w * h * 4) return flags;

            double mirrorSum = 0, rotSum = 0;
            var    visible   = 0;

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i  = (y * w + x) * 4;
                    var mi = (y * w + (w - 1 - x)) * 4;
                    var ri = ((h - 1 - y) * w + (w - 1 - x)) * 4;

                    if (data[i + 3] == 0 && data[mi + 3] == 0 && data[ri + 3] == 0) continue;

                    visible++;
                    mirrorSum += Math.Abs(data[i + 2] - data[mi + 2])
                               + Math.Abs(data[i + 1] - data[mi + 1])
                               + Math.Abs(data[i]     - data[mi]);
                    rotSum    += Math.Abs(data[i + 2] - data[ri + 2])
                               + Math.Abs(data[i + 1] - data[ri + 1])
                               + Math.Abs(data[i]     - data[ri]);
                }
            }

            if (visible == 0) return flags;

            var mirrorDiff = mirrorSum / visible / 3.0;
            var rotDiff    = rotSum    / visible / 3.0;

            flags = 0;
            if (mirrorDiff >= SymmetricThreshold) flags |= 1;
            if (rotDiff    >= SymmetricThreshold) flags |= 2;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, $"[LKami9SpotDiff] symmetry check failed: {iconId}");
        }

        return flags;
    }

    private Level? BuildClassicLevel(int grid, int diffs)
    {
        var n = grid * grid;
        if (this.iconPool.Count < n + diffs + 8) return null;

        var level = new Level
        {
            Grid   = grid,
            Base   = new Cell[n],
            Right  = new Cell[n],
            IsDiff = new bool[n],
            Found  = new bool[n],
        };

        var used = new HashSet<int>();
        for (var i = 0; i < n; i++)
        {
            var k    = this.PickUnused(used);
            var pick = this.iconPool[k];
            level.Base[i]  = new Cell { IconId = pick.IconId, Category = pick.Category };
            level.Right[i] = new Cell { IconId = pick.IconId, Category = pick.Category };
        }

        var picked = new HashSet<int>();
        var guard  = 0;
        while (picked.Count < diffs && guard++ < diffs * 40)
        {
            var i = this.rng.Next(n);
            if (!picked.Add(i)) continue;

            var cell = level.Right[i];

            // 有相当一部分图标是左右镜像对称的（圆盾、圆盘、铃铛），
            // 翻转/旋转之后完全看不出来，所以换图标占大头，翻转变形只作点缀
            var roll = this.rng.Next(10);
            var kind = roll < 6 ? 0 : roll < 8 ? 1 : 2;
            var applied = false;

            // 翻转类差异必须先确认图标不对称
            if (kind != 0)
            {
                var mirror = kind == 1;
                if (this.IsTransformVisible(cell.IconId, mirror))
                {
                    cell.Variant = mirror ? 1 : 2;
                    applied      = true;
                }
            }

            if (!applied)
            {
                // 换到「不同 UI 类别」的图标，形状差异最大，也最不容易看走眼
                var alt = this.PickDifferentCategory(used, cell.Category);
                if (alt >= 0)
                {
                    cell.IconId   = this.iconPool[alt].IconId;
                    cell.Category = this.iconPool[alt].Category;
                    applied       = true;
                }
            }

            if (!applied)
            {
                // 换不了图标、翻转又看不出来：撤销这一格，换一格重试，绝不留下看不见的差异
                picked.Remove(i);
                cell.IconId  = level.Base[i].IconId;
                cell.Variant = 0;
                continue;
            }

            level.IsDiff[i] = true;
        }

        if (picked.Count == 0) return null;

        level.DiffTotal = picked.Count;
        return level;
    }

    private int PickDifferentCategory(HashSet<int> used, uint avoidCategory)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var k = this.rng.Next(this.iconPool.Count);
            if (this.iconPool[k].Category == avoidCategory) continue;
            if (!used.Add(k)) continue;
            return k;
        }

        return -1;
    }

    private int PickUnused(HashSet<int> used)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var k = this.rng.Next(this.iconPool.Count);
            if (used.Add(k)) return k;
        }

        return this.rng.Next(this.iconPool.Count);
    }

    private bool poolFailed;
    private int  poolMask = -1;     // -1 = 尚未构建，其余为 Src* 位标志组合
    private bool runOdd;            // 本局是不是「找唯一不同」，开局时锁定，中途改设置不影响当前局

    private void EnsureIconPool()
    {
        var mask = this.config.IconSourceMask & SrcAll;

        if (this.iconPool.Count > 0 && this.poolMask == mask) return;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        this.iconPool.Clear();
        this.iconByCategory.Clear();
        this.poolMask = mask;

        if (mask == 0)
        {
            this.poolFailed = true;
            return;
        }

        var cacheKey = this.PoolCacheKey();

        if (this.TryLoadPoolCache(mask, cacheKey))
        {
            watch.Stop();
            this.poolFailed = false;
            DalamudServices.PluginLog.Information(
                $"[LKami9SpotDiff] icon pool(缓存): mask=0x{mask:X2}, {this.iconPool.Count} 个，耗时 {watch.ElapsedMilliseconds} ms");
            return;
        }

        try
        {
            // 所有来源共用一个图标 ID 去重集：同一张图只留先扫到的那一份
            var seenIcon  = new HashSet<uint>();
            var seenModel = new HashSet<ulong>();

            if ((mask & SrcItemsAll) != 0)  this.AddItemIcons(seenIcon, seenModel, mask);
            if ((mask & SrcSkillsAll) != 0) this.AddSkillIcons(seenIcon, mask);

            watch.Stop();
            this.poolFailed = false;

            DalamudServices.PluginLog.Information(
                $"[LKami9SpotDiff] icon pool: mask=0x{mask:X2}, {this.iconPool.Count} 个，耗时 {watch.ElapsedMilliseconds} ms");

            this.SavePoolCache(mask, cacheKey);
        }
        catch (Exception ex)
        {
            // 扫描中途失败会留下半张池子，清掉，下次进来重新建
            this.iconPool.Clear();
            this.iconByCategory.Clear();
            this.poolFailed = true;
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] build icon pool failed");
        }
    }

    // ---------- 建池缓存 ----------
    // 扫一遍表和模型要 300ms 左右，缓存到配置目录，下次开窗直接读。
    // 失效判据用「三张表的行数」：游戏更新后行数变化，缓存自然作废。

    private string PoolCachePath(int mask) =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(),
            $"LKami9SpotDiff.pool.{mask}.json");

    private string PoolCacheKey()
    {
        try
        {
            var items   = DalamudServices.DataManager.GetExcelSheet<Item>().Count;
            var actions = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().Count;
            var crafts  = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.CraftAction>().Count;

            // 带上分桶规则版本：改了分类逻辑就让旧缓存失效，不用等游戏更新
            return $"{PoolCacheVersion}-{items}-{actions}-{crafts}";
        }
        catch
        {
            return string.Empty;   // 取不到行数就不缓存，宁慢不脏
        }
    }

    private bool TryLoadPoolCache(int mask, string key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        try
        {
            var path = this.PoolCachePath(mask);
            if (!File.Exists(path)) return false;

            var cache = JsonSerializer.Deserialize<PoolCache>(File.ReadAllText(path));
            if (cache == null || cache.Key != key) return false;
            if (cache.Icons == null || cache.Categories == null) return false;
            if (cache.Icons.Length == 0 || cache.Icons.Length != cache.Categories.Length) return false;

            for (var i = 0; i < cache.Icons.Length; i++)
                this.AddToPool(cache.Icons[i], cache.Categories[i]);

            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9SpotDiff] load icon pool cache failed");
            return false;
        }
    }

    private void SavePoolCache(int mask, string key)
    {
        if (string.IsNullOrEmpty(key) || this.iconPool.Count == 0) return;

        try
        {
            var icons = new uint[this.iconPool.Count];
            var cats  = new uint[this.iconPool.Count];

            for (var i = 0; i < this.iconPool.Count; i++)
            {
                icons[i] = this.iconPool[i].IconId;
                cats[i]  = this.iconPool[i].Category;
            }

            File.WriteAllText(this.PoolCachePath(mask),
                JsonSerializer.Serialize(new PoolCache { Key = key, Icons = icons, Categories = cats }));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9SpotDiff] save icon pool cache failed");
        }
    }

    private void AddItemIcons(HashSet<uint> seenIcon, HashSet<ulong> seenModel, int mask)
    {
        foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
        {
            if (item.RowId == 0) continue;

            var icon = item.Icon;
            if (icon == 0) continue;
            if (string.IsNullOrWhiteSpace(item.Name.ToString())) continue;

            var cat      = item.ItemUICategory.RowId;
            var isEquip  = item.EquipSlotCategory.RowId != 0;
            var isFood   = cat == 45 || cat == 46;   // 食材 / 食品
            var isFish   = cat == 47;                // 水产品
            uint category;

            if (isEquip)
            {
                if ((mask & SrcEquip) == 0) continue;
                category = CatEquipBase + cat;
            }
            else if (isFood)
            {
                if ((mask & SrcFood) == 0) continue;
                category = CatFoodBase + cat;
            }
            else if (isFish)
            {
                if ((mask & SrcFish) == 0) continue;
                category = CatFishBase + cat;
            }
            else
            {
                if ((mask & SrcOther) == 0) continue;
                category = CatOtherBase + cat;
            }

            if (!seenIcon.Add(icon)) continue;

            // 同模型换色的装备图标 ID 不同但外形一模一样，装备类按模型再筛一遍
            if (isEquip)
            {
                var model = item.ModelMain;
                if (model != 0 && !seenModel.Add(model)) continue;
            }

            this.AddToPool(icon, category);
        }
    }

    private void AddSkillIcons(HashSet<uint> seenIcon, int mask)
    {
        var actions = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

        foreach (var action in actions)
        {
            var icon = action.Icon;
            if (icon == 0) continue;
            if (!action.IsPlayerAction) continue;     // 剔除未实装 / 怪物专用行
            if (string.IsNullOrWhiteSpace(action.Name.ToString())) continue;

            var jobCategory = action.ClassJobCategory.RowId;
            var isGather    = false;
            foreach (var job in GatherJobCategories)
            {
                if (jobCategory != job) continue;
                isGather = true;
                break;
            }

            uint category;

            if (isGather)
            {
                if ((mask & SrcGather) == 0) continue;
                category = CatGatherBase + jobCategory;
            }
            else if (action.IsPvP)
            {
                if ((mask & SrcPvp) == 0) continue;
                category = CatPvpBase + action.ActionCategory.RowId;
            }
            else
            {
                if ((mask & SrcPve) == 0) continue;
                category = CatPveBase + action.ActionCategory.RowId;
            }

            if (!seenIcon.Add(icon)) continue;
            this.AddToPool(icon, category);
        }

        if ((mask & SrcCraft) == 0) return;

        foreach (var craft in DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.CraftAction>())
        {
            var icon = craft.Icon;
            if (icon == 0) continue;
            if (string.IsNullOrWhiteSpace(craft.Name.ToString())) continue;
            if (!seenIcon.Add(icon)) continue;

            this.AddToPool(icon, CatCraftBase);
        }
    }

    private void AddToPool(uint icon, uint category)
    {
        this.iconPool.Add((icon, category));

        if (!this.iconByCategory.TryGetValue(category, out var list))
            this.iconByCategory[category] = list = [];

        list.Add(icon);
    }

    private void DrawIcon(uint iconId, Vector2 pos, Vector2 size, int variant = 0)
    {
        if (this.brokenIcons.Contains(iconId)) return;

        if (!this.iconCache.TryGetValue(iconId, out var shared))
        {
            try
            {
                shared = DalamudServices.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(iconId));
                this.iconCache[iconId] = shared;
            }
            catch
            {
                // 记下来，别每帧都为一个坏 ID 抛一次异常
                this.brokenIcons.Add(iconId);
                return;
            }
        }

        // 纹理是异步加载的：没准备好时 GetWrapOrDefault 返回 null，等下一帧再来。
        // 绝不能缓存空 wrap，否则那个图标会永远画不出来。
        var wrap = shared.GetWrapOrDefault();
        if (wrap == null) return;

        if (variant == 0)
        {
            ImGui.SetCursorScreenPos(pos);
            ImGui.Image(wrap.Handle, size, Vector2.Zero, Vector2.One, Vector4.One);
            return;
        }

        // 只交换 uv，不改尺寸：镜像翻左右，旋转 180° 再上下也翻一次
        var draw = ImGui.GetWindowDrawList();
        var (u0, u1, u2, u3) = variant == 1
            ? (new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f))
            : (new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f));

        draw.AddImageQuad(
            wrap.Handle,
            pos,
            pos + new Vector2(size.X, 0f),
            pos + size,
            pos + new Vector2(0f, size.Y),
            u0, u1, u2, u3,
            ImGui.GetColorU32(Vector4.One));
    }

    #endregion

    #region 最高分持久化

    private void RecordRun()
    {
        if (this.score <= 0) return;

        this.history.Insert(0, new RunRecord
        {
            At    = DateTime.Now,
            Score = this.score,
            Level = this.levelNo,
            Wrong = this.wrongClicks,
        });

        if (this.history.Count > 20)
            this.history.RemoveRange(20, this.history.Count - 20);

        this.SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(this.HistoryFilePath)) return;
            var payload = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(this.HistoryFilePath));
            if (payload == null) return;

            this.history.Clear();
            var count = Math.Min(payload.Count, 20);
            for (var i = 0; i < count; i++) this.history.Add(payload[i]);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] load history failed");
        }
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(this.HistoryFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(this.HistoryFilePath, JsonSerializer.Serialize(this.history));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] save history failed");
        }
    }

    private void LoadBest()
    {
        try
        {
            if (!File.Exists(this.BestFilePath)) return;
            var payload = JsonSerializer.Deserialize<BestPayload>(File.ReadAllText(this.BestFilePath));
            if (payload != null) this.best = Math.Max(0, payload.Best);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] load best failed");
        }
    }

    private void SaveBest()
    {
        try
        {
            var dir = Path.GetDirectoryName(this.BestFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(this.BestFilePath,
                JsonSerializer.Serialize(new BestPayload { Best = this.best }));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] save best failed");
        }
    }

    private sealed class BestPayload
    {
        public int Best { get; set; }
    }

    private sealed class PoolCache
    {
        public string Key        { get; set; } = string.Empty;
        public uint[] Icons      { get; set; } = [];
        public uint[] Categories { get; set; } = [];
    }

    private sealed class RunRecord
    {
        public DateTime At     { get; set; }
        public int      Score  { get; set; }
        public int      Level  { get; set; }
        public int      Wrong  { get; set; }
    }

    #endregion

    /// <summary>
    /// 直接播游戏自带音效。两条正确入口（都在 ClientStructs 的 UIGlobals 上）：
    ///   PlayChatSoundEffect(id) —— 聊天提示音，合法 id 1~16
    ///   PlaySoundEffect(id)     —— 通用 UI 音效，窗口开关是 23/24
    /// 用错入口（比如拿 1~4 去调通用接口）不会报错，只会静默无声。
    /// </summary>
    private static class Sfx
    {
        public static unsafe void Play(uint id, bool chat)
        {
            try
            {
                if (chat) FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect(id);
                else      FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(id);
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] PlaySe failed");
            }
        }
    }

    private static Vector4 Opaque(Vector4 c) => c with { W = 1f };
}
