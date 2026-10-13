using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace AutoTreasure.Automation;

public enum AutomationState
{
    Idle,
    CheckingStock,
    ReadingMaps,
    DiscardingJunk,
    Waiting,
    Paused,
    EmergencyStopped,
}

/// <summary>
/// Stage 7 总控: owns the loop, the panic hotkey and the per-tick state machine.
/// Stages 3/4/5 (pathing, combat, dungeon) are intentionally not implemented here;
/// they are the extension points described in docs/ARCHITECTURE.md.
/// </summary>
public sealed class AutomationController : IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly InventoryScanner inventory;
    private readonly ActivityLog activity;
    private readonly DiscardPolicy discardPolicy;
    private readonly DiscardAction discardAction;

    private bool panicKeyWasDown;
    private DateTime nextActionAt = DateTime.MinValue;
    private readonly Random random = new();

    public AutomationController(Plugin plugin, InventoryScanner inventory, ActivityLog activity)
    {
        this.plugin = plugin;
        this.configuration = plugin.Configuration;
        this.inventory = inventory;
        this.activity = activity;
        this.discardPolicy = new DiscardPolicy(this.configuration);
        this.discardAction = new DiscardAction(this.configuration, this.activity);

        Plugin.Framework.Update += this.OnFrameworkUpdate;
    }

    public AutomationState State { get; private set; } = AutomationState.Idle;

    public string StateText => this.State switch
    {
        AutomationState.Idle => "空闲",
        AutomationState.CheckingStock => "检查库存",
        AutomationState.ReadingMaps => "读图中",
        AutomationState.DiscardingJunk => "清理垃圾",
        AutomationState.Waiting => "等待中",
        AutomationState.Paused => "已暂停",
        AutomationState.EmergencyStopped => "紧急停止",
        _ => this.State.ToString(),
    };

    public DiscardAction Discarder => this.discardAction;

    // ------------------------------------------------------------------ control

    public void Start()
    {
        if (this.configuration.EmergencyStop)
        {
            this.activity.Warn("处于紧急停止状态，请先在设置中解除。");
            return;
        }

        if (!this.CanRun(out var reason))
        {
            this.activity.Warn($"无法启动：{reason}");
            return;
        }

        this.configuration.AutomationEnabled = true;
        this.State = AutomationState.CheckingStock;
        this.activity.Success("自动循环已启动。");
        Plugin.Chat.Print("[挖宝] 自动循环已启动。");
    }

    public void Stop()
    {
        this.configuration.AutomationEnabled = false;
        this.State = AutomationState.Idle;
        this.activity.Add("自动循环已停止。");
        Plugin.Chat.Print("[挖宝] 自动循环已停止。");
    }

    public void EmergencyStop()
    {
        this.configuration.AutomationEnabled = false;
        this.configuration.EmergencyStop = true;
        this.State = AutomationState.EmergencyStopped;
        this.activity.Error("紧急停止已触发！所有自动操作已中止。");
        Plugin.Chat.PrintError("[挖宝] 紧急停止已触发，请在设置中解除。");
        this.configuration.Save();
    }

    public void ClearEmergencyStop()
    {
        this.configuration.EmergencyStop = false;
        this.State = this.configuration.AutomationEnabled ? AutomationState.CheckingStock : AutomationState.Idle;
        this.activity.Success("已解除紧急停止。");
        this.configuration.Save();
    }

    private bool CanRun(out string reason)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            reason = "尚未进入游戏。";
            return false;
        }

        if (Plugin.ObjectTable.LocalPlayer is null)
        {
            reason = "角色尚未加载完成。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // ------------------------------------------------------------------ ticking

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // Advance any market-board travel regardless of the automation switch,
            // because the user can start it manually from the UI or /at mb.
            this.plugin.MarketBoard?.Tick();

            this.CheckPanicHotkey();

            if (!this.configuration.AutomationEnabled || this.configuration.EmergencyStop)
            {
                this.plugin.UpdateDtr(this.configuration.EmergencyStop ? "挖宝: 急停" : "挖宝: 关");
                return;
            }

            if (!this.CanRun(out _))
            {
                this.State = AutomationState.Waiting;
                this.plugin.UpdateDtr("挖宝: 等待");
                return;
            }

            // Human-like pacing: do one unit of work, then wait a random interval.
            if (DateTime.UtcNow < this.nextActionAt)
            {
                return;
            }

            this.Step();

            var delay = this.random.Next(
                Math.Max(50, this.configuration.ActionDelayMinMs),
                Math.Max(51, this.configuration.ActionDelayMaxMs));

            this.nextActionAt = DateTime.UtcNow.AddMilliseconds(delay);
        }
        catch (Exception ex)
        {
            this.activity.Error($"循环异常：{ex.Message}");
            Plugin.Log.Error(ex, "自动挖宝循环发生异常");
        }
    }

    private void CheckPanicHotkey()
    {
        var vk = (VirtualKey)this.configuration.PanicHotkey;

        bool down;
        try
        {
            down = Plugin.KeyState[vk];
        }
        catch
        {
            return;
        }

        if (down && !this.panicKeyWasDown)
        {
            this.EmergencyStop();
        }

        this.panicKeyWasDown = down;
    }

    /// <summary>One unit of automation work per invocation.</summary>
    private void Step()
    {
        switch (this.State)
        {
            case AutomationState.CheckingStock:
                this.StepCheckStock();
                break;

            case AutomationState.ReadingMaps:
                this.StepReadMaps();
                break;

            case AutomationState.DiscardingJunk:
                this.StepDiscardJunk();
                break;

            default:
                this.State = AutomationState.CheckingStock;
                break;
        }

        this.plugin.UpdateDtr($"挖宝: {this.StateText}");
    }

    // ------------------------------------------------------------- 1 买图阶段

    private void StepCheckStock()
    {
        var gil = this.inventory.GetGil();
        var stock = this.inventory.TotalMapStock();

        if (gil >= 0 && gil < this.configuration.MinimumGilReserve)
        {
            this.activity.Warn($"金币 {gil:N0} 低于保留线 {this.configuration.MinimumGilReserve:N0}，暂停买图。");
            this.State = AutomationState.DiscardingJunk;
            return;
        }

        if (stock >= this.configuration.DesiredMapStock)
        {
            this.activity.Add($"地图库存 {stock} 张，已满足目标 {this.configuration.DesiredMapStock} 张。");
            this.State = AutomationState.DiscardingJunk;
            return;
        }

        if (this.configuration.MapItemIds.Count == 0)
        {
            this.activity.Warn("尚未配置地图物品 ID，无法买图。请在设置中添加。");
            this.State = AutomationState.Paused;
            return;
        }

        // Buying requires board interaction (stage 1 NPC travel), which is not yet
        // implemented. Surface the gap rather than silently looping.
        this.activity.Warn($"需要补充地图（{stock}/{this.configuration.DesiredMapStock}），但自动买图尚未接入。");
        this.State = AutomationState.DiscardingJunk;
    }

    // ------------------------------------------------------------- 2 读图阶段

    /// <summary>
    /// Stage 2. The mindmap's flow is: read ONE map to reveal a coordinate, then
    /// keep the remaining two split across the player's bags and the chocobo
    /// saddlebag. This step reports and enforces that split, and reports what the
    /// next map in the queue would be.
    /// </summary>
    private void StepReadMaps()
    {
        var inBags = this.inventory.CountMapsInPlayerBags();
        var inSaddle = this.inventory.CountMapsInSaddlebag();
        var total = inBags + inSaddle;

        if (total == 0)
        {
            this.activity.Warn("没有可用地图，回到买图阶段。");
            this.State = AutomationState.CheckingStock;
            return;
        }

        // Enforce the configured split. This is a *report*, because actually moving
        // items between containers needs the game's inventory move call, which is
        // not wired yet; we never silently pretend a move happened.
        var bagsOver = inBags > this.configuration.MaxMapsInPlayerBags;
        var saddleOver = inSaddle > this.configuration.MaxMapsInSaddlebag;

        if (bagsOver || saddleOver)
        {
            this.activity.Warn(
                $"地图分布不符合配置：背包 {inBags}/{this.configuration.MaxMapsInPlayerBags}，" +
                $"鸟包 {inSaddle}/{this.configuration.MaxMapsInSaddlebag}（自动分仓尚未接入）。");
        }
        else
        {
            this.activity.Add($"地图排队：背包 {inBags} 张 / 鸟包 {inSaddle} 张。");
        }

        if (!this.configuration.AutoDecipherMaps)
        {
            this.activity.Add("未开启自动读图，跳过。");
            this.State = AutomationState.DiscardingJunk;
            return;
        }

        // Reading a map requires invoking the game's "use item" path, which is not
        // implemented yet. Report it explicitly instead of looping silently.
        this.activity.Warn("自动读图尚未接入（需调用游戏使用物品逻辑），本张跳过。");
        this.State = AutomationState.DiscardingJunk;
    }

    // --------------------------------------------------------- 6 丢弃小垃圾

    private void StepDiscardJunk()
    {
        if (!this.configuration.AutoDiscardEnabled)
        {
            this.CompleteCycle();
            return;
        }

        var free = this.inventory.CountFreePlayerSlots();
        var shouldClean = free <= this.configuration.FreeSlotThreshold || this.configuration.DiscardEveryCycle;

        if (!shouldClean)
        {
            this.activity.Add($"背包剩余 {free} 格，无需清理。");
            this.CompleteCycle();
            return;
        }

        var work = this.discardPolicy.BuildWorkList();
        if (work.Count == 0)
        {
            this.activity.Add("清理检查完成，没有命中规则的垃圾。");
            this.CompleteCycle();
            return;
        }

        var removed = this.discardAction.DiscardAll(work);
        if (removed > 0)
        {
            this.activity.Success($"已丢弃 {removed} 件物品。");
            this.configuration.Save();
        }

        this.CompleteCycle();
    }

    private void CompleteCycle()
    {
        this.configuration.TotalRunsCompleted++;
        this.configuration.LastRunUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        this.configuration.Save();

        this.State = AutomationState.CheckingStock;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= this.OnFrameworkUpdate;
    }
}
