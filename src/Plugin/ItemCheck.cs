using System;
using System.Linq;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

// Check only the input being used, not unrelated contents of the player's inventory or chest.
internal sealed class ItemCheck
{
    private readonly Inventory inventory;
    private readonly Item original;
    private readonly Vector2i position;
    private readonly byte[] data;
    private readonly int amount;
    internal ItemCheck(Inventory inventory, Item item, int amount)
    {
        this.inventory = inventory; original = item; position = item.m_gridPos;
        data = Identity(item); this.amount = amount;
    }
    private static byte[] Identity(Item item)
    {
        var copy = item.Clone(); copy.m_stack = 1; copy.m_gridPos = new Vector2i(0, 0);
        var pkg = new ZPackage(); copy.Save(pkg); return pkg.GetArray();
    }
    internal Item Resolve()
    {
        var item = inventory.ContainsItem(original) ? original : inventory.GetAllItems().SingleOrDefault(x => x.m_gridPos == position && data.SequenceEqual(Identity(x)));
        if (item == null || item.m_stack < amount || !data.SequenceEqual(Identity(item)))
            throw new InvalidOperationException("A required item changed or is no longer available. Please select it again.");
        return item;
    }
}
