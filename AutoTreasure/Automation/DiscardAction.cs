using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoTreasure.Automation;

/// <summary>
/// Stage 6 execution: actually removes an item from the player's inventory.
///
/// IMPORTANT — this is the one place the plugin reaches past the public Dalamud API.
/// <c>IGameInventory</c> exposes only reads and change events; there is no "discard"
/// method. Discarding is therefore done through FFXIVClientStructs, calling the
/// game's own <c>InventoryManager.DiscardItem</c>.
///
/// Verified signature (FFXIVClientStructs):
///     public static partial InventoryManager* Instance();
///     public partial int DiscardItem(InventoryType container, ushort slot);
///
/// FFXIVClientStructs' <c>InventoryType</c> numeric values match Dalamud's
/// <c>GameInventoryType</c> exactly (Inventory1=0..Inventory4=3,
/// SaddleBag1=4000, SaddleBag2=4001, PremiumSaddleBag1=4100, PremiumSaddleBag2=4101),
/// so the container value can be cast directly.
/// </summary>
public sealed class DiscardAction
{
    private readonly ActivityLog activity;
    private readonly Configuration configuration;

    /// <summary>Safety cap per cleanup pass, so a bad rule cannot empty the bags.</summary>
    private const int MaxDiscardsPerPass = 20;

    public DiscardAction(Configuration configuration, ActivityLog activity)
    {
        this.configuration = configuration;
        this.activity = activity;
    }

    /// <summary>True when the game-side discard call resolved for this build.</summary>
    public bool IsAvailable => true;

    public string UnavailableReason => string.Empty;

    /// <summary>
    /// Attempt to discard everything in the work list.
    /// Returns how many items were actually discarded.
    /// </summary>
    public int DiscardAll(IReadOnlyList<GameInventoryItem> workList)
    {
        if (workList.Count == 0)
        {
            return 0;
        }

        var discarded = 0;
        foreach (var item in workList)
        {
            if (discarded >= MaxDiscardsPerPass)
            {
                this.activity.Warn($"本轮已达单次丢弃上限 {MaxDiscardsPerPass} 件，剩余留到下一轮。");
                break;
            }

            if (this.TryDiscard(item))
            {
                discarded++;
                this.configuration.TotalItemsDiscarded++;
            }
            else
            {
                this.activity.Error($"丢弃失败：{InventoryScanner.GetItemName(item.BaseItemId)}（槽位 {item.InventorySlot}）");
            }
        }

        return discarded;
    }

    /// <summary>
    /// Performs the discard for a single slot via the game's InventoryManager.
    /// </summary>
    private unsafe bool TryDiscard(GameInventoryItem item)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                this.activity.Error("无法获取 InventoryManager 实例。");
                return false;
            }

            // The container ids are numerically identical between the two enums.
            var container = (InventoryType)(uint)item.ContainerType;
            var slot = (ushort)item.InventorySlot;

            var result = manager->DiscardItem(container, slot);

            // The game returns a non-negative value on success.
            return result >= 0;
        }
        catch (Exception ex)
        {
            this.activity.Error($"调用丢弃失败：{ex.Message}");
            return false;
        }
    }
}
