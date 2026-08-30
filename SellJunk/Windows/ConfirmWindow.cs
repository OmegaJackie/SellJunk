using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SellJunk.Game;

namespace SellJunk.Windows;

/// <summary>
/// The last stop before anything is sold: the exact list that will go, with a checkbox on every
/// row. Nothing here is live game state - it is a snapshot staged on the framework thread, and
/// the only things that cross back are the request flags below.
/// </summary>
internal enum ConfirmMode
{
    Sell,
    Retrieve,
}

internal sealed class ConfirmWindow : Window
{
    /// <summary>What the confirmed action will be. Set alongside the staged list.</summary>
    public volatile ConfirmMode Mode = ConfirmMode.Sell;

    /// <summary>Staged snapshot, written by the plugin on the framework thread before opening.</summary>
    public JunkStack[] Items = [];

    /// <summary>
    /// Per-row include flags, toggled here on the render thread and read once on confirm.
    /// Parallel to <see cref="Items"/>. Individual bool writes are atomic, and the arrays are
    /// replaced wholesale rather than resized, so no lock is needed.
    /// </summary>
    public bool[] Include = [];

    public volatile bool ConfirmRequested;
    public volatile bool CancelRequested;
    private uint _keepForeverRequest;

    public ConfirmWindow() : base("Confirm###SellJunkConfirm")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 360),
            MaximumSize = new Vector2(1400, 1200),
        };
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public uint TakeKeepForeverRequest() => Interlocked.Exchange(ref _keepForeverRequest, 0);

    public override void OnClose() => CancelRequested = true;

    public override void Draw()
    {
        var count = 0;
        var gil = 0L;
        var rebuy = 0L;
        for (var i = 0; i < Items.Length && i < Include.Length; i++)
        {
            if (!Include[i])
                continue;
            count++;
            gil += Items[i].StackValue;
            rebuy += Items[i].StackRebuyCost;
        }

        var selling = Mode == ConfirmMode.Sell;
        var plural = count == 1 ? "" : "s";

        if (selling)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                $"About to sell {count} stack{plural} for about {gil:N0} gil.");
            ImGui.TextDisabled($"Buying all of it back from a vendor would cost about {rebuy:N0} gil.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Uncheck anything you want to keep, then confirm.");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.45f, 0.78f, 1f, 1f),
                $"About to pull {count} stack{plural} out of your retainer and into your bags.");
            ImGui.TextDisabled("Nothing is sold here, and you can entrust anything back afterwards.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Uncheck anything you would rather leave with the retainer.");
        }

        ImGui.Separator();

        using (ImRaii.Disabled(count == 0))
        {
            var verb = selling ? "Sell" : "Retrieve";
            if (ImGui.Button($"{verb} {count} stacks", new Vector2(180, 0)))
                ConfirmRequested = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            CancelRequested = true;

        ImGui.SameLine();
        if (ImGui.Button("Check all"))
        {
            for (var i = 0; i < Include.Length; i++)
                Include[i] = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Uncheck all"))
        {
            for (var i = 0; i < Include.Length; i++)
                Include[i] = false;
        }

        ImGui.Separator();
        DrawTable();
    }

    private void DrawTable()
    {
        if (Items.Length == 0)
        {
            ImGui.TextDisabled("Nothing staged.");
            return;
        }

        using var table = ImRaii.Table("##confirmlist", 7,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn("##inc", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("You get", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Re-buy", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableSetupColumn("##keep", ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < Items.Length && i < Include.Length; i++)
        {
            var stack = Items[i];
            ImGui.TableNextRow();
            using var id = ImRaii.PushId(i);

            ImGui.TableNextColumn();
            ImGui.Checkbox("##inc", ref Include[i]);

            ImGui.TableNextColumn();
            if (Include[i])
                ImGui.TextUnformatted(stack.Name);
            else
                ImGui.TextDisabled(stack.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stack.Quantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{stack.StackValue:N0}");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(stack.StackRebuyCost > 0 ? $"{stack.StackRebuyCost:N0}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(stack.Detail);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Keep forever"))
            {
                Include[i] = false;
                Interlocked.Exchange(ref _keepForeverRequest, stack.ItemId);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unchecks this row and adds the item to your keep list, so it is never flagged again.");
        }
    }
}
