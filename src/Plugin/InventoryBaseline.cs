using System;
using System.Collections.Generic;
using System.Linq;
using EpicLootContainerAccess.Core;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

// Validation only. Rollback must still capture the full inventory immediately before execution.
internal sealed class InventoryBaseline
{
    private readonly bool player;
    private readonly string source;
    private readonly Entry[] entries;
    private sealed class Entry
    {
        internal readonly Item Copy;
        internal readonly byte[] Bytes;
        internal Entry(Item item) { Copy = item.Clone(); Copy.m_customData = new Dictionary<string, string>(item.m_customData); Bytes = Serialize(item); }
    }
    internal InventoryBaseline(Inventory inventory, bool player, string source)
    {
        this.player = player; this.source = source;
        entries = Available(inventory).Select(x => new Entry(x)).ToArray();
    }
    private IEnumerable<Item> Available(Inventory inventory) => inventory.GetAllItems()
        .Where(x => !player || !Rules.Protected(x.m_equipped, true, x.m_gridPos.x, x.m_gridPos.y));
    private static byte[] Serialize(Item item) { var pkg = new ZPackage(); item.Save(pkg); return pkg.GetArray(); }
    internal string? Difference(Inventory inventory)
    {
        var current = Available(inventory).ToArray();
        if (current.Length != entries.Length) return $"{source}: eligible stack count {entries.Length} -> {current.Length} (item added, removed or protection changed)";
        for (int i = 0; i < current.Length; i++)
        {
            if (entries[i].Bytes.SequenceEqual(Serialize(current[i]))) continue;
            var a = entries[i].Copy; var b = current[i];
            var fields = new List<string>();
            if (a.m_dropPrefab != b.m_dropPrefab) fields.Add("prefab");
            if (a.m_stack != b.m_stack) fields.Add("stack");
            if (a.m_durability != b.m_durability) fields.Add("durability");
            if (a.m_gridPos != b.m_gridPos) fields.Add("position");
            if (a.m_equipped != b.m_equipped) fields.Add("equipped");
            if (a.m_quality != b.m_quality) fields.Add("quality");
            if (a.m_variant != b.m_variant) fields.Add("variant");
            if (a.m_customData.Count != b.m_customData.Count || a.m_customData.Any(x => !b.m_customData.TryGetValue(x.Key, out var value) || value != x.Value)) fields.Add("custom data");
            if (fields.Count == 0) fields.Add("serialized item data");
            return $"{source}: stack {i} ({a.m_shared.m_name}), changed {string.Join(", ", fields)}";
        }
        return null;
    }
}
