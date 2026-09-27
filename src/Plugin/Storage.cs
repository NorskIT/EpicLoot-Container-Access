using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;
using EpicLootContainerAccess.Core;
using Item = ItemDrop.ItemData;
using Object = UnityEngine.Object;

namespace EpicLootContainerAccess;

internal sealed class Storage : IDisposable
{
    private readonly Plugin plugin;
    private readonly HashSet<Container> registered = new();
    private readonly Dictionary<Inventory, Action> subscriptions = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly List<Item> items = new();
    internal readonly Dictionary<Item, Container> Sources = new();
    internal readonly List<Container> Nearby = new();
    private bool dirty = true;
    private float nextRefresh;
    private Component? table;
    internal long Rebuilds { get; private set; }
    internal Storage(Plugin plugin) { this.plugin = plugin; }
    internal static Component? Table => EnchantingTableUI.instance && EnchantingTableUI.instance.gameObject.activeInHierarchy
        ? EnchantingTableUI.instance.SourceTable : null;
    internal static ZNetView? View(Container c) => c.m_rootObjectOverride ? c.m_rootObjectOverride : c.GetComponent<ZNetView>();
    internal static ZDOID Id(Container c) => View(c)?.GetZDO()?.m_uid ?? ZDOID.None;
    internal static byte[] Snapshot(Inventory inventory) { var pkg = new ZPackage(); inventory.Save(pkg); return pkg.GetArray(); }
    internal static void Save(Container c) => AccessTools.Method(typeof(Container), "Save").Invoke(c, null);
    internal static void Load(Container c) => AccessTools.Method(typeof(Container), "Load").Invoke(c, null);
    internal void Bootstrap()
    {
        // Once at plugin startup only. Scene objects created later register through Container.Awake.
        foreach (var c in Object.FindObjectsByType<Container>(FindObjectsSortMode.None)) Add(c);
    }
    internal void Add(Container c) { if (c && registered.Add(c)) dirty = true; }
    internal void Remove(Container c) { registered.Remove(c); dirty = true; }
    internal void Invalidate() => dirty = true;
    internal void Tick()
    {
        if (Table != table)
        {
            table = Table;
            dirty = true;
            if (!table) ClearCache();
        }
        if (table && Time.unscaledTime >= nextRefresh) dirty = true;
    }
    internal bool Protected(Item item)
    {
        bool local = Player.m_localPlayer && Player.m_localPlayer.GetInventory().ContainsItem(item);
        return Rules.Protected(item.m_equipped, local, item.m_gridPos.x, item.m_gridPos.y);
    }
    internal bool Eligible(Container c, bool allowOwnReservation = false)
    {
        var current = Table;
        if (!current || !Player.m_localPlayer || !c || !c.gameObject.activeInHierarchy || c.GetComponent<TombStone>()) return false;
        var view = View(c);
        if (!view || !view.IsValid() || c.GetInventory() == null) return false;
        if (!Rules.InRange((c.transform.position - current.transform.position).sqrMagnitude, plugin.Network.Range)) return false;
        if (c.IsInUse() || view.GetZDO().GetInt(ZDOVars.s_inUse) != 0 || (c.m_wagon && c.m_wagon.InUse())) return false;
        if (plugin.Network.ReservedByOther(Id(c))) return false;
        return Access(c, Player.m_localPlayer.GetPlayerID());
    }
    internal static bool Access(Container c, long playerId)
    {
        if (!(bool)AccessTools.Method(typeof(Container), "CheckAccess").Invoke(c, new object[] { playerId })) return false;
        // Local player's ward permissions. Remote owner handshake also checks CheckAccess.
        return !c.m_checkGuardStone || PrivateArea.CheckAccess(c.transform.position, 0, false);
    }
    internal void Refresh(bool force = false)
    {
        if (Table != table) { table = Table; dirty = true; }
        if (!table || !plugin.Network.ConfigReady) { ClearCache(); return; }
        if (!force && !dirty) return;
        var watch = Stopwatch.StartNew();
        ClearCache();
        foreach (var c in registered.ToArray())
        {
            if (!c) { registered.Remove(c!); continue; }
            if (!Eligible(c)) continue;
            var inv = c.GetInventory();
            Nearby.Add(c);
            if (!subscriptions.ContainsKey(inv))
            {
                Action changed = Invalidate;
                inv.m_onChanged += changed;
                subscriptions.Add(inv, changed);
            }
            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_stack <= 0 || Protected(item) || Sources.ContainsKey(item)) continue;
                Sources.Add(item, c);
                items.Add(item);
                counts.TryGetValue(item.m_shared.m_name, out int n);
                counts[item.m_shared.m_name] = Rules.Sum(new[] { n, item.m_stack });
            }
        }
        dirty = false;
        nextRefresh = Time.unscaledTime + .25f;
        Rebuilds++;
        plugin.Trace($"Storage rebuild {Rebuilds}: {Nearby.Count} containers, {items.Count} stacks, {watch.Elapsed.TotalMilliseconds:F2} ms");
    }
    private void ClearCache()
    {
        foreach (var pair in subscriptions) pair.Key.m_onChanged -= pair.Value;
        subscriptions.Clear();
        items.Clear(); counts.Clear(); Sources.Clear(); Nearby.Clear();
    }
    internal List<Item> Items() { Refresh(); return new List<Item>(items); }
    internal int Count(string name) { Refresh(); return counts.TryGetValue(name, out int n) ? n : 0; }
    internal List<Item> All()
    {
        var result = Player.m_localPlayer ? Player.m_localPlayer.GetInventory().GetAllItems().Where(x => !Protected(x)).ToList() : new List<Item>();
        foreach (var item in Items()) if (!result.Contains(item)) result.Add(item);
        return result;
    }
    internal int Total(string name)
    {
        int fromContainers = Count(name);
        int fromPlayer = Player.m_localPlayer ? Rules.Sum(Player.m_localPlayer.GetInventory().GetAllItems()
            .Where(x => x.m_shared.m_name == name && !Protected(x)).Select(x => x.m_stack)) : 0;
        return Rules.Sum(new[] { fromContainers, fromPlayer });
    }
    internal bool HasLive(Item item)
    {
        if (Protected(item)) return false;
        if (Player.m_localPlayer && Player.m_localPlayer.GetInventory().ContainsItem(item)) return true;
        Refresh();
        return Sources.TryGetValue(item, out var c) && Eligible(c) && c.GetInventory().ContainsItem(item);
    }
    internal int Remove(string name, int amount) => Withdraw(name, amount, false);
    internal int Withdraw(string name, int amount, bool includePlayer)
    {
        if (!plugin.Actions.Executing) throw new InvalidOperationException("Storage withdrawals require a coordinated table action.");
        Refresh(true);
        var candidates = (includePlayer ? All() : Items()).Where(x => x.m_shared.m_name == name && !Protected(x)).ToList();
        int[] take = Rules.Allocate(candidates.Select(x => x.m_stack).ToArray(), amount);
        for (int i = 0; i < take.Length; i++) if (take[i] > 0) RemoveExact(candidates[i], take[i]);
        return amount;
    }
    internal int RemoveExact(Item item, int amount)
    {
        if (!plugin.Actions.Executing || !HasLive(item) || amount <= 0 || item.m_stack < amount)
            throw new InvalidOperationException("The selected item is no longer available or is protected.");
        Inventory inventory;
        if (Player.m_localPlayer.GetInventory().ContainsItem(item)) inventory = Player.m_localPlayer.GetInventory();
        else
        {
            var c = Sources[item];
            if (!plugin.Network.OwnsReservation(Id(c)) || !View(c)!.IsOwner()) throw new InvalidOperationException("Storage ownership changed.");
            inventory = c.GetInventory();
        }
        int before = item.m_stack;
        inventory.RemoveItem(item, amount);
        int taken = before - (inventory.ContainsItem(item) ? item.m_stack : 0);
        if (taken != amount) throw new InvalidOperationException("Incomplete material withdrawal.");
        dirty = true;
        return taken;
    }
    internal void DisposeWorld() { ClearCache(); registered.RemoveWhere(x => !x); dirty = true; table = null; }
    public void Dispose() { ClearCache(); registered.Clear(); }
}
