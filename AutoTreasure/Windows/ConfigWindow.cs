using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoTreasure.Automation;
using AutoTreasure.Data;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoTreasure.Windows;

/// <summary>
/// Settings window covering stages 1, 2, 6 and the stage-7 safety options.
///
/// The map-grade, market-city and drop-item controls are all driven by dropdowns
/// built from live game data at runtime, so nothing breaks when the Chinese client
/// differs from the global one.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string blacklistInput = string.Empty;
    private string whitelistInput = string.Empty;
    private string dropSearch = string.Empty;

    public ConfigWindow(Plugin plugin)
        : base("自动挖宝设置###AutoTreasureConfig")
    {
        this.plugin = plugin;

        this.Size = new Vector2(640, 660);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = this.plugin.Configuration;

        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("地图 / 买图"))
            {
                this.DrawMapSelection(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("市场布告板"))
            {
                this.DrawMarketBoard(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("丢弃垃圾"))
            {
                this.DrawJunk(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("总控 / 安全"))
            {
                this.DrawGlobal(config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ------------------------------------------------------- 藏宝图等级下拉
    private void DrawMapSelection(Configuration config)
    {
        var grades = this.plugin.Catalog.MapGrades;

        ImGui.TextDisabled("藏宝图等级（下拉选择，不使用物品 ID）");
        ImGui.Separator();

        if (grades.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                "未能从游戏数据读取藏宝图列表（TreasureHuntRank）。");
            ImGui.TextWrapped("请确认已进入游戏并登录角色，然后重新打开本窗口。");
            return;
        }

        // Summary of what is currently selected.
        var selectedCount = config.MapItemIds.Count;
        ImGui.Text($"已选 {selectedCount} / {grades.Count} 个等级");
        ImGui.SameLine();
        if (ImGui.Button("全选"))
        {
            config.MapItemIds = grades.Select(g => g.ItemId).ToList();
            config.SelectedMapGrades = grades.Select(g => g.GradeCode ?? g.DisplayName).ToList();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("全不选"))
        {
            config.MapItemIds.Clear();
            config.SelectedMapGrades.Clear();
            config.Save();
        }

        ImGui.Spacing();

        // The dropdown itself.
        var preview = selectedCount == 0
            ? "请选择藏宝图等级…"
            : selectedCount == 1
                ? grades.FirstOrDefault(g => config.MapItemIds.Contains(g.ItemId))?.DisplayName ?? "1 项"
                : $"已选择 {selectedCount} 个等级";

        if (ImGui.BeginCombo("##mapgrades", preview))
        {
            ImGui.TextDisabled("勾选要自动处理的地图等级");
            ImGui.Separator();

            foreach (var grade in grades)
            {
                var isSelected = config.MapItemIds.Contains(grade.ItemId);
                var label = grade.GradeCode is null
                    ? $"（无编号）{grade.ItemName}"
                    : $"{grade.GradeCode} — {grade.ItemName}";

                if (grade.MaxPartySize > 1)
                {
                    label += $"  [{grade.MaxPartySize}人]";
                }

                if (ImGui.Checkbox(label + $"##g{grade.ItemId}", ref isSelected))
                {
                    if (isSelected)
                    {
                        if (!config.MapItemIds.Contains(grade.ItemId))
                        {
                            config.MapItemIds.Add(grade.ItemId);
                            if (grade.GradeCode is not null)
                            {
                                config.SelectedMapGrades.Add(grade.GradeCode);
                            }
                        }
                    }
                    else
                    {
                        config.MapItemIds.Remove(grade.ItemId);
                        if (grade.GradeCode is not null)
                        {
                            config.SelectedMapGrades.Remove(grade.GradeCode);
                        }
                    }

                    config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"共从游戏数据读取 {grades.Count} 个等级条目。");

        ImGui.Separator();
        ImGui.TextDisabled("买图阶段");

        var desired = config.DesiredMapStock;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("目标库存（张）", ref desired, 1, 99))
        {
            config.DesiredMapStock = desired;
            config.Save();
        }

        var reserve = (int)Math.Clamp(config.MinimumGilReserve, 0, int.MaxValue);
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt("金币保留线", ref reserve, 10000, 100000))
        {
            config.MinimumGilReserve = Math.Max(0, reserve);
            config.Save();
        }
    }

    // ------------------------------------------------------ 市场布告板下拉
    private void DrawMarketBoard(Configuration config)
    {
        var nav = this.plugin.MarketBoard;

        ImGui.TextDisabled("目标主城（下拉选择）");
        ImGui.Separator();

        var preview = config.SelectedMarketCityName ?? "请选择主城…";
        if (ImGui.BeginCombo("##city", preview))
        {
            foreach (var city in MarketCities.All)
            {
                var isSelected = config.SelectedMarketCityName == city.DisplayName;
                if (ImGui.Selectable(city.DisplayName, isSelected))
                {
                    config.SelectedMarketCityName = city.DisplayName;
                    config.Save();
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("清除选择"))
        {
            config.SelectedMarketCityName = null;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("当前状态");

        // Dependency status, so the user can see why something is unavailable.
        var navReady = nav is not null && this.plugin.Navmesh.IsAvailable;
        var lifeReady = this.plugin.Lifestream.IsAvailable;

        ImGui.TextColored(
            navReady ? new Vector4(0.45f, 0.9f, 0.5f, 1f) : new Vector4(1f, 0.6f, 0.3f, 1f),
            navReady ? "vnavmesh：可用" : "vnavmesh：不可用（需安装并加载）");

        ImGui.TextColored(
            lifeReady ? new Vector4(0.45f, 0.9f, 0.5f, 1f) : new Vector4(1f, 0.6f, 0.3f, 1f),
            lifeReady ? "Lifestream：可用（跨城传送）" : "Lifestream：不可用（跨城传送需要它）");

        if (nav is not null)
        {
            ImGui.Text($"定位：{nav.GetAvailabilityText()}");
        }

        ImGui.Spacing();
        if (ImGui.Button("立即前往市场布告板", new Vector2(200, 0)))
        {
            var result = this.plugin.TravelToMarketBoard();
            this.plugin.Activity.Add(result);
        }

        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            this.plugin.MarketBoard?.Cancel();
        }

        ImGui.Spacing();
        ImGui.TextWrapped(
            "说明：在已有市场布告板的地图会直接经 vnavmesh 走过去；不在该地图时会先用 " +
            "Lifestream 传送到所选主城，到达后自动走向布告板。布告板位置在运行时从游戏对象中识别，" +
            "不使用写死的坐标。");
    }

    // ------------------------------------------------------------------- 6 丢弃
    private void DrawJunk(Configuration config)
    {
        var enabled = config.AutoDiscardEnabled;
        if (ImGui.Checkbox("启用自动丢弃", ref enabled))
        {
            config.AutoDiscardEnabled = enabled;
            config.Save();
        }

        ImGui.Separator();

        var free = config.FreeSlotThreshold;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("剩余格数低于此值时清理", ref free, 0, 40))
        {
            config.FreeSlotThreshold = free;
            config.Save();
        }

        var every = config.DiscardEveryCycle;
        if (ImGui.Checkbox("每轮结束都清理一次", ref every))
        {
            config.DiscardEveryCycle = every;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("按地图等级的掉落物选择（下拉）");

        this.DrawDropItemSelector(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("判定规则");

        var price = (int)Math.Clamp(config.VendorPriceThreshold, 0, int.MaxValue);
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt("低于此售价则丢弃（0=关闭）", ref price, 10, 100))
        {
            config.VendorPriceThreshold = Math.Max(0, price);
            config.Save();
        }

        var white = config.DiscardWhiteGear;
        if (ImGui.Checkbox("丢弃白色品质装备", ref white))
        {
            config.DiscardWhiteGear = white;
            config.Save();
        }

        var green = config.DiscardGreenGear;
        if (ImGui.Checkbox("丢弃绿色品质装备", ref green))
        {
            config.DiscardGreenGear = green;
            config.Save();
        }

        var protect = config.ProtectMarketable;
        if (ImGui.Checkbox("保护可交易物品", ref protect))
        {
            config.ProtectMarketable = protect;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##lists"))
        {
            if (ImGui.BeginTabItem("黑名单（总是丢弃）"))
            {
                this.DrawIdList(config.DiscardBlacklist, ref this.blacklistInput, "blacklist");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("白名单（永不丢弃）"))
            {
                this.DrawIdList(config.DiscardWhitelist, ref this.whitelistInput, "whitelist");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// Searchable item picker for the discard list.
    ///
    /// This deliberately does NOT present a fixed "what drops from grade N" table,
    /// because no such table exists in game data: the <c>Treasure</c> sheet only
    /// holds the coffer's localized noun and model path, and published third-party
    /// drop lists are provably incomplete (G17 lists 2 items, G18 lists 1, and all
    /// five S-maps list none). Showing those numbers as fact would be misleading, so
    /// the list is curated by the user via search instead.
    /// </summary>
    private void DrawDropItemSelector(Configuration config)
    {
        var grades = this.plugin.Catalog.MapGrades;
        if (grades.Count == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "游戏数据尚未就绪。");
            return;
        }

        // Which grade's dig spots to show; purely informational context.
        var gradePreview = this.selectedDropGradeName ?? "选择地图等级…";
        if (ImGui.BeginCombo("##dropgrade", gradePreview))
        {
            foreach (var grade in grades)
            {
                var label = grade.GradeCode is null
                    ? $"（无编号）{grade.ItemName}"
                    : $"{grade.GradeCode} — {grade.ItemName}";

                if (ImGui.Selectable(label, this.selectedDropGradeName == grade.DisplayName))
                {
                    this.selectedDropGradeName = grade.DisplayName;
                    this.selectedDropGradeItemId = grade.ItemId;
                    this.digSpotsCache = null;
                }
            }

            ImGui.EndCombo();
        }

        var selectedGrade = grades.FirstOrDefault(g => g.ItemId == this.selectedDropGradeItemId);
        if (selectedGrade is not null)
        {
            // Show where this grade's chests actually are — verified game data.
            this.digSpotsCache ??= this.plugin.Catalog.GetDigSpots(selectedGrade);
            var spots = this.digSpotsCache;

            if (spots.Count > 0)
            {
                var territories = spots
                    .Select(s => s.TerritoryName)
                    .Distinct()
                    .OrderBy(n => n, StringComparer.CurrentCulture)
                    .ToList();

                ImGui.TextDisabled($"该等级共 {spots.Count} 个藏宝点，分布于 {territories.Count} 张地图：");
                ImGui.TextWrapped(string.Join("、", territories.Take(12))
                                  + (territories.Count > 12 ? " …" : string.Empty));
            }
            else
            {
                ImGui.TextDisabled("该等级暂无藏宝点数据。");
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("搜索要丢弃的物品（游戏数据里没有现成的掉落表，需自行勾选）");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##dropsearch", "输入物品名称，至少 2 个字…", ref this.dropSearch, 64);
        ImGui.SameLine();
        if (ImGui.Button("搜索##dosearch"))
        {
            this.searchResultsCache = this.plugin.Catalog.SearchItems(this.dropSearch);
            this.lastSearchTerm = this.dropSearch;
        }

        if (this.searchResultsCache is null)
        {
            ImGui.TextDisabled("输入关键词后点「搜索」。");
            return;
        }

        ImGui.TextDisabled($"“{this.lastSearchTerm}” 找到 {this.searchResultsCache.Count} 项；勾选即加入黑名单。");

        ImGui.BeginChild("##dropitems", new Vector2(0, 180), true);
        foreach (var (itemId, name) in this.searchResultsCache)
        {
            var selected = config.DiscardBlacklist.Contains(itemId);
            if (ImGui.Checkbox($"{name}##drop{itemId}", ref selected))
            {
                if (selected)
                {
                    if (!config.DiscardBlacklist.Contains(itemId))
                    {
                        config.DiscardBlacklist.Add(itemId);
                    }
                }
                else
                {
                    config.DiscardBlacklist.Remove(itemId);
                }

                config.Save();
            }
        }

        ImGui.EndChild();
    }

    private string? selectedDropGradeName;
    private uint selectedDropGradeItemId;
    private IReadOnlyList<(uint ItemId, string Name)>? searchResultsCache;
    private string lastSearchTerm = string.Empty;
    private IReadOnlyList<GameDataCatalog.DigSpot>? digSpotsCache;

    private void DrawIdList(List<uint> list, ref string input, string id)
    {
        ImGui.SetNextItemWidth(160);
        ImGui.InputText($"##input{id}", ref input, 12);
        ImGui.SameLine();
        if (ImGui.Button($"添加##add{id}") && uint.TryParse(input.Trim(), out var value) && value > 0)
        {
            if (!list.Contains(value))
            {
                list.Add(value);
                this.plugin.Configuration.Save();
            }

            input = string.Empty;
        }

        ImGui.Separator();
        ImGui.BeginChild($"##scroll{id}", new Vector2(0, 200), true);

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var itemId = list[i];
            ImGui.PushID($"{id}{i}");
            if (ImGui.Button("移除"))
            {
                list.RemoveAt(i);
                this.plugin.Configuration.Save();
                ImGui.PopID();
                continue;
            }

            ImGui.SameLine();
            ImGui.Text($"{InventoryScanner.GetItemName(itemId)}（ID {itemId}）");
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // ------------------------------------------------------------------ 7 总控
    private void DrawGlobal(Configuration config)
    {
        var showDtr = config.ShowDtrEntry;
        if (ImGui.Checkbox("在服务器信息栏显示状态", ref showDtr))
        {
            config.ShowDtrEntry = showDtr;
            this.plugin.SyncDtrEntry();
            config.Save();
        }

        var verbose = config.VerboseLogging;
        if (ImGui.Checkbox("详细日志", ref verbose))
        {
            config.VerboseLogging = verbose;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("拟人化操作间隔（毫秒）");

        var min = config.ActionDelayMinMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("最小间隔", ref min, 50, 5000))
        {
            config.ActionDelayMinMs = min;
            if (config.ActionDelayMaxMs < min)
            {
                config.ActionDelayMaxMs = min + 50;
            }

            config.Save();
        }

        var max = config.ActionDelayMaxMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("最大间隔", ref max, 60, 8000))
        {
            config.ActionDelayMaxMs = Math.Max(max, config.ActionDelayMinMs + 1);
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("紧急停止热键");

        var vk = config.PanicHotkey;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("虚拟键码", ref vk, 1, 10))
        {
            config.PanicHotkey = Math.Clamp(vk, 1, 254);
            config.Save();
        }

        ImGui.TextDisabled("常见值：F12=123，F11=122，F10=121，END=35");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("关于");
        ImGui.TextWrapped(
            "已实现：藏宝图等级选择、市场布告板导航（vnavmesh + Lifestream）、垃圾丢弃、总控面板。" +
            "寻路挖宝（阶段 3）、自动战斗（阶段 4）与副本（阶段 5）尚未实现。");
    }
}
