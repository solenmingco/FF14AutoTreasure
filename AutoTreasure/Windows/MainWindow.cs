using System;
using System.Numerics;
using AutoTreasure.Automation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoTreasure.Windows;

/// <summary>
/// Main control window: stage 7 总控 + live activity log + profit stats.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("FF14 自动挖宝助手###AutoTreasureMain")
    {
        this.plugin = plugin;

        this.Size = new Vector2(560, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = this.plugin.Configuration;
        var automation = this.plugin.Automation;

        // ------------------------------------------------------- 状态栏
        if (ImGui.BeginTable("##status", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("状态：");
            ImGui.TableNextColumn();

            var color = automation.State switch
            {
                AutomationState.EmergencyStopped => new Vector4(1f, 0.35f, 0.35f, 1f),
                AutomationState.Paused => new Vector4(1f, 0.75f, 0.3f, 1f),
                AutomationState.Idle => new Vector4(0.7f, 0.7f, 0.7f, 1f),
                _ => new Vector4(0.4f, 0.9f, 0.5f, 1f),
            };

            ImGui.TextColored(color, automation.StateText);
            ImGui.EndTable();
        }

        // ------------------------------------------------------- 控制按钮
        var running = config.AutomationEnabled && !config.EmergencyStop;

        if (!running)
        {
            if (ImGui.Button("开始循环", new Vector2(110, 0)))
            {
                automation.Start();
            }
        }
        else
        {
            if (ImGui.Button("停止循环", new Vector2(110, 0)))
            {
                automation.Stop();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("紧急停止", new Vector2(110, 0)))
        {
            automation.EmergencyStop();
        }

        if (config.EmergencyStop)
        {
            ImGui.SameLine();
            if (ImGui.Button("解除急停", new Vector2(110, 0)))
            {
                automation.ClearEmergencyStop();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("设置", new Vector2(80, 0)))
        {
            this.plugin.ToggleConfigUi();
        }

        ImGui.Separator();

        // ------------------------------------------------------- 地图与导航
        if (ImGui.CollapsingHeader("市场布告板导航", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var mb = this.plugin.MarketBoard;

            ImGui.Text("目标主城：");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f),
                config.SelectedMarketCityName ?? "（未选择）");

            if (ImGui.Button("前往市场布告板", new Vector2(170, 0)))
            {
                this.plugin.Activity.Add(this.plugin.TravelToMarketBoard());
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(80, 0)))
            {
                mb?.Cancel();
            }

            ImGui.TextDisabled($"vnavmesh：{(this.plugin.Navmesh.IsAvailable ? "可用" : "不可用")}   " +
                               $"Lifestream：{(this.plugin.Lifestream.IsAvailable ? "可用" : "不可用")}");

            if (mb is not null)
            {
                ImGui.Text($"状态：{mb.Phase}   {mb.GetAvailabilityText()}");
            }
        }

        // ------------------------------------------------------- 库存概览
        if (ImGui.CollapsingHeader("库存与金币", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var gil = this.plugin.Inventory.GetGil();
            var inBags = this.plugin.Inventory.CountMapsInPlayerBags();
            var inSaddle = this.plugin.Inventory.CountMapsInSaddlebag();
            var free = this.plugin.Inventory.CountFreePlayerSlots();

            ImGui.Text($"金币：{(gil >= 0 ? gil.ToString("N0") : "未知")}");
            ImGui.Text($"地图：背包 {inBags} 张 / 陆行鸟背包 {inSaddle} 张");
            ImGui.Text($"背包剩余空间：{free} 格");

            var gradeCount = this.plugin.Catalog.MapGrades.Count;
            ImGui.Text($"已识别的藏宝图等级：{gradeCount} 个（已选 {config.MapItemIds.Count} 个）");

            ImGui.SameLine();
            if (ImGui.Button("刷新"))
            {
                this.plugin.Catalog.Invalidate();
                this.plugin.Activity.Add("已刷新库存与游戏数据。");
            }
        }

        // ------------------------------------------------------- 收益统计
        if (ImGui.CollapsingHeader("收益统计"))
        {
            ImGui.Text($"累计读图：{config.TotalMapsDeciphered} 次");
            ImGui.Text($"完成循环：{config.TotalRunsCompleted} 轮");
            ImGui.Text($"丢弃物品：{config.TotalItemsDiscarded} 件");

            if (config.LastRunUnixSeconds > 0)
            {
                var last = DateTimeOffset.FromUnixTimeSeconds(config.LastRunUnixSeconds).LocalDateTime;
                ImGui.Text($"上次完成：{last:yyyy-MM-dd HH:mm:ss}");
            }

            if (ImGui.Button("重置统计"))
            {
                config.TotalMapsDeciphered = 0;
                config.TotalRunsCompleted = 0;
                config.TotalItemsDiscarded = 0;
                config.LastRunUnixSeconds = 0;
                config.Save();
                this.plugin.Activity.Add("统计已重置。");
            }
        }

        // ------------------------------------------------------- 日志
        if (ImGui.CollapsingHeader("运行日志", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.Button("清空日志"))
            {
                this.plugin.Activity.Clear();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"共 {this.plugin.Activity.Entries.Count} 条");

            ImGui.BeginChild("##log", new Vector2(0, 200), true);
            foreach (var entry in this.plugin.Activity.Entries)
            {
                var color = entry.Level switch
                {
                    ActivityLevel.Success => new Vector4(0.45f, 0.9f, 0.5f, 1f),
                    ActivityLevel.Warning => new Vector4(1f, 0.8f, 0.35f, 1f),
                    ActivityLevel.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
                    _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
                };

                ImGui.TextColored(color, $"[{entry.Time:HH:mm:ss}] {entry.Message}");
            }

            // Auto-scroll to the newest entry.
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10f)
            {
                ImGui.SetScrollHereY(1f);
            }

            ImGui.EndChild();
        }
    }
}
