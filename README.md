# SellJunk

A Dalamud plugin that finds vendor-replaceable clutter in your bags, shows you exactly what it
found and why, and sells it to any open vendor on one click.

## The optimizer panel

The main window is a set of independent categories, in the shape of Teamcraft's inventory
optimizer: one collapsible row each, with its own count, its own threshold, and its own on/off
toggle. A stack is staged for review if **any enabled** category catches it.

| Category | Param | Default | Eligible items |
|---|---|---|---|
| Items that can be bought with gil | max price | **on**, 1000g | 897 |
| Items that can be gathered easily | max node level | off, 90 | 580 |
| Items only used in below-max-level crafts | max craft level | off, auto | 2,620 |
| Items you have in very small stacks | threshold | off, 3 | live only |
| Items only used for a single recipe | — | off | 1,025 |
| Items not used in any recipe | — | off | 17,800 |

"Eligible items" is how many distinct items in the *whole game* the category can match, as a sense
of scale before you switch one on — not how many are in your bags. Only the vendor category is on
out of the box, because that is the one where selling is always reversible.

Three more categories are **advisory**: they're counted and listed, but never sold, because they
aren't sales — *duplicated across containers* (consolidate by hand), *100% spiritbond gear*
(extract the materia), and *HQ stacks that could be lowered*.

### Getting the old conservative rule back

Settings → Global gates → **Require the item to be re-buyable from a vendor** puts the original
AND-gate back over everything: nothing is staged unless a vendor also stocks it cheaply. With that
on plus the gathering and craft categories enabled, you get the previous behaviour — 290 items
instead of 3,470.

### What this deliberately spares

- **Timed node drops.** Unspoiled, ephemeral and legendary node materials never qualify, at any
  level. You can't just go re-gather those on demand.
- **Intermediates that feed max-level crafts.** The craft rule follows the chain. Muddy Water is a
  level-1 craft ingredient, but its output feeds a level-100 recipe, so it is not junk. Turning
  "Follow the craft chain" off would wrongly flag it, along with about 115 others.

  One consequence worth knowing, because the two sub-rules are OR'd: an item that feeds a
  max-level craft can still be sold if it *also* happens to be a cheap low-level node drop.
  On current game data that is 15 items — Iron Ore, Cotton Boll, Rock Salt, Muddy Water,
  Iron Sand and similar. All are re-buyable for single-digit to ~200 gil, which is why this is
  allowed by default. **"Never sell anything that feeds a max-level craft"** in the settings
  turns the craft chain into a veto and spares them.
- **Items used in no recipe at all.** Under a strict reading these don't satisfy "only used in
  below-max crafts", so they're spared by default. There's a toggle if you disagree.
- HQ, unique, collectable, indisposable items, and anything carrying materia or spiritbond.
- Anything a vendor pays 0 gil for, and anything on your keep list.

## Using it

`/selljunk` (or `/sj`) opens the window. `/selljunk config`, `/selljunk sell`, `/selljunk retrieve`,
`/selljunk stop` also work.

**Thresholds** live on the category rows themselves, next to their counts, so you tune them where
you can see the effect. The vendor one defaults to 1000 gil. Only items an NPC actually stocks
*for gil* count; token and currency shops are ignored, and so are quest- or achievement-gated
listings, since you can't necessarily buy those back.

**Selling is a two-step confirm.** Walk up to any vendor and open the shop. The main window lists
every flagged stack with the reason and what the vendor pays. Pressing **Review** does not sell
anything — it opens a separate **Confirm sale** pane containing the exact list that would go, with
a checkbox on every row:

- Untick anything you want to keep this time. The totals update as you go.
- **Check all / Uncheck all** for a fast pass.
- **Keep forever** on a row unticks it *and* adds it to your keep list so it is never flagged again.
- **Cancel**, or just closing the pane, sells nothing.

Only the still-ticked rows are sold, and each one is re-checked against your current rules at that
moment — so if you added something to the keep list from inside the pane, it is dropped even though
it was staged. If the vendor closes while the pane is open, the pane closes and nothing is sold.

**Retainers get the same optimizer.** Open a retainer at a summoning bell and the window opens
docked to the **top-right edge** of whichever retainer window is up — the side opposite
AutoRetainer — with the Retainer tab already selected. It follows the retainer window if you drag
it, and flips to the left side if there isn't room on the right. While docked it's pinned (no move
or resize); turn off *Dock to the retainer window* in settings to place it yourself.

Docking is gated on the `OccupiedSummoningBell` condition first, then anchors to the most specific
retainer window present, in order: `InventoryRetainerLarge`, `InventoryRetainer`, `RetainerGrid0`,
`SelectString`, `RetainerList` — so it tracks you from the bell list through the menu into the
inventory grid. The condition check is load-bearing: `SelectString` is the generic NPC menu addon,
so without it the panel would dock itself to any shop or quest dialog in the game.

