using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoTreasure.Windows;

/// <summary>
/// Settings window covering stages 1, 2, 6 and the stage-7 safety options.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string mapIdInput = string.Empty;
    private string blacklistInput = string.Empty;
    private string whitelistInput = string.Empty;

    public ConfigWindow(Plugin plugin)
        : base("自动挖宝设置###AutoTreasureConfig")
    {
        this.plugin = plugin;

        this.Size = new Vector2(600, 620);
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
            if (ImGui.BeginTabItem("买图 / 读图"))
            {
                this.DrawMapsAndBuying(config);
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

    // ------------------------------------------------------------- 1 / 2 买图读图
    private void DrawMapsAndBuying(Configuration config)
    {
        ImGui.TextDisabled("地图物品（用于统计库存与队列）");
        ImGui.Separator();

        for (var i = config.MapItemIds.Count - 1; i >= 0; i--)
        {
            var id = config.MapItemIds[i];
            ImGui.PushID($"map{i}");
            if (ImGui.Button("移除"))
            {
                config.MapItemIds.RemoveAt(i);
                config.Save();
            }

            ImGui.SameLine();
            ImGui.Text($"{InventoryScanner.GetItemName(id)}（ID {id}）");
            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("##newmap", ref this.mapIdInput, 12);
        ImGui.SameLine();
        if (ImGui.Button("添加地图 ID") && uint.TryParse(this.mapIdInput.Trim(), out var newMapId) && newMapId > 0)
        {
            if (!config.MapItemIds.Contains(newMapId))
            {
                config.MapItemIds.Add(newMapId);
                config.Save();
                this.plugin.Activity.Success($"已添加地图：{InventoryScanner.GetItemName(newMapId)}");
            }

            this.mapIdInput = string.Empty;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("买图阶段");

        var desired = config.DesiredMapStock;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("目标库存（张）", ref desired, 1, 99))
        {
            config.DesiredMapStock = desired;
            config.Save();
        }

        // ImGui takes ref int, so bridge through an intermediate value.
        var reserve = (int)Math.Clamp(config.MinimumGilReserve, 0, int.MaxValue);
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt("金币保留线", ref reserve, 10000, 100000))
        {
            config.MinimumGilReserve = Math.Max(0, reserve);
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("读图阶段");

        var autoRead = config.AutoDecipherMaps;
        if (ImGui.Checkbox("自动读图", ref autoRead))
        {
            config.AutoDecipherMaps = autoRead;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(尚未接入)");

        var bags = config.MaxMapsInPlayerBags;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("背包保留张数", ref bags, 0, 4))
        {
            config.MaxMapsInPlayerBags = bags;
            config.Save();
        }

        var saddle = config.MaxMapsInSaddlebag;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("鸟包保留张数", ref saddle, 0, 4))
        {
            config.MaxMapsInSaddlebag = saddle;
            config.Save();
        }
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

        ImGui.TextDisabled(this.plugin.Automation.Discarder.IsAvailable
            ? "丢弃执行：已接入"
            : "丢弃执行：未接入 — 目前只做判定并在日志中列出，不会真正删除物品");
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

        // ------------------------------------------------------- 黑名单 / 白名单
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

    private void DrawIdList(System.Collections.Generic.List<uint> list, ref string input, string id)
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
        ImGui.BeginChild($"##scroll{id}", new Vector2(0, 240), true);

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
            "本插件实现思维导图的阶段 1 / 2 / 6 / 7。阶段 3（寻路）、4（战斗）、5（副本）" +
            "尚未实现，界面与日志会明确提示，不会假装完成。");
    }
}
