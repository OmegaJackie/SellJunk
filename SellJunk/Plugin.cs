using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Lumina.Excel.Sheets;
using SellJunk.Data;
using SellJunk.Game;
using SellJunk.Windows;

namespace SellJunk;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/selljunk";
    private const string ShortCommand = "/sj";

    private readonly Configuration _config;
    private readonly JunkIndex _index = new();
    private readonly JunkTracker _tracker;
    private readonly ShopSeller _seller;
    private readonly RetainerRetriever _retriever;
    private readonly YesAlreadySuppressor _yesAlready = new();

    private readonly WindowSystem _windows = new("SellJunk");
    private readonly MainWindow _main;
    private readonly ConfigWindow _configWindow;
    private readonly ConfirmWindow _confirmWindow = new();

    private readonly CancellationTokenSource _cancel = new();

    /// <summary>
    /// Index builds are chained onto this rather than fired off independently, so two rebuilds
    /// can never run at once and the last one to be requested is always the last to publish.
    /// </summary>
    private Task _indexTask = Task.CompletedTask;

    private bool _shopWasOpen;
    private bool _retainerWasOpen;
    private bool _sellerWasRunning;
    private bool _retrieverWasRunning;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Inject(new Services());

        _config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _tracker = new JunkTracker(_config, _index);
        _seller = new ShopSeller(_config);
        _retriever = new RetainerRetriever(_config);

        _main = new MainWindow(_config, _tracker, _seller, _retriever, _index);
        _configWindow = new ConfigWindow(_config, _index);
        _windows.AddWindow(_main);
        _windows.AddWindow(_configWindow);
        _windows.AddWindow(_confirmWindow);
        PublishKeepList();

        // Excel data is static and thread-safe, so the index build stays off the game thread.
        QueueIndexWork(token => _index.Build(_config.RollUpCraftChain, token), "build");

        Services.PluginInterface.UiBuilder.Draw += _windows.Draw;
        Services.PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        Services.Framework.Update += OnUpdate;

        Services.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open SellJunk. /selljunk config for settings, /selljunk sell to review a sale at an open vendor.",
        });
        Services.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /selljunk.",
        });
    }

    /// <summary>
    /// Chain one piece of index work onto the serialized queue. Cancellation matters at unload:
    /// Dalamud tears down the plugin's service scope and unloads the assembly as soon as Dispose
    /// returns, so a build still walking the sheets would be running against dead services.
    /// </summary>
    private void QueueIndexWork(Action<CancellationToken> work, string what)
    {
        var token = _cancel.Token;
        _indexTask = _indexTask.ContinueWith(_ =>
        {
            if (token.IsCancellationRequested)
                return;

            try
            {
                work(token);
            }
            catch (OperationCanceledException)
            {
                // Plugin is unloading - nothing to report.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Services.Log.Error(ex, $"Junk index {what} failed; SellJunk will stay idle.");
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void Dispose()
    {
        // Stop the index worker before anything it touches goes away.
        _cancel.Cancel();
        try
        {
            _indexTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Index worker did not stop cleanly.");
        }

        Services.Commands.RemoveHandler(Command);
        Services.Commands.RemoveHandler(ShortCommand);

        Services.Framework.Update -= OnUpdate;
        Services.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        Services.PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;

        _windows.RemoveAllWindows();

        // Must not be skipped: the stop-request set outlives this plugin, and a leftover
        // entry would leave the user's YesAlready silently paused.
        _yesAlready.Dispose();
        _cancel.Dispose();
    }

    private void OpenMain() => _main.IsOpen = true;

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config" or "settings":
                _configWindow.IsOpen = true;
                break;

            case "sell":
                if (!GameActions.ShopIsOpen())
                    Services.Chat.PrintError("[SellJunk] No vendor is open.");
                else
                    _main.SellRequested = true;
                break;

            case "retrieve":
                if (!GameActions.RetainerOpen())
                    Services.Chat.PrintError("[SellJunk] No retainer is open.");
                else
                    _main.RetrieveRequested = true;
                break;

            case "stop":
                _seller.Stop("stopped by user");
                _retriever.Stop("stopped by user");
                break;

            default:
                _main.IsOpen = !_main.IsOpen;
                break;
        }
    }

    /// <summary>
    /// The framework thread owns every read and write of game state. The windows only ever set
    /// request flags; this is where they are acted on.
    /// </summary>
    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Settings are handled even when the index never came up, so a broken build after a game
        // patch cannot silently swallow every config change the user makes.
        HandleConfigChanges();

        if (!_index.Ready)
            return;

        try
        {
            HandleContextTransitions();
            HandleRequests();

            _seller.Tick();
            _retriever.Tick();
        }
        catch (Exception ex)
        {
            // A throw must not skip ReportFinished below, or YesAlready stays suppressed and the
            // user's confirmation dialogs quietly stop being answered.
            Services.Log.Error(ex, "SellJunk tick failed; stopping the current run.");
            _seller.Stop("error");
            _retriever.Stop("error");
        }

        ReportFinished();

        _tracker.Tick();
    }

    private void HandleConfigChanges()
    {
        if (_configWindow.RebuildIndexRequested)
        {
            _configWindow.RebuildIndexRequested = false;
            var rollUp = _config.RollUpCraftChain;
            QueueIndexWork(_ => _index.RebuildCraftDemand(rollUp), "rebuild");
        }

        // Keep-list edits arrive as requests because the window runs on the render thread and
        // the set is read on the framework thread on every scan and before every sale.
        var unkeep = _configWindow.TakeUnkeepRequest();
        if (unkeep != 0)
        {
            _config.Blacklist.Remove(unkeep);
            _configWindow.Dirty = true;
        }

        if (_configWindow.ClearKeepListRequested)
        {
            _configWindow.ClearKeepListRequested = false;
            _config.Blacklist.Clear();
            _configWindow.Dirty = true;
        }

        if (!_configWindow.Dirty)
            return;

        _configWindow.Dirty = false;
        Services.PluginInterface.SavePluginConfig(_config);
        PublishKeepList();
        _tracker.Refresh();
    }

    /// <summary>The categories currently allowed to contribute to a sale.</summary>
    private JunkCategory EnabledSellMask()
    {
        var mask = JunkCategory.None;
        foreach (var info in JunkCategories.All)
        {
            if (info.Sellable && _config.IsEnabled(info))
                mask |= info.Category;
        }

        return mask;
    }

    /// <summary>Rebuild the render-thread-safe snapshot of the keep list.</summary>
    private void PublishKeepList()
    {
        var sheet = Services.Data.GetExcelSheet<Item>();
        _configWindow.KeepListSnapshot = _config.Blacklist
            .OrderBy(static id => id)
            .Select(id => (id, sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}"))
            .ToArray();
    }

    private void HandleContextTransitions()
    {
        var shopOpen = GameActions.ShopIsOpen();
        if (shopOpen != _shopWasOpen)
        {
            _shopWasOpen = shopOpen;
            if (shopOpen)
            {
                _tracker.Refresh();
                if (_config.AutoOpenWindowAtShop && _tracker.Stageable.Count > 0)
                    _main.IsOpen = true;
            }
            else
            {
                _seller.Stop("shop closed");

                // A staged sale is only meaningful while the vendor is still there.
                if (_confirmWindow.IsOpen && _confirmWindow.Mode == ConfirmMode.Sell)
                    CloseConfirmWindow();
            }
        }

        var retainerOpen = GameActions.RetainerOpen();
        if (retainerOpen == _retainerWasOpen)
            return;

        _retainerWasOpen = retainerOpen;
        if (retainerOpen)
        {
            _tracker.Refresh();

            if (_config.AutoOpenWindowAtRetainer)
            {
                _main.IsOpen = true;
                _main.FocusRetainerTab = true;
            }

            // This setting is explicitly "without asking", so it deliberately skips the
            // confirmation pane. Retrieval is reversible - you can entrust it all back.
            if (_config.RetainerAutoRetrieve && _tracker.Retainer.Count > 0)
                StartRetrieve([.. _tracker.Retainer]);
        }
        else
        {
            _retriever.Stop("retainer closed");

            if (_confirmWindow.IsOpen && _confirmWindow.Mode == ConfirmMode.Retrieve)
                CloseConfirmWindow();
        }
    }

    private void CloseConfirmWindow()
    {
        _confirmWindow.IsOpen = false;
        _confirmWindow.Items = [];
        _confirmWindow.Include = [];
        _confirmWindow.ConfirmRequested = false;
        _confirmWindow.CancelRequested = false;
    }

    private void HandleRequests()
    {
        if (_main.BlacklistRequest != 0)
        {
            _config.Blacklist.Add(_main.BlacklistRequest);
            _main.BlacklistRequest = 0;
            Services.PluginInterface.SavePluginConfig(_config);
            PublishKeepList();
            _tracker.Refresh();
        }

        if (_main.RefreshRequested)
        {
            _main.RefreshRequested = false;
            _tracker.Refresh();
        }

        // Category toggles and thresholds are edited on the optimizer panel itself.
        if (_main.SettingsChanged)
        {
            _main.SettingsChanged = false;
            Services.PluginInterface.SavePluginConfig(_config);
            _tracker.Refresh();
        }

        HandleConfirmWindow();

        if (_main.SellRequested)
        {
            _main.SellRequested = false;
            StageSale();
        }

        if (!_main.RetrieveRequested)
            return;

        _main.RetrieveRequested = false;
        StageRetrieve();
    }

    private void HandleConfirmWindow()
    {
        var keepForever = _confirmWindow.TakeKeepForeverRequest();
        if (keepForever != 0)
        {
            _config.Blacklist.Add(keepForever);
            Services.PluginInterface.SavePluginConfig(_config);
            PublishKeepList();
        }

        if (_confirmWindow.CancelRequested)
        {
            CloseConfirmWindow();
            return;
        }

        if (!_confirmWindow.ConfirmRequested)
            return;

        _confirmWindow.ConfirmRequested = false;
        var approved = CollectConfirmed();

        if (_confirmWindow.Mode == ConfirmMode.Sell)
            StartSell(approved);
        else
            StartRetrieve(approved);

        _confirmWindow.IsOpen = false;
    }

    /// <summary>
    /// Everything still ticked in the confirmation pane. Each one is re-classified against the
    /// current rules, because the pane can sit open while settings change - and the keep list in
    /// particular can grow from the pane itself. DescribeItem applies the keep list as a rail, so
    /// this covers that too.
    /// </summary>
    private List<JunkStack> CollectConfirmed()
    {
        var items = _confirmWindow.Items;
        var include = _confirmWindow.Include;
        var chosen = new List<JunkStack>(items.Length);

        for (var i = 0; i < items.Length && i < include.Length; i++)
        {
            if (!include[i])
                continue;

            // Re-check against the current categories, not the ones that staged it.
            var facts = _index.DescribeItem(items[i].ItemId, _config);
            if (facts.PassesRails && (facts.Categories & EnabledSellMask()) != 0)
                chosen.Add(items[i]);
        }

        return chosen;
    }

    /// <summary>
    /// Build the list the user is about to approve and show it. Nothing is sold until they
    /// confirm, and the seller revalidates every slot again at that point, so a stale row here
    /// is skipped rather than mis-sold.
    /// </summary>
    private void StageSale()
    {
        _tracker.Refresh();
        Stage(ConfirmMode.Sell, _tracker.Stageable, "Nothing in your bags matches your enabled categories.");
    }

    private void StageRetrieve()
    {
        _tracker.Refresh();
        Stage(ConfirmMode.Retrieve, _tracker.Retainer, "Nothing in this retainer matches your enabled categories.");
    }

    private void Stage(ConfirmMode mode, IReadOnlyList<JunkStack> source, string emptyMessage)
    {
        if (_seller.Running || _retriever.Running)
            return;

        if (source.Count == 0)
        {
            Services.Chat.Print($"[SellJunk] {emptyMessage}");
            return;
        }

        var staged = source.ToArray();
        var include = new bool[staged.Length];
        for (var i = 0; i < include.Length; i++)
            include[i] = true;

        _confirmWindow.Mode = mode;
        _confirmWindow.Items = staged;
        _confirmWindow.Include = include;
        _confirmWindow.ConfirmRequested = false;
        _confirmWindow.CancelRequested = false;
        _confirmWindow.IsOpen = true;
    }

    private void StartSell(List<JunkStack> approved)
    {
        if (_seller.Running || _retriever.Running)
            return;

        if (approved.Count == 0)
        {
            Services.Chat.Print("[SellJunk] Nothing left ticked - nothing sold.");
            return;
        }

        if (!GameActions.ShopIsOpen())
        {
            Services.Chat.PrintError("[SellJunk] The vendor closed before you confirmed; nothing was sold.");
            return;
        }

        _yesAlready.Suppress();
        _seller.Start(approved);
        _sellerWasRunning = true;
    }

    private void StartRetrieve(List<JunkStack> approved)
    {
        if (_seller.Running || _retriever.Running)
            return;

        if (approved.Count == 0)
        {
            Services.Chat.Print("[SellJunk] Nothing left ticked - nothing retrieved.");
            return;
        }

        if (!GameActions.RetainerOpen())
        {
            Services.Chat.PrintError("[SellJunk] The retainer closed before you confirmed; nothing was moved.");
            return;
        }

        _yesAlready.Suppress();
        _retriever.Start(approved);
        _retrieverWasRunning = true;
    }

    private void ReportFinished()
    {
        if (_sellerWasRunning && !_seller.Running)
        {
            _sellerWasRunning = false;
            _yesAlready.Restore();
            _tracker.Refresh();
            if (_config.ChatSummary)
                Services.Chat.Print($"[SellJunk] Sold {_seller.Completed} stacks for about {_seller.GilTotal:N0} gil.");
        }

        if (!_retrieverWasRunning || _retriever.Running)
            return;

        _retrieverWasRunning = false;
        _yesAlready.Restore();
        _tracker.Refresh();
        if (_config.ChatSummary)
            Services.Chat.Print($"[SellJunk] Retrieved {_retriever.Completed} stacks from your retainer.");
    }
}
