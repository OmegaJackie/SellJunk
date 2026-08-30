using System.Collections.Generic;
using Dalamud.Configuration;
using SellJunk.Data;

namespace SellJunk;

/// <summary>
/// Persisted settings.
///
/// The model is an optimizer panel: each category is an independent filter with its own toggle
/// and its own threshold, and a stack is staged for review if ANY enabled sell-able category
/// catches it. <see cref="RequireVendorBuyable"/> can put the old conservative AND-gate back on
/// top of that if you want it.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    // ---- Category toggles --------------------------------------------------

    /// <summary>
    /// Per-category on/off. Missing entries fall back to the category's own default, so
    /// categories added in a later version light up without a config migration.
    /// </summary>
    public Dictionary<JunkCategory, bool> CategoryEnabled = new();

    internal bool IsEnabled(CategoryInfo info) =>
        CategoryEnabled.TryGetValue(info.Category, out var on) ? on : info.DefaultEnabled;

    internal void SetEnabled(JunkCategory category, bool value) => CategoryEnabled[category] = value;

    // ---- Category parameters -----------------------------------------------

    /// <summary>Item must be stocked by a gil vendor for strictly less than this.</summary>
    public int MaxVendorPrice = 1000;

    /// <summary>Highest gathering node level that still counts as "easily gathered".</summary>
    public int MaxNodeLevel = 90;

    /// <summary>
    /// Items obtainable only from timed nodes (unspoiled / ephemeral / legendary) or from
    /// hidden node slots never satisfy the node rule, whatever their level.
    /// </summary>
    public bool ExcludeTimedNodes = true;

    /// <summary>Stacks smaller than this count as "very small". Only applies to stackable items.</summary>
    public int SmallStackThreshold = 3;

    /// <summary>
    /// Read the craft cap out of the recipe data at load instead of trusting a baked-in number,
    /// so the plugin does not go stale the patch the level cap moves.
    /// </summary>
    public bool AutoDetectMaxCraftLevel = true;

    public int MaxCraftLevel = 100;

    /// <summary>
    /// Follow the craft chain. A level-60 intermediate whose output feeds a level-100 recipe
    /// counts as a max-level material and is spared. Off = only look one step.
    /// </summary>
    public bool RollUpCraftChain = true;

    // ---- Global gates ------------------------------------------------------

    /// <summary>
    /// Optional AND-gate over every category: nothing is staged unless a vendor also stocks it
    /// cheaply, so every sale stays reversible. Off by default now that categories are
    /// independent, but this is how you get the original conservative behaviour back.
    /// </summary>
    public bool RequireVendorBuyable = false;

    /// <summary>
    /// Never stage anything that ultimately feeds a max-level craft, whatever else matched it.
    /// </summary>
    public bool CraftChainVeto = false;

    // ---- Safety rails (applied before the categories, always) --------------
    public bool SkipHq = true;
    public bool SkipUnique = true;
    public bool SkipIndisposable = true;
    public bool SkipCollectables = true;
    public bool SkipSpiritbondOrMateria = true;

    /// <summary>Never sell something the vendor pays less than this for.</summary>
    public int MinSellPrice = 1;

    /// <summary>Item ids that are never sold, whatever the categories say. The "Keep" button feeds this.</summary>
    public HashSet<uint> Blacklist = new();

    // ---- Containers --------------------------------------------------------
    public bool IncludeArmoryChest = false;

    // ---- Behaviour ---------------------------------------------------------

    /// <summary>
    /// Framework ticks between two consecutive game actions (~60 to the second). 30 matches the
    /// 500 ms per item that AutoRetainer has used in production for years. Lower is faster and
    /// leans harder on the server.
    /// </summary>
    public int TicksBetweenActions = 30;

    public bool AutoOpenWindowAtShop = true;
    public bool ChatSummary = true;

    /// <summary>
    /// While a retainer window is up, pin this window to its top-right edge - the side opposite
    /// the one AutoRetainer takes - and follow it if it is dragged.
    /// </summary>
    public bool DockToRetainerWindow = true;

    /// <summary>Open the window and select the Retainer tab as soon as a retainer is opened.</summary>
    public bool AutoOpenWindowAtRetainer = true;

    /// <summary>At a summoning bell, pull matching junk out of the open retainer automatically.</summary>
    public bool RetainerAutoRetrieve = false;
}