For reference, AutoRetainer's `RetainerListOverlay` sits directly *above* `RetainerList`
(`addon->Y - height`), so the top-right position does not collide with it.

The Retainer tab shows the identical category rows, counts and thresholds over that retainer's
bags — the toggles are shared, so changing one changes it for both sides.

The game has no "sell to an NPC from a retainer's bag" interaction — it does not exist, so no
plugin can do it. **Review** there stages a *retrieve* instead: the same confirmation pane, worded
for the move, pulling the ticked stacks into your inventory to sell at the next vendor. Nothing is
sold at this step and you can entrust anything back afterwards.

Duplication is detected across both sides at once, so "the same item is in your bags *and* your
retainer" — the case actually worth consolidating — shows up rather than only within-container
repeats.

The **auto-retrieve** setting deliberately skips the confirmation pane; it is opt-in and described
as "without asking", and the move is reversible.

## Building

Needs the .NET 10 SDK and Dalamud installed (the SDK finds it at
`%AppData%\XIVLauncher\addon\Hooks\dev`).

```bash
dotnet build "C:\Users\Jackie\Documents\SellJunk\SellJunk\SellJunk.csproj" -c Release
```

Output lands at `SellJunk\bin\Release\SellJunk.dll` — note there is no target-framework subfolder,
the Dalamud SDK turns that off.

To load it in game: **Dalamud Settings → Experimental → Dev Plugin Locations**, add

```
C:\Users\Jackie\Documents\SellJunk\SellJunk\bin\Debug\SellJunk.dll
```

then Plugin Installer → Dev Tools → reload.

## How it works

**Classification** (`Data/JunkIndex.cs`) is built once at load from Lumina Excel data, off the game
thread. Three tables:

- *Vendor* — membership in the `GilShopItem` subrow sheet, priced from `Item.PriceMid`. Membership
  is the signal, not the price: 50,358 items have a nonzero `PriceMid` but only 6,741 are actually
  stocked anywhere, so price alone over-reports by ~7.5x. Listings gated behind a quest or
  achievement don't count either — if you haven't done the quest, no shop will show it and the
  sale isn't actually reversible. That drops 6,741 to 4,984.
- *Gathering* — `GatheringPointBase` and the `GatheringItemPoint` subrow sheet, both keyed back to
  items, with per-`GatheringPoint` timing decoded from `GatheringPointTransient` the way GatherBuddy
  does it (including the always-up downgrade and the `duration == 160` fixup).
- *Crafting* — a reverse index from ingredient to the highest craft level it ultimately feeds,
  relaxed to a fixpoint so the chain rollup is cycle-safe.

**Selling** (`Game/GameActions.cs`) drives the inventory right-click menu. This is not a stylistic
choice: there is no sell function anywhere in FFXIVClientStructs (`ShopEventHandler` exposes only
`ExecuteBuy`), and the Shop addon has no sell callback. The game sells purely from the context menu,
so the plugin opens that menu for the slot, finds the entry whose label matches `Addon` sheet row 93,
and fires it — the same path a human click takes, with no signature to break on patch day. This is
what SimpleTweaks and AutoRetainer both do.

**Retrieving** (same file) goes through `AgentRetainer`'s inherited
`InventoryContextEvent.HandleCallback` with the documented `Retrieve = 0` command.

**The loop** (`Game/SlotActionRunner.cs`) does one action per cooldown window (~500ms by default),
and confirms each one by re-reading the slot before starting the next. It never trusts a cached
snapshot: slots are revalidated immediately before every action, because a sale past the ten-entry
buyback list is irreversible.

While a run is active, YesAlready is paused via its `YesAlready.StopRequests` data share so it can't
race the confirmation dialog. That's deliberately the shared-set route rather than YesAlready's
enable/pause IPC, which writes the user's saved config — a crash mid-run would otherwise leave it
switched off permanently.

### Threading

Game memory may only be touched on the framework thread, but ImGui draws on the render thread. So
the windows never read game state or mutate shared config directly — they set request flags and
render framework-thread snapshots (`JunkTracker`, and the keep-list snapshot in `ConfigWindow`).
That matters most for the keep list: it is consulted on every scan *and* immediately before every
sale, so a `HashSet` rewrite from the render thread mid-lookup could make the last-chance check
miss and sell a protected item.

The index is built off-thread and published by reference swap, never mutated in place, so a reader
sees either the whole old table or the whole new one. Rebuilds are chained onto a single task so
two can't overlap, and are cancelled on unload.

## Caveats

- Fish are not gathering nodes and have no node level, so the gathering rule doesn't apply to them.
  They can still qualify via the craft rule.
- Spearfishing items live in a different sheet (`SpearfishingItem`) and are excluded from the node rule.
- The armoury chest is scannable but off by default — it holds gear, which is riskier to sell than
  materials.
- Built against Dalamud API 15 / FFXIVClientStructs 7.51. Struct field reads may need revisiting
  after a game patch.
