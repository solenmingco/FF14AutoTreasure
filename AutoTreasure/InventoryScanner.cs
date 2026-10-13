using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AutoTreasure;

/// <summary>
/// Read-only helpers over the player's bags and the chocobo saddlebag.
/// All lookups use <see cref="GameInventoryItem.BaseItemId"/>, because
/// <see cref="GameInventoryItem.ItemId"/> carries HQ/collectable offsets.
/// </summary>
public sealed class InventoryScanner
{
    /// <summary>Player bags 1-4.</summary>
    public static readonly GameInventoryType[] PlayerBags =
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };

    /// <summary>Chocobo saddlebag pages.</summary>
    public static readonly GameInventoryType[] Saddlebags =
    {
        GameInventoryType.SaddleBag1,
        GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1,
        GameInventoryType.PremiumSaddleBag2,
    };

    private readonly Configuration configuration;
    private readonly ActivityLog activity;

    public InventoryScanner(Configuration configuration, ActivityLog activity)
    {
        this.configuration = configuration;
        this.activity = activity;
    }

    private static IGameInventory Inventory => Plugin.GameInventory;

    /// <summary>
    /// Snapshot every non-empty item across the given containers.
    ///
    /// Returns a materialised list on purpose: <c>GetInventoryItems</c> hands back a
    /// <c>ReadOnlySpan</c>, which cannot be held across a <c>yield</c> boundary, so an
    /// iterator method over it does not compile.
    /// </summary>
    public List<GameInventoryItem> Snapshot(IEnumerable<GameInventoryType> containers)
    {
        var result = new List<GameInventoryItem>();

        foreach (var container in containers)
        {
            ReadOnlySpan<GameInventoryItem> items;
            try
            {
                items = Inventory.GetInventoryItems(container);
            }
            catch (Exception ex)
            {
                this.activity.Error($"读取容器 {container} 失败：{ex.Message}");
                continue;
            }

            foreach (var item in items)
            {
                if (!item.IsEmpty)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    /// <summary>Total quantity of an item across the given containers.</summary>
    public int CountItem(uint baseItemId, IEnumerable<GameInventoryType> containers)
    {
        var total = 0;
        foreach (var item in this.Snapshot(containers))
        {
            if (item.BaseItemId == baseItemId)
            {
                total += item.Quantity;
            }
        }

        return total;
    }

    public int CountMapsInPlayerBags() => this.CountMapsIn(PlayerBags);

    public int CountMapsInSaddlebag() => this.CountMapsIn(Saddlebags);

    private int CountMapsIn(IEnumerable<GameInventoryType> containers)
    {
        var mapIds = this.configuration.MapItemIds;
        if (mapIds.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var item in this.Snapshot(containers))
        {
            if (mapIds.Contains(item.BaseItemId))
            {
                total += item.Quantity;
            }
        }

        return total;
    }

    public int TotalMapStock() => this.CountMapsInPlayerBags() + this.CountMapsInSaddlebag();

    /// <summary>Number of empty slots across the four player bags.</summary>
    public int CountFreePlayerSlots()
    {
        var free = 0;
        foreach (var container in PlayerBags)
        {
            foreach (var item in Inventory.GetInventoryItems(container))
            {
                if (item.IsEmpty)
                {
                    free++;
                }
            }
        }

        return free;
    }

    /// <summary>Current Gil held by the player, or -1 when unavailable.</summary>
    public long GetGil()
    {
        try
        {
            foreach (var item in Inventory.GetInventoryItems(GameInventoryType.Currency))
            {
                if (!item.IsEmpty)
                {
                    return item.Quantity;
                }
            }
        }
        catch (Exception ex)
        {
            this.activity.Error($"读取金币失败：{ex.Message}");
        }

        return -1;
    }

    /// <summary>Resolve an item's display name via Lumina, falling back to the raw id.</summary>
    public static string GetItemName(uint baseItemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            var row = sheet.GetRowOrDefault(baseItemId);
            if (row is null)
            {
                return $"#{baseItemId}";
            }

            var name = row.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"#{baseItemId}" : name;
        }
        catch
        {
            return $"#{baseItemId}";
        }
    }

    /// <summary>Vendor sell price, or 0 when unknown.</summary>
    public static long GetVendorPrice(uint baseItemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            var row = sheet.GetRowOrDefault(baseItemId);
            return row is null ? 0 : row.Value.PriceMid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Item quality: 1 = white, 2 = green, 3 = blue, 4 = purple, 7 = pink.</summary>
    public static int GetRarity(uint baseItemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            var row = sheet.GetRowOrDefault(baseItemId);
            return row is null ? 0 : row.Value.Rarity;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// True when the item can be listed on the market board.
    /// Lumina's <c>Item</c> sheet has no "marketable" flag; marketability is the
    /// inverse of <c>IsUntradable</c>.
    /// </summary>
    public static bool IsMarketable(uint baseItemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            var row = sheet.GetRowOrDefault(baseItemId);
            return row is not null && !row.Value.IsUntradable;
        }
        catch
        {
            return false;
        }
    }
}
