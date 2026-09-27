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
    private readonly Dictionary<Inventory, InventoryBaseline> baseline = new();
    private readonly Dictionary<Inventory, InventoryUndo> undo = new();
    private readonly List<Action> products = new();
    private readonly List<Item> selected = new();
    private Dictionary<IListElement, int> selection = new();
    private AugmentChoiceDialog? dialog;
    private bool finishing;
    private int settleFrames;
    private EnchantingFeature? upgradedFeature;
    private int previousLevel;
    private int expectedUpgradeLevel;
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
            containers = plugin.Storage.Nearby.ToArray();
            if (containers.Length > 255) throw new InvalidOperationException("Too many containers. Reduce the storage radius.");
            baseline.Clear();
            var playerInventory = Player.m_localPlayer.GetInventory();
            baseline[playerInventory] = new InventoryBaseline(playerInventory, true, "Player inventory");
            foreach (var c in containers) baseline[c.GetInventory()] = new InventoryBaseline(c.GetInventory(), false, "Container " + Storage.Id(c));
            target.Lock();
            if (target.MainButton) target.MainButton.interactable = false;
            deadline = Time.unscaledTime + 5;
            plugin.Network.Begin(containers);
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
            else if (selected.Any(x => !plugin.Storage.HasLive(x))) Abort("The selected item moved or became protected.");
            return;
        }
        if (finishing)
        {
            // Ross drawers commit their view at end-of-frame. Do not release immediately after Save.
            if (--settleFrames <= 0) Release();
            return;
        }
        if (Time.unscaledTime > deadline) { Abort("Storage request timed out. Nothing was consumed."); return; }
        if (!plugin.Network.Granted) return;
        if (!table.GetComponent<ZNetView>().IsOwner()) return;
        if (containers.Any(c => !c || !Storage.View(c) || !Storage.View(c)!.IsOwner())) return;
        try
        {
            foreach (var c in containers)
            {
                if (!plugin.Storage.Eligible(c) || !plugin.Network.Fingerprints.TryGetValue(Storage.Id(c), out var expected) || Network.Hash(c.GetInventory()) != expected)
                {
                    plugin.Warn("Container " + Storage.Id(c) + ": access or authoritative fingerprint changed. " + baseline[c.GetInventory()].Difference(c.GetInventory()));
                    Abort("Storage changed while preparing the action. Please try again.");
                    return;
                }
            }
            foreach (var pair in baseline)
            {
                var difference = pair.Value.Difference(pair.Key);
                if (difference == null) continue;
                plugin.Warn(difference);
                Abort("Inventory changed. Please select the action again.");
                return;
            }
            if (selected.Any(x => !plugin.Storage.HasLive(x))) { Abort("The selected item moved or is protected."); return; }
            var currentSelection = panel!.AvailableItems ? panel.AvailableItems.GetCurrentSelectionAmounts() : new Dictionary<IListElement, int>();
            if (currentSelection.Count != selection.Count || selection.Any(x => !currentSelection.TryGetValue(x.Key, out int quantity) || quantity != x.Value))
            { Abort("The selection changed while waiting for storage."); return; }
            undo.Clear();
            foreach (var inv in baseline.Keys) undo[inv] = new InventoryUndo(inv);
            products.Clear();
            Executing = true;
            try
            {
                if (panel is UpgradeTableUI upgrade) Upgrade(upgrade);
                else method!.Invoke(panel, null);
            }
            finally { Executing = false; }
            Persist();
            // All deductions succeeded before anything is given to the player.
            // Once publishing starts, refunding inputs could duplicate already delivered output.
            undo.Clear(); upgradedFeature = null;
            foreach (var give in products) give();
            products.Clear();
            if (!dialog) Finish();
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
            if (!Busy || !plugin.Network.Granted || !plugin.Storage.HasLive(target) || Storage.Table != table ||
                containers.Any(c => !c || !plugin.Storage.Eligible(c) || !Storage.View(c)!.IsOwner()))
            { Abort("The selected item is no longer available. Augment cancelled."); return; }
            try
            {
                undo.Clear();
                foreach (var inv in baseline.Keys) undo[inv] = new InventoryUndo(inv);
                Executing = true;
                original(target, index, effect);
                Persist();
                undo.Clear();
            }
            catch (Exception e) { plugin.Error(e); Rollback(); Abort("Augment failed; the action was cancelled."); }
            finally { Executing = false; }
        };
    }
    private void Persist()
    {
        foreach (var c in containers)
        {
            if (!c || !plugin.Network.OwnsReservation(Storage.Id(c)) || !Storage.View(c)!.IsOwner()) throw new InvalidOperationException("Storage ownership was lost before saving.");
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
    private void Finish() { undo.Clear(); products.Clear(); dialog = null; finishing = true; settleFrames = 2; }
    private void Release()
    {
        var old = panel;
        panel = null; table = null; method = null; dialog = null; finishing = false;
        containers = Array.Empty<Container>(); baseline.Clear(); undo.Clear(); selected.Clear(); selection.Clear(); products.Clear(); upgradedFeature = null;
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
        var old = panel;
        Release();
        if (old) { old.Cancel(); old.DeselectAll(); old.Unlock(); }
        if (reason != null) plugin.Notice(reason);
    }
}
