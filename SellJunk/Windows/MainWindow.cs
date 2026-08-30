using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SellJunk.Data;
using SellJunk.Game;

namespace SellJunk.Windows;

/// <summary>
/// The optimizer panel: one collapsible row per category, each with its own count, its own
/// parameter, and its own on/off toggle. Enabled sell-able categories combine with OR - a stack
/// is staged if any of them catches it.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Accent = new(0.45f, 0.78f, 1f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.85f, 0.4f, 1f);

    private readonly Configuration _cfg;
    private readonly JunkTracker _tracker;
    private readonly ShopSeller _seller;
    private readonly RetainerRetriever _retriever;
    private readonly JunkIndex _index;

    /// <summary>
    /// Set on the render thread, consumed on the framework thread. Volatile so the poll on the
    /// other side cannot be hoisted or see a stale value.
    /// </summary>
    public volatile bool SellRequested;

    public volatile bool RetrieveRequested;
    public volatile bool RefreshRequested;
    public volatile bool SettingsChanged;
    public volatile uint BlacklistRequest;

    public MainWindow(Configuration cfg, JunkTracker tracker, ShopSeller seller, RetainerRetriever retriever, JunkIndex index)
        : base("SellJunk###SellJunkMain")
    {
        _cfg = cfg;
        _tracker = tracker;
        _seller = seller;
        _retriever = retriever;
        _index = index;

        SizeConstraints = new WindowSizeConstraints
        {
            // Min width has to stay under the docked width below, or the constraint would
            // override it and the panel would overhang the retainer window.
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(1800, 1400),
        };
    }

    /// <summary>Gap between the retainer window and this one, in unscaled pixels.</summary>
    private const float DockGap = 6f;

    /// <summary>Set on the framework thread when a retainer opens, consumed once by Draw.</summary>
    public volatile bool FocusRetainerTab;

    private bool _pendingRetainerFocus;

    /// <summary>
    /// Dock to the top-right of whatever retainer window is up, so this sits opposite the
    /// left-hand side AutoRetainer occupies rather than covering it.
    ///
    /// The rect comes from a framework-thread snapshot - PreDraw runs on the render thread, where
    /// walking addon memory would be a crash risk.
    /// </summary>
    public override void PreDraw()
    {
        const ImGuiWindowFlags pinned = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        if (!_cfg.DockToRetainerWindow || !_tracker.HasRetainerAnchor)
        {
            // Hand control back the moment the retainer window goes away.
            Flags &= ~pinned;
            return;
        }

        var anchor = _tracker.RetainerAnchorPos;
        var size = _tracker.RetainerAnchorSize;
        var scale = ImGuiHelpers.GlobalScale;
        var width = 640f * scale;

        var pos = new Vector2(anchor.X + size.X + DockGap * scale, anchor.Y);

        // Keep it on screen if the retainer window sits far right - flip to its left side
        // rather than sliding off the edge.
        var viewport = ImGui.GetMainViewport();
        if (pos.X + width > viewport.Pos.X + viewport.Size.X)
            pos.X = anchor.X - width - DockGap * scale;

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, System.Math.Max(size.Y, 420f * scale)), ImGuiCond.Always);

        // Without this the window visibly snaps back on every drag attempt, which reads as a bug.
        Flags |= pinned;
    }

    public override void Draw()
    {
        if (!_index.Ready)
        {
            ImGui.TextUnformatted("Building the item index from game data...");
            return;
        }

        if (FocusRetainerTab)
        {
            FocusRetainerTab = false;
            _pendingRetainerFocus = true;
        }

        DrawRunnerBanner();

        using var tabs = ImRaii.TabBar("##selljunktabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem($"Optimizer ({_tracker.Stageable.Count})###optimizer"))
        {
            if (tab)
                DrawOptimizer();
        }

        var retainerFlags = ImGuiTabItemFlags.None;
        if (_pendingRetainerFocus)
        {
            _pendingRetainerFocus = false;
            retainerFlags = ImGuiTabItemFlags.SetSelected;
        }

        using (var tab = ImRaii.TabItem($"Retainer ({_tracker.Retainer.Count})###retainer", retainerFlags))
        {
            if (tab)
                DrawRetainer();
        }
    }

    private void DrawRunnerBanner()
    {
        if (!_seller.Running && !_retriever.Running)
            return;

        var active = _seller.Running ? _seller : (SlotActionRunner)_retriever;
        ImGui.TextColored(Accent, active.Status);
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            _seller.Stop("stopped by user");
            _retriever.Stop("stopped by user");
        }

        ImGui.Separator();
    }

    // ------------------------------------------------------------- optimizer

    private void DrawOptimizer()
    {
        var shopOpen = _tracker.ShopOpen;
        var count = _tracker.Stageable.Count;

        ImGui.Spacing();
        using (ImRaii.Disabled(!shopOpen || count == 0 || _seller.Running || _retriever.Running))
        {
            if (ImGui.Button($"Review {count} stacks  (~{_tracker.StageableValue:N0} gil)", new Vector2(320, 0)))
                SellRequested = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens a confirmation pane listing everything that would be sold.\nNothing is sold until you confirm there.");

        if (!shopOpen)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("- open a vendor to enable");
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            RefreshRequested = true;

        ImGui.SameLine();
        ImGui.TextDisabled($"| {count} stacks staged from {CountEnabledSellCategories()} enabled categories");

        ImGui.Separator();
        ImGui.Spacing();

        foreach (var bucket in _tracker.Buckets)
            DrawCategoryRow(bucket, "bags");
    }

    private int CountEnabledSellCategories()
    {
        var n = 0;
        foreach (var info in JunkCategories.All)
        {
            if (info.Sellable && _cfg.IsEnabled(info))
                n++;
        }

        return n;
    }

    /// <summary>
    /// <paramref name="scope"/> keeps the bags and retainer copies of the same category from
    /// sharing one expand/collapse state.
    /// </summary>
    private void DrawCategoryRow(CategoryBucket bucket, string scope)
    {
        var info = bucket.Info;
        var enabled = _cfg.IsEnabled(info);

        using var id = ImRaii.PushId($"{scope}:{(int)info.Category}");

        // Disabled and advisory rows are dimmed so the active sell set reads at a glance.
        using (ImRaii.PushColor(ImGuiCol.Text, Muted, !enabled || !info.Sellable))
        {
            var open = ImGui.TreeNodeEx($"{info.Name} ({bucket.Items.Count})###header",
                ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(info.Tooltip);

            DrawRowControls(bucket, enabled);

            if (open)
            {
                DrawCategoryBody(bucket);
                ImGui.TreePop();
            }
        }
    }

    /// <summary>The right-hand side of a category header: its parameter and its on/off toggle.</summary>
    private void DrawRowControls(CategoryBucket bucket, bool enabled)
    {
        var info = bucket.Info;
        var toggleWidth = 90f;
        var paramWidth = info.Param == CategoryParam.None ? 0f : 230f;
        var right = ImGui.GetWindowContentRegionMax().X;

        if (info.Param != CategoryParam.None)
        {
            ImGui.SameLine(right - toggleWidth - paramWidth);
            ImGui.TextUnformatted(info.ParamLabel);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            DrawParam(info.Param);
        }

        ImGui.SameLine(right - toggleWidth);

        if (!info.Sellable)
        {
            ImGui.TextDisabled("advisory");
            return;
        }

        var on = enabled;
        if (ImGui.Checkbox("##enabled", ref on))
        {
            _cfg.SetEnabled(info.Category, on);
            SettingsChanged = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(on ? "Enabled - contributes to the sale." : "Disabled - listed but never staged.");
    }

    private void DrawParam(CategoryParam param)
    {
        switch (param)
        {
            case CategoryParam.MaxVendorPrice:
            {
                var value = _cfg.MaxVendorPrice;
                if (ImGui.InputInt("##param", ref value, 0, 0))
                {
                    _cfg.MaxVendorPrice = System.Math.Clamp(value, 1, 1_000_000);
                    SettingsChanged = true;
                }

                break;
            }

            case CategoryParam.NodeLevel:
            {
                var value = _cfg.MaxNodeLevel;
                if (ImGui.InputInt("##param", ref value, 0, 0))
                {
                    _cfg.MaxNodeLevel = System.Math.Clamp(value, 1, 100);
                    SettingsChanged = true;
                }

                break;
            }

            case CategoryParam.StackThreshold:
            {
                var value = _cfg.SmallStackThreshold;
                if (ImGui.InputInt("##param", ref value, 0, 0))
                {
                    _cfg.SmallStackThreshold = System.Math.Clamp(value, 1, 999);
                    SettingsChanged = true;
                }

                break;
            }

            case CategoryParam.CraftLevel:
            {
                if (_cfg.AutoDetectMaxCraftLevel)
                {
                    ImGui.TextDisabled($"{_index.DetectedMaxCraftLevel} (auto)");
                    break;
                }

                var value = _cfg.MaxCraftLevel;
                if (ImGui.InputInt("##param", ref value, 0, 0))
                {
                    _cfg.MaxCraftLevel = System.Math.Clamp(value, 1, 100);
                    SettingsChanged = true;
                }

                break;
            }
        }
    }

    private void DrawCategoryBody(CategoryBucket bucket)
    {
        if (bucket.Info.AdvisoryNote is { } note)
            ImGui.TextColored(Warn, note);

        if (bucket.Items.Count == 0)
        {
            ImGui.TextDisabled("Nothing here.");
            return;
        }

        if (bucket.Info.Sellable)
            ImGui.TextDisabled($"Worth about {bucket.Value:N0} gil.");

        DrawTable($"##cat{(int)bucket.Info.Category}", bucket.Items, bucket.Info.Sellable);
    }

    // -------------------------------------------------------------- retainer

    private void DrawRetainer()
    {
        if (!_tracker.RetainerWasOpen)
        {
            ImGui.TextWrapped(
                "Open a retainer at a summoning bell to see what junk it is holding.\n\n" +
                "Note: the game has no way to sell to an NPC straight from a retainer's bag, so " +
                "clearing retainer junk means pulling it into your inventory first and selling it " +
                "at the next vendor.");
            return;
        }

        var junk = _tracker.Retainer;
        var free = _tracker.FreeBagSlots;

        ImGui.Spacing();

        // Not gated on free slots: a stackable item can still merge into a partial stack, and
        // anything that genuinely has nowhere to go is skipped per item rather than blocked here.
        using (ImRaii.Disabled(junk.Count == 0 || _seller.Running || _retriever.Running))
        {
            if (ImGui.Button($"Review {junk.Count} stacks to retrieve", new Vector2(320, 0)))
                RetrieveRequested = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opens a confirmation pane listing everything that would be pulled\ninto your bags. Nothing moves until you confirm there.");

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            RefreshRequested = true;

        ImGui.SameLine();
        ImGui.TextDisabled(free == 0
            ? "| bags full - only stackables will fit"
            : $"| {free} free bag slots");

        ImGui.Separator();
        ImGui.TextDisabled(
            "The game cannot sell to an NPC from a retainer's bag, so these are pulled into your " +
            "inventory to sell at the next vendor.");
        ImGui.Spacing();

        // Same categories, same toggles - they are shared settings, so changing one here changes
        // it for your bags too.
        foreach (var bucket in _tracker.RetainerBuckets)
            DrawCategoryRow(bucket, "retainer");
    }

    // ----------------------------------------------------------------- table

    private void DrawTable(string id, IReadOnlyList<JunkStack> junk, bool sellable)
    {
        var height = System.Math.Min(junk.Count * 24f + 30f, 260f);

        using var table = ImRaii.Table(id, 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, height));
        if (!table)
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("You get", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Re-buy", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Also in", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("##keep", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var stack in junk)
        {
            ImGui.TableNextRow();
            using var rowId = ImRaii.PushId($"{(int)stack.Container}:{stack.Slot}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stack.IsHq ? $"{stack.Name} (HQ)" : stack.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stack.Quantity.ToString());

            ImGui.TableNextColumn();
            if (sellable)
                ImGui.TextUnformatted($"{stack.StackValue:N0}");
            else
                ImGui.TextDisabled("-");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(stack.StackRebuyCost > 0 ? $"{stack.StackRebuyCost:N0}" : "-");

            // Which OTHER categories also caught this stack - the quickest way to see why
            // something is in the sale beyond the row you are looking at.
            ImGui.TableNextColumn();
            ImGui.TextDisabled(DescribeCategories(stack.Categories));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Keep"))
                BlacklistRequest = stack.ItemId;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Never sell this item again (adds it to the keep list).");
        }
    }

    private static string DescribeCategories(JunkCategory categories)
    {
        var names = new List<string>(3);
        foreach (var info in JunkCategories.All)
        {
            if (categories.HasFlag(info.Category))
                names.Add(ShortName(info.Category));
        }

        return string.Join(", ", names);
    }

    private static string ShortName(JunkCategory category) => category switch
    {
        JunkCategory.GilBuyable => "vendor",
        JunkCategory.EasilyGathered => "gathered",
        JunkCategory.SmallStack => "small stack",
        JunkCategory.SingleRecipe => "one recipe",
        JunkCategory.NoRecipe => "no recipe",
        JunkCategory.SubMaxCraft => "sub-max craft",
        JunkCategory.Duplicated => "duplicated",
        JunkCategory.FullSpiritbond => "spiritbond",
        JunkCategory.LowerableHq => "HQ",
        _ => category.ToString(),
    };
}
