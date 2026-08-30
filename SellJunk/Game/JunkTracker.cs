using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using SellJunk.Data;

namespace SellJunk.Game;

/// <summary>One optimizer row: a category and everything currently in it.</summary>
internal sealed class CategoryBucket(CategoryInfo info)
{
    public CategoryInfo Info { get; } = info;
    public List<JunkStack> Items { get; } = [];
    public long Value { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>
/// Keeps a cached, category-grouped view of the inventory.
///
/// This exists because of a threading split: inventory lives in game memory and may only be
/// read on the framework thread, while ImGui draws on the render thread. So the scan happens
/// on tick and the windows render the snapshot it leaves behind.
/// </summary>
internal sealed class JunkTracker(Configuration cfg, JunkIndex index)
{
    /// <summary>Roughly once a second. The scan is cheap, but it is not free.</summary>
    private const int RefreshIntervalTicks = 60;

    private int _ticksSinceRefresh = RefreshIntervalTicks;

    /// <summary>Category buckets over the player's own containers, in display order.</summary>
    public IReadOnlyList<CategoryBucket> Buckets { get; private set; } = [];

    /// <summary>Distinct stacks that at least one ENABLED sell-able category caught.</summary>
    public IReadOnlyList<JunkStack> Stageable { get; private set; } = [];

    public long StageableValue { get; private set; }

    /// <summary>The same category breakdown over the retainer that is currently open.</summary>
    public IReadOnlyList<CategoryBucket> RetainerBuckets { get; private set; } = [];

    public IReadOnlyList<JunkStack> Retainer { get; private set; } = [];
    public long RetainerValue { get; private set; }
    public bool RetainerWasOpen { get; private set; }

    // Sampled every tick so the windows never have to touch game memory themselves.
    public bool ShopOpen { get; private set; }
    public int FreeBagSlots { get; private set; }

    /// <summary>Screen rect of the retainer window to dock against, sampled on the framework thread.</summary>
    public bool HasRetainerAnchor { get; private set; }

    public Vector2 RetainerAnchorPos { get; private set; }
    public Vector2 RetainerAnchorSize { get; private set; }

    /// <summary>Framework thread only.</summary>
    public void Tick()
    {
        ShopOpen = GameActions.ShopIsOpen();
        FreeBagSlots = GameActions.FreeBagSlots();

        // Sampled every tick, not on refresh: the retainer window can be dragged, and the docked
        // panel has to follow it without a second of lag.
        if (GameActions.TryGetRetainerAnchor(out var anchorPos, out var anchorSize))
        {
            HasRetainerAnchor = true;
            RetainerAnchorPos = anchorPos;
            RetainerAnchorSize = anchorSize;
        }
        else
        {
            HasRetainerAnchor = false;
        }

        if (++_ticksSinceRefresh < RefreshIntervalTicks)
            return;

        _ticksSinceRefresh = 0;
        Refresh();
    }

    /// <summary>Framework thread only.</summary>
    public void Refresh()
    {
        if (!index.Ready || !Services.ClientState.IsLoggedIn)
        {
            Clear();
            return;
        }

        RetainerWasOpen = GameActions.RetainerOpen();

        var playerContainers = InventoryScanner.PlayerContainers(cfg);

        // One duplication map across both sides, so "the same item is in your bags AND your
        // retainer" is detected - which is the case actually worth consolidating.
        var allContainers = new List<InventoryType>(playerContainers);
        if (RetainerWasOpen)
            allContainers.AddRange(InventoryScanner.RetainerBags);
        var containerMap = InventoryScanner.BuildContainerMap(allContainers);

        var enabledSellMask = EnabledSellMask();

        var scanned = InventoryScanner.Scan(playerContainers, cfg, index, containerMap);
        Buckets = Bucketise(scanned);
        Stageable = FilterStageable(scanned, enabledSellMask, out var total);
        StageableValue = total;

        if (RetainerWasOpen)
        {
            var retainer = InventoryScanner.Scan(InventoryScanner.RetainerBags, cfg, index, containerMap);
            RetainerBuckets = Bucketise(retainer);
            Retainer = FilterStageable(retainer, enabledSellMask, out var retainerTotal);
            RetainerValue = retainerTotal;
        }
        else
        {
            // Retainer containers are not populated once the session ends, so holding the
            // old list would just be showing stale data as if it were live.
            RetainerBuckets = [];
            Retainer = [];
            RetainerValue = 0;
        }
    }

    private void Clear()
    {
        Buckets = [];
        Stageable = [];
        StageableValue = 0;
        RetainerBuckets = [];
        Retainer = [];
        RetainerValue = 0;
    }

    /// <summary>A stack matched by two enabled categories must still only be acted on once.</summary>
    private static List<JunkStack> FilterStageable(List<JunkStack> scanned, JunkCategory mask, out long total)
    {
        var kept = new List<JunkStack>();
        total = 0;

        foreach (var stack in scanned)
        {
            if ((stack.Categories & mask) == 0)
                continue;
            kept.Add(stack);
            total += stack.StackValue;
        }

        return kept;
    }

    private JunkCategory EnabledSellMask()
    {
        var mask = JunkCategory.None;
        foreach (var info in JunkCategories.All)
        {
            if (info.Sellable && cfg.IsEnabled(info))
                mask |= info.Category;
        }

        return mask;
    }

    private List<CategoryBucket> Bucketise(List<JunkStack> scanned)
    {
        var buckets = new List<CategoryBucket>(JunkCategories.All.Count);
        foreach (var info in JunkCategories.All)
            buckets.Add(new CategoryBucket(info) { Enabled = cfg.IsEnabled(info) });

        foreach (var stack in scanned)
        {
            foreach (var bucket in buckets)
            {
                if (!stack.Categories.HasFlag(bucket.Info.Category))
                    continue;

                bucket.Items.Add(stack);
                bucket.Value += stack.StackValue;
            }
        }

        return buckets;
    }
}
