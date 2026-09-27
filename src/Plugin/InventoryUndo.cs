using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using EpicLoot.Data;
using HarmonyLib;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

// A synchronous undo point, never a persistent backup or a network recovery mechanism.
// Preserve item identity: Player and equipment-slot mods hold references to equipped items.
internal sealed class InventoryUndo
{
    private readonly Inventory inventory;
    private readonly List<(Item Live, Item Copy, string? Magic)> items;
    private static readonly FieldInfo[] Fields = typeof(Item).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => !f.IsInitOnly).ToArray();
    internal InventoryUndo(Inventory inventory)
    {
        this.inventory = inventory;
        items = inventory.GetAllItems().Select(item => (item, item.Clone(), (string?)EpicLoot.API.GetMagicItemJson(item))).ToList();
    }
    internal void Restore()
    {
        var list = inventory.GetAllItems(); list.Clear();
        foreach (var saved in items)
        {
            saved.Live.Data().Remove<MagicItemComponent>();
            foreach (var field in Fields) field.SetValue(saved.Live, field.GetValue(saved.Copy));
            saved.Live.m_customData = new Dictionary<string, string>(saved.Copy.m_customData);
            if (saved.Magic != null && !EpicLoot.API.ApplyMagicItemJson(saved.Live, saved.Magic))
                throw new InvalidOperationException("Could not restore the selected item's enchantments.");
            list.Add(saved.Live);
        }
        AccessTools.Method(typeof(Inventory), "Changed").Invoke(inventory, new object[] { false, false });
    }
}
