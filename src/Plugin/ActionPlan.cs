using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot_UnityLib;
using EpicLoot.CraftingV2;
using HarmonyLib;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

internal sealed class ActionPlan
{
    internal readonly List<(Item Item, int Amount)> Payments = new();
    internal readonly Dictionary<Item, int> Remaining = new();
    internal readonly HashSet<Container> Containers = new();
    internal readonly Dictionary<string, int> Costs;
    internal readonly string Options;
    internal static EnchantingFeature Feature(EnchantingTableUIPanelBase panel) =>
        panel is SacrificeUI ? EnchantingFeature.Sacrifice : panel is ConvertUI ? EnchantingFeature.ConvertMaterials :
        panel is EnchantUI ? EnchantingFeature.Enchant : panel is AugmentUI ? EnchantingFeature.Augment :
        panel is DisenchantUI ? EnchantingFeature.Disenchant : EnchantingFeature.Rune;
    internal static object Field(object panel, string name) => AccessTools.Field(panel.GetType(), name).GetValue(panel);
    private static List<InventoryItemListElement> Cost(string name, params object[] args) =>
        (List<InventoryItemListElement>)AccessTools.Method(typeof(EnchantingUIController), name).Invoke(null, args);
    internal static string Settings(EnchantingTableUIPanelBase panel)
    {
        string[] fields = panel is EnchantUI ? new[] { "_rarity" } : panel is AugmentUI ? new[] { "_augmentIndex" } :
            panel is RuneUI ? new[] { "_runeAction", "_selectedRarity", "_selectedEnchantmentIndex" } :
            panel is SacrificeUI ? new[] { "_sacrificeMode" } : panel is UpgradeTableUI ? new[] { "_selectedFeature" } : Array.Empty<string>();
        return string.Join("/", fields.Select(x => Field(panel, x).ToString())) +
            (panel is SacrificeUI s ? "/" + s.IdentifyStyle.value : "");
    }
    internal static List<Tuple<T, int>> ReadSelection<T>(EnchantingTableUIPanelBase panel) where T : class, IListElement
    {
        var selected = panel.AvailableItems ? panel.AvailableItems.GetSelectedItems<IListElement>() : null;
        if (selected == null || selected.Count == 0) throw new InvalidOperationException("Select an item or recipe first.");
        if (selected.Any(x => !(x.Item1 is T) || x.Item1.GetItem() == null || x.Item2 <= 0))
            throw new InvalidOperationException("The selection is not valid for this action. Please select it again.");
        return selected.Select(x => Tuple.Create((T)x.Item1, x.Item2)).ToList();
    }
    internal static Dictionary<string, int> CalculateCosts(EnchantingTableUIPanelBase panel)
    {
        var table = (EnchantingTable)Storage.Table!;
        Item RequiredItem()
        {
            var selected = ReadSelection<InventoryItemListElement>(panel);
            if (selected.Count != 1) throw new InvalidOperationException("Select one item for this action.");
            return selected[0].Item1.GetItem();
        }
        List<InventoryItemListElement> cost;
        if (panel is ConvertUI convert) cost = ConvertUI.GetConversionCost(ReadSelection<ConversionRecipeUnity>(convert));
        else if (panel is UpgradeTableUI)
        {
            int index = (int)Field(panel, "_selectedFeature");
            if (index < 0 || !table) throw new InvalidOperationException("Select a table feature first.");
            var feature = (EnchantingFeature)index;
            cost = table.IsFeatureLocked(feature) ? table.GetFeatureUnlockCost(feature) : table.GetFeatureUpgradeCost(feature);
        }
        else if (panel is EnchantUI) cost = Cost("GetEnchantCost", RequiredItem(), Field(panel, "_rarity"));
        else if (panel is AugmentUI) cost = Cost("GetAugmentCost", RequiredItem(), Field(panel, "_augmentIndex"));
        else if (panel is DisenchantUI) cost = Cost("GetDisenchantCost", RequiredItem());
        else if (panel is SacrificeUI sacrifice && Field(panel, "_sacrificeMode").ToString() == "Identify")
        {
            float value = table.GetFeatureCurrentValue(EnchantingFeature.Sacrifice).Item1;
            float reduction = float.IsNaN(value) || value == 0 ? 1 : 1 - value / 100;
            cost = Cost("GetIdentifyCostForCategory", sacrifice.IdentifyStyle.options[sacrifice.IdentifyStyle.value].text,
                ReadSelection<InventoryItemListElement>(sacrifice).Select(x => Tuple.Create(x.Item1.GetItem(), x.Item2)).ToList(), reduction);
        }
        else if (panel is SacrificeUI) { ReadSelection<InventoryItemListElement>(panel); cost = new List<InventoryItemListElement>(); }
        else if (panel is RuneUI)
        {
            float value = table.GetFeatureCurrentValue(EnchantingFeature.Rune).Item1;
            float reduction = float.IsNaN(value) || value == 0 ? 1 : 1 - value / 100;
            cost = Cost(Field(panel, "_runeAction").ToString() == "Etch" ? "GetRuneEtchCost" : "GetRuneExtractCost", RequiredItem(), Field(panel, "_selectedRarity"), reduction);
        }
        else throw new InvalidOperationException("Unsupported enchanting action: " + panel.GetType().Name);
        // Disenchant pays even with EpicLoot's no-cost cheat enabled.
        if (Player.m_localPlayer && Player.m_localPlayer.NoCostCheat() && !(panel is DisenchantUI)) return new Dictionary<string, int>();
        return cost.GroupBy(x => x.GetItem().m_shared.m_name).ToDictionary(x => x.Key, x => x.Sum(y => y.GetItem().m_stack));
    }
    internal ActionPlan(Plugin plugin, EnchantingTableUIPanelBase panel, IEnumerable<(Item Item, int Amount)> selected)
        : this(CalculateCosts(panel), Settings(panel), selected, plugin.Storage.All().OrderBy(x => plugin.Storage.Sources.TryGetValue(x, out var c) ?
            (c.transform.position - Storage.Table!.transform.position).sqrMagnitude : -1)
            .ThenBy(x => plugin.Storage.Sources.TryGetValue(x, out var c) ? Storage.Id(c).ToString() : "").ToArray(),
            item => plugin.Storage.Sources.TryGetValue(item, out var c) ? c : null) { }
    internal ActionPlan(Dictionary<string, int> costs, string options, IEnumerable<(Item Item, int Amount)> selected,
        IEnumerable<Item> orderedItems, Func<Item, Container?> source)
    {
        Costs = costs; Options = options;
        var chosen = selected.ToArray();
        var all = orderedItems.ToArray();
        void Include(Item item, int amount)
        {
            Remaining.TryGetValue(item, out int prior); Remaining[item] = checked(prior + amount);
            var container = source(item); if (container) Containers.Add(container);
        }
        foreach (var entry in chosen)
        {
            if (entry.Amount <= 0 || entry.Amount > entry.Item.m_stack) throw new InvalidOperationException("The selected quantity changed.");
            Include(entry.Item, entry.Amount);
        }
        foreach (var cost in Costs)
        {
            int needed = cost.Value;
            foreach (var candidate in all.Where(x => x.m_shared.m_name == cost.Key))
            {
                Remaining.TryGetValue(candidate, out int reserved);
                int take = Math.Min(needed, Math.Max(0, candidate.m_stack - reserved));
                if (take == 0) continue;
                Payments.Add((candidate, take)); Include(candidate, take); needed -= take;
                if (needed == 0) break;
            }
            if (needed != 0) throw new InvalidOperationException("Missing available materials: " + cost.Key);
        }
    }
    internal void Consume(Item item, int amount)
    {
        if (!Remaining.TryGetValue(item, out int remaining) || amount > remaining)
            throw new InvalidOperationException("The action attempted an unplanned withdrawal.");
        Remaining[item] = remaining - amount;
    }
    internal void Rebind(Func<Item, Item> resolve)
    {
        var remaining = Remaining.Select(x => (Item: resolve(x.Key), Amount: x.Value)).ToArray();
        Remaining.Clear(); foreach (var entry in remaining) Remaining.Add(entry.Item, entry.Amount);
        for (int i = 0; i < Payments.Count; i++) Payments[i] = (resolve(Payments[i].Item), Payments[i].Amount);
    }
    internal void Withdraw(Storage storage, string name, int amount) => Withdraw(name, amount, (item, quantity) => storage.RemoveExact(item, quantity));
    internal void Withdraw(string name, int amount, Action<Item, int> remove)
    {
        if (Payments.Where(x => x.Item.m_shared.m_name == name).Sum(x => x.Amount) < amount)
            throw new InvalidOperationException("The action cost changed while waiting.");
        for (int i = 0; i < Payments.Count && amount > 0; i++)
        {
            var entry = Payments[i]; if (entry.Item.m_shared.m_name != name) continue;
            int take = Math.Min(amount, entry.Amount); if (take == 0) continue;
            remove(entry.Item, take); Payments[i] = (entry.Item, entry.Amount - take); amount -= take;
        }
    }
}
