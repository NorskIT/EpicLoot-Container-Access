using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

internal sealed class Actions
{
    private readonly Plugin plugin;
    private EnchantingTableUIPanelBase? panel;
    private MethodBase? method;
    private Component? table;
    private float deadline;
    private Container[] containers = Array.Empty<Container>();
    private readonly HashSet<Inventory> inventories = new();
    private readonly Dictionary<Inventory, InventoryUndo> undo = new();
    private readonly List<Action> products = new();
    private readonly List<Item> selected = new();
    private readonly Dictionary<Item, ItemCheck> bindings = new();
    private Dictionary<IListElement, int> selection = new();
    private AugmentChoiceDialog? dialog;
    private readonly Inventory augmentItem = new Inventory("ECA augment", null, 1, 1);
    private bool augmentPaid;
    private bool finishing;
    private int settleFrames;
    private EnchantingFeature? upgradedFeature;
    private int previousLevel;
    private int expectedUpgradeLevel;
    internal ActionPlan? Plan;
    private bool NeedsNetwork;
    internal bool Executing { get; private set; }
    internal bool Busy => panel;
    internal Actions(Plugin plugin) { this.plugin = plugin; }

    internal bool Intercept(EnchantingTableUIPanelBase target, MethodBase original)
    {
        if (Executing) return true;
        if (!plugin.Ready) return true;
        if (Busy) return false;
        if (!Storage.Table || !plugin.Network.ConfigReady) { plugin.Notice("Storage is synchronizing. Please try again."); target.Cancel(); return false; }
        try
        {
            plugin.Storage.Refresh(true);
            panel = target; method = original; table = Storage.Table;
            selected.Clear();
            selection = target.AvailableItems ? target.AvailableItems.GetCurrentSelectionAmounts() : new Dictionary<IListElement, int>();
            // Conversion recipes are not live items. Their requirements are guarded during payment.
            if (target.AvailableItems)
                foreach (var pair in selection)
                    if (pair.Key is InventoryItemListElement && pair.Value > 0) selected.Add(pair.Key.GetItem());
            if (target is UpgradeTableUI)
            {
                int feature = (int)AccessTools.Field(typeof(UpgradeTableUI), "_selectedFeature").GetValue(target);
                expectedUpgradeLevel = ((EnchantingTable)table).GetFeatureLevel((EnchantingFeature)feature);
            }
            if (selected.Any(x => !plugin.Storage.HasLive(x))) { Abort("The selected item moved or is protected."); return false; }
            var inputs = selection.Where(x => x.Value > 0 && x.Key is InventoryItemListElement)
                .Select(x => (Item: x.Key.GetItem(), Amount: x.Value)).ToList();
            if (target is RuneUI rune && ActionPlan.Field(rune, "_runeAction").ToString() == "Etch")
            {
                var chosenRune = rune.AvailableRunes.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
                if (chosenRune == null || !plugin.Storage.HasLive(chosenRune)) throw new InvalidOperationException("Select an available rune.");
                inputs.Add((chosenRune, 1)); selected.Add(chosenRune);
            }
            Plan = new ActionPlan(plugin, target, inputs);
            containers = Plan.Containers.ToArray();
            bindings.Clear();
            foreach (var item in Plan.Remaining.Keys)
            {
                var inventory = plugin.Storage.Sources.TryGetValue(item, out var source) ? source.GetInventory() : Player.m_localPlayer.GetInventory();
                bindings[item] = new ItemCheck(inventory, item, Plan.Remaining[item]);
            }
            if (containers.Length > 255) throw new InvalidOperationException("Too many containers. Reduce the storage radius.");
            inventories.Clear();
            var playerInventory = Player.m_localPlayer.GetInventory();
            inventories.Add(playerInventory);
            foreach (var c in containers) inventories.Add(c.GetInventory());
            target.Lock();
            if (target.MainButton) target.MainButton.interactable = false;
            deadline = Time.unscaledTime + 5;
            NeedsNetwork = containers.Any(c => !Storage.View(c)!.IsOwner()) || (target is UpgradeTableUI && !table.GetComponent<ZNetView>().IsOwner());
            if (NeedsNetwork) plugin.Network.Begin(containers, target is UpgradeTableUI, target.GetType().Name);
        }
        catch (Exception e) { plugin.Error(e); Abort(e.Message); }
        return false;
    }
    internal void Update()
    {
        if (!Busy) return;
        if (!Player.m_localPlayer || !table || Storage.Table != table || !panel!.isActiveAndEnabled)
        { Abort("The table was closed. Storage action cancelled."); return; }
        if (plugin.Network.Failure != null) { Abort(plugin.Network.Failure); return; }
        if (dialog)
        {
            if (!dialog.gameObject.activeSelf) Finish();
            else if (selected.Any(x => !augmentItem.ContainsItem(x) && !plugin.Storage.HasLive(x))) Abort("The selected item moved or became protected.");
            return;
        }
        if (finishing)
        {
            // Ross drawers commit their view at end-of-frame. Do not release immediately after Save.
            if (--settleFrames <= 0) Release();
            return;
        }
        if (Time.unscaledTime > deadline) { Abort("Storage request timed out. Nothing was consumed."); return; }
        if (NeedsNetwork && !plugin.Network.Granted) return;
        if (panel is UpgradeTableUI && !table.GetComponent<ZNetView>().IsOwner()) return;
        if (containers.Any(c => !c || !Storage.View(c) || !Storage.View(c)!.IsOwner()))
        { if (!NeedsNetwork) Abort("Storage ownership changed. Please try again."); return; }
        try
        {
            if ((Player.m_localPlayer.transform.position - table.transform.position).sqrMagnitude > 100)
            { Abort("The enchanting table is no longer in reach."); return; }
            if (!(panel is UpgradeTableUI) && !((EnchantingTable)table).IsFeatureUnlocked(ActionPlan.Feature(panel!)))
            { Abort("This table feature is locked."); return; }
            if (panel is RuneUI rune && ActionPlan.Field(rune, "_runeAction").ToString() == "Etch" &&
                rune.AvailableRunes.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem() != selected.Last())
            { Abort("The selected rune changed."); return; }
            foreach (var c in containers)
            {
                // Ownership and inventory replication arrive independently. Load the received ZDO before validating it.
                Storage.Load(c);
                if (!plugin.Storage.Eligible(c)) { Abort("A required container is no longer available."); return; }
            }
            Item Resolve(Item item) => bindings.TryGetValue(item, out var check) ? check.Resolve() : item;
            Plan!.Rebind(Resolve);
            for (int i = 0; i < selected.Count; i++) selected[i] = Resolve(selected[i]);
            foreach (var entry in selection.Keys.OfType<InventoryItemListElement>()) entry.Item = Resolve(entry.GetItem());
            if (panel is RuneUI selectedRunePanel)
            {
                foreach (var entry in selectedRunePanel.AvailableRunes.GetCurrentSelectionAmounts().Keys.OfType<InventoryItemListElement>()) entry.Item = Resolve(entry.GetItem());
                var selectedItemField = AccessTools.Field(typeof(RuneUI), "_selectedItem");
                if (selectedItemField.GetValue(panel) is Item oldItem) selectedItemField.SetValue(panel, Resolve(oldItem));
            }
            plugin.Storage.Invalidate();
            if (selected.Any(x => !plugin.Storage.HasLive(x))) { Abort("The selected item moved or is protected."); return; }
            var currentSelection = panel!.AvailableItems ? panel.AvailableItems.GetCurrentSelectionAmounts() : new Dictionary<IListElement, int>();
            if (currentSelection.Count != selection.Count || selection.Any(x => !currentSelection.TryGetValue(x.Key, out int quantity) || quantity != x.Value))
            { Abort("The selection changed while waiting for storage."); return; }
            var currentCosts = ActionPlan.CalculateCosts(panel!);
            if (ActionPlan.Settings(panel!) != Plan!.Options || currentCosts.Count != Plan.Costs.Count || currentCosts.Any(x => !Plan.Costs.TryGetValue(x.Key, out int n) || n != x.Value))
            { Abort("The action settings or cost changed. Please select it again."); return; }
            undo.Clear();
            foreach (var inv in inventories) undo[inv] = new InventoryUndo(inv);
            products.Clear();
            Executing = true;
            try
            {
                if (panel is UpgradeTableUI upgrade) Upgrade(upgrade);
                else method!.Invoke(panel, null);
                if (Plan!.Payments.Any(x => x.Amount != 0)) throw new InvalidOperationException("The action did not complete its planned payment.");
                if (dialog) TakeAugmentItem();
                else MoveProcessedItems();
            }
            finally { Executing = false; }
            Persist();
            // All deductions succeeded before anything is given to the player.
            // Once publishing starts, refunding inputs could duplicate already delivered output.
            undo.Clear(); upgradedFeature = null; augmentPaid = dialog;
            foreach (var give in products) give();
            products.Clear();
            if (!dialog) Finish();
            else
            {
                // Inputs are paid and the selected item is now local. No storage participation while choosing.
                containers = Array.Empty<Container>(); NeedsNetwork = false; inventories.Clear(); inventories.Add(Player.m_localPlayer.GetInventory());
                plugin.Network.Release();
            }
        }
        catch (Exception e)
        {
            Executing = false;
            plugin.Error(e);
            Rollback();
            Abort(e is TargetInvocationException && e.InnerException != null ? e.InnerException.Message : e.Message);
        }
    }
    internal void Queue(Item item) { var copy = item.Clone(); products.Add(() => InventoryManagement.Instance.GiveItem(copy)); }
    internal void Queue(string name, int amount) => products.Add(() => InventoryManagement.Instance.GiveItem(name, amount));
    private void Upgrade(UpgradeTableUI upgrade)
    {
        var station = (EnchantingTable)table!;
        int index = (int)AccessTools.Field(typeof(UpgradeTableUI), "_selectedFeature").GetValue(upgrade);
        if (index < 0) throw new InvalidOperationException("Select a table feature first.");
        var feature = (EnchantingFeature)index;
        if (station.GetFeatureLevel(feature) != expectedUpgradeLevel) throw new InvalidOperationException("The table was upgraded by another player. Please select the feature again.");
        if (!station.IsFeatureAvailable(feature) || station.IsFeatureMaxLevel(feature)) throw new InvalidOperationException("This feature cannot be upgraded.");
        var cost = station.IsFeatureLocked(feature) ? station.GetFeatureUnlockCost(feature) : station.GetFeatureUpgradeCost(feature);
        if (!Player.m_localPlayer.NoCostCheat())
            foreach (var entry in cost) plugin.Storage.Withdraw(entry.GetItem().m_shared.m_name, entry.GetItem().m_stack, true);
        previousLevel = station.GetFeatureLevel(feature); upgradedFeature = feature;
        station.SetFeatureLevel(feature, previousLevel + 1);
        if (station.GetFeatureLevel(feature) != previousLevel + 1) throw new InvalidOperationException("The table rejected the upgrade.");
        upgrade.Cancel();
        AccessTools.Method(typeof(UpgradeTableUI), "Refresh").Invoke(upgrade, null);
    }
    internal void WrapChoice(AugmentChoiceDialog choice, Item item, ref Action<Item, int, MagicItemEffect> callback)
    {
        if (!Executing) return;
        dialog = choice;
        var original = callback;
        bool completed = false;
        callback = (target, index, effect) =>
        {
            if (completed) return;
            completed = true;
            if (!Busy || (NeedsNetwork && !plugin.Network.Granted) || (!augmentItem.ContainsItem(target) && !plugin.Storage.HasLive(target)) || Storage.Table != table ||
                containers.Any(c => !c || !plugin.Storage.Eligible(c) || !Storage.View(c)!.IsOwner()))
            { Abort("The selected item is no longer available. Augment cancelled."); return; }
            try
            {
                undo.Clear();
                foreach (var inv in inventories) undo[inv] = new InventoryUndo(inv);
                undo[augmentItem] = new InventoryUndo(augmentItem);
                Executing = true;
                original(target, index, effect);
                MoveProcessedItems(true);
                Persist();
                undo.Clear();
                Executing = false;
                foreach (var give in products) give();
                products.Clear();
                Finish();
            }
            catch (Exception e) { Executing = false; plugin.Error(e); Rollback(); Abort("Augment failed; the action was cancelled."); }
            finally { Executing = false; }
        };
    }
    private void TakeAugmentItem()
    {
        var item = selected.First();
        var source = containers.FirstOrDefault(c => c.GetInventory().ContainsItem(item));
        if (!source) return;
        source.GetInventory().RemoveItem(item);
        augmentItem.GetAllItems().Add(item);
    }
    private void MoveProcessedItems(bool confirmed = false)
    {
        if (!(panel is EnchantUI || panel is AugmentUI || panel is DisenchantUI || panel is RuneUI)) return;
        foreach (var item in selected.Take(1))
        {
            var source = containers.FirstOrDefault(c => c.GetInventory().ContainsItem(item));
            if (!source) continue;
            if (!confirmed && (!undo.TryGetValue(source.GetInventory(), out var saved) || !saved.ItemChanged(item))) continue;
            if (!Storage.View(source)!.IsOwner()) throw new InvalidOperationException("Storage ownership changed before delivery.");
            Queue(item);
            source.GetInventory().RemoveItem(item);
        }
    }
    private void Persist()
    {
        foreach (var c in containers)
        {
            if (!c || !Storage.View(c)!.IsOwner()) throw new InvalidOperationException("Storage ownership was lost before saving.");
            Storage.Save(c);
        }
        plugin.Storage.Invalidate();
    }
    private void Rollback()
    {
        if (upgradedFeature.HasValue && table && table.GetComponent<ZNetView>().IsOwner())
            ((EnchantingTable)table).SetFeatureLevel(upgradedFeature.Value, previousLevel);
        upgradedFeature = null;
        // Restore item data directly. Inventory.Load would clamp oversized drawer stacks.
        foreach (var pair in undo)
        {
            var source = containers.FirstOrDefault(c => c && c.GetInventory() == pair.Key);
            if (source && !Storage.View(source)!.IsOwner()) { plugin.Notice("Storage ownership changed; automatic rollback was skipped for that container."); continue; }
            pair.Value.Restore();
        }
        foreach (var c in containers) if (c && Storage.View(c)!.IsOwner()) Storage.Save(c);
        undo.Clear(); products.Clear(); plugin.Storage.Invalidate();
    }
    private void DeliverAugment()
    {
        if (!augmentPaid) return;
        augmentPaid = false;
        foreach (var item in augmentItem.GetAllItems().ToArray()) InventoryManagement.Instance.GiveItem(item.Clone());
        augmentItem.GetAllItems().Clear();
    }
    private void Finish() { DeliverAugment(); undo.Clear(); products.Clear(); dialog = null; finishing = true; settleFrames = 2; }
    private void Release()
    {
        var old = panel;
        panel = null; table = null; method = null; dialog = null; finishing = false;
        containers = Array.Empty<Container>(); NeedsNetwork = false; Plan = null; bindings.Clear(); inventories.Clear(); undo.Clear(); selected.Clear(); selection.Clear(); products.Clear(); upgradedFeature = null;
        augmentItem.GetAllItems().Clear(); augmentPaid = false;
        plugin.Network.Release(); plugin.Storage.Invalidate();
        // EpicLoot retains its own success/choice dialog locking state.
        if (old && !old.CanCancel()) old.Unlock();
    }
    internal void Abort(string? reason)
    {
        if (dialog)
        {
            dialog.gameObject.SetActive(false);
        }
        DeliverAugment();
        var old = panel;
        Release();
        if (old) { old.Cancel(); old.DeselectAll(); old.Unlock(); }
        if (reason != null) plugin.Notice(reason);
    }
}
