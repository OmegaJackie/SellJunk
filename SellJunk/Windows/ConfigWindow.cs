using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SellJunk.Data;

namespace SellJunk.Windows;

/// <summary>
/// Everything that is not a per-category switch. The categories and their thresholds live on the
/// optimizer panel itself, next to their counts, which is where you actually want to tune them.
/// </summary>
internal sealed class ConfigWindow : Window
{
    private readonly Configuration _cfg;
    private readonly JunkIndex _index;

    /// <summary>
    /// Set on the render thread, consumed on the framework thread. The keep list is read by the
    /// classifier on every scan and by ShopSeller immediately before every sale, so it must never
    /// be mutated or enumerated from here - a rewrite mid-lookup could make the last-chance
    /// keep-list check miss and sell a protected item.
    /// </summary>
    public volatile bool Dirty;

    public volatile bool RebuildIndexRequested;
    public volatile bool ClearKeepListRequested;
    private uint _unkeepRequest;

    /// <summary>Framework-thread snapshot of the keep list, published by the plugin.</summary>
    public (uint Id, string Name)[] KeepListSnapshot = [];

    public ConfigWindow(Configuration cfg, JunkIndex index) : base("SellJunk settings###SellJunkConfig")
    {
        _cfg = cfg;
        _index = index;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 420),
            MaximumSize = new Vector2(1200, 1200),
        };
    }

    public uint TakeUnkeepRequest() => Interlocked.Exchange(ref _unkeepRequest, 0);

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##configtabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Global gates"))
        {
            if (tab)
                DrawGates();
        }

        using (var tab = ImRaii.TabItem("Safety"))
        {
            if (tab)
                DrawSafety();
        }

        using (var tab = ImRaii.TabItem($"Keep list ({KeepListSnapshot.Length})###keeplist"))
        {
            if (tab)
                DrawKeepList();
        }
    }

    private void DrawGates()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("These apply on top of every category. The categories themselves,");
        ImGui.TextDisabled("and their thresholds, live on the optimizer panel.");
        ImGui.Spacing();

        if (ImGui.Checkbox("Require the item to be re-buyable from a vendor", ref _cfg.RequireVendorBuyable))
            Dirty = true;
        Help("An AND-gate over everything: nothing is staged unless a vendor also stocks it cheaply,\n" +
             "so every sale stays reversible. This is the original conservative behaviour - with it\n" +
             "on, the other categories can only narrow the vendor-buyable set, never add to it.");

        if (ImGui.Checkbox("Never sell anything that feeds a max-level craft", ref _cfg.CraftChainVeto))
            Dirty = true;
        Help("A veto that outranks every category. Without it, a base mat that feeds a level-100\n" +
             "craft can still be staged when another category catches it - Iron Ore, Cotton Boll,\n" +
             "Rock Salt, Muddy Water and about 11 others. Most cost single-digit gil to re-buy.");

        ImGui.Separator();

        if (ImGui.Checkbox("Ignore timed nodes", ref _cfg.ExcludeTimedNodes))
            Dirty = true;
        Help("Applies to the 'easily gathered' category. Unspoiled, ephemeral and legendary node\n" +
             "materials never qualify, whatever their level - you cannot re-gather those on demand.");

        if (ImGui.Checkbox($"Read the craft level cap from game data (currently {_index.DetectedMaxCraftLevel})", ref _cfg.AutoDetectMaxCraftLevel))
            Dirty = true;
        Help("Keeps working when the level cap moves in a future patch.");

        if (ImGui.Checkbox("Follow the craft chain", ref _cfg.RollUpCraftChain))
        {
            Dirty = true;
            RebuildIndexRequested = true;
        }

        Help("Strongly recommended. Without it, a level-60 intermediate looks disposable even when\n" +
             "its output feeds a level-100 recipe. Muddy Water and Iron Sand are exactly this trap:\n" +
             "direct craft level 1 and 14, but both feed max-level crafts.");
    }

    private void DrawSafety()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("These apply before the categories, always.");
        ImGui.Spacing();

        if (ImGui.Checkbox("Never sell HQ", ref _cfg.SkipHq))
            Dirty = true;
        if (ImGui.Checkbox("Never sell unique items", ref _cfg.SkipUnique))
            Dirty = true;
        if (ImGui.Checkbox("Never sell collectables", ref _cfg.SkipCollectables))
            Dirty = true;
        if (ImGui.Checkbox("Never sell items that cannot be discarded", ref _cfg.SkipIndisposable))
            Dirty = true;
        if (ImGui.Checkbox("Never sell items with materia or spiritbond", ref _cfg.SkipSpiritbondOrMateria))
            Dirty = true;

        ImGui.Separator();

        if (ImGui.Checkbox("Include the armoury chest", ref _cfg.IncludeArmoryChest))
            Dirty = true;
        Help("Off by default - the armoury holds gear, which is riskier to sell than materials.");

        ImGui.Separator();

        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Ticks between actions", ref _cfg.TicksBetweenActions, 5, 120))
            Dirty = true;
        Help("About 60 ticks to a second. Lower is faster but leans harder on the server;\n" +
             "the default of 30 is roughly two items a second.");

        if (ImGui.Checkbox("Open the window automatically at a vendor", ref _cfg.AutoOpenWindowAtShop))
            Dirty = true;
        if (ImGui.Checkbox("Open the window automatically at a retainer", ref _cfg.AutoOpenWindowAtRetainer))
            Dirty = true;
        if (ImGui.Checkbox("Dock to the retainer window", ref _cfg.DockToRetainerWindow))
            Dirty = true;
        Help("Pins the window to the top-right edge of whichever retainer window is open - the\n" +
             "side opposite AutoRetainer - and follows it if you drag it. Turn this off to place\n" +
             "and size the window yourself.");
        if (ImGui.Checkbox("Print a summary to chat when a run finishes", ref _cfg.ChatSummary))
            Dirty = true;
        if (ImGui.Checkbox("Auto-retrieve junk when a retainer opens", ref _cfg.RetainerAutoRetrieve))
            Dirty = true;
        Help("Pulls matching junk into your bags as soon as you open a retainer, without asking.");
    }

    private void DrawKeepList()
    {
        ImGui.Spacing();

        // Renders the snapshot, never the live HashSet.
        var list = KeepListSnapshot;
        if (list.Length == 0)
        {
            ImGui.TextDisabled("Empty. Use the Keep button on any row in the main window.");
            return;
        }

        if (ImGui.Button("Clear the whole list"))
            ClearKeepListRequested = true;

        ImGui.Separator();

        using var child = ImRaii.Child("##keeplist", new Vector2(0, 0));
        if (!child)
            return;

        foreach (var (id, name) in list)
        {
            using (ImRaii.PushId((int)id))
            {
                if (ImGui.SmallButton("Remove"))
                    Interlocked.Exchange(ref _unkeepRequest, id);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(name);
        }
    }

    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
