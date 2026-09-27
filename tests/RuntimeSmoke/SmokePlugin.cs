using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace EpicLootContainerAccess.RuntimeSmoke;

[BepInPlugin("norskit.epiclootcontaineraccess.smoke", "ECA Isolated Smoke Test", "0.1.0")]
[BepInDependency(Plugin.Id)]
public sealed class SmokePlugin : BaseUnityPlugin
{
    private IEnumerator Start()
    {
        var directory = Environment.GetEnvironmentVariable("ECA_SMOKE_OUTPUT");
        if (string.IsNullOrEmpty(directory)) yield break;
        yield return new WaitForSecondsRealtime(18);
        try
        {
            var plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (!(bool)typeof(Plugin).GetField("Ready", flags)!.GetValue(plugin)) throw new Exception("Integration did not enable.");
            var providers = EpicLoot.API.GetRegisteredProviders();
            if (providers["Inventory"].Count(x => x == Plugin.Id) != 1) throw new Exception("Inventory provider registration failed.");
            if (!providers["Sacrifice"].Contains(Plugin.Id)) throw new Exception("Sacrifice protection not registered.");
            var patched = Harmony.GetAllPatchedMethods().Where(x => Harmony.GetPatchInfo(x).Owners.Contains(Plugin.Id)).ToArray();
            if (patched.Count(x => x.Name == "DoMainAction") != 7) throw new Exception("Not all seven table action panels are guarded.");
            string drawers = "RossItemDrawers absent: optional dependency behavior confirmed.";
            var drawerPlugin = Chainloader.PluginInfos.Values.FirstOrDefault(x => x.Metadata.GUID == "com.rossdwest.itemdrawers");
            if (drawerPlugin != null)
            {
                if (!drawerPlugin.Instance.GetType().Assembly.GetTypes().Any(x => x != typeof(Container) && typeof(Container).IsAssignableFrom(x)))
                    throw new Exception("RossItemDrawers no longer exposes a Container subclass.");
                drawers = "RossItemDrawers " + drawerPlugin.Metadata.Version + " loaded alongside ECA; Container inheritance verified (not a network persistence test).";
            }
            var iron = ObjectDB.instance.GetItemPrefab("Iron");
            if (!iron) throw new Exception("Iron prefab not available in menu ObjectDB.");
            var inv = new Inventory("ECA synthetic drawer", null, 4, 4);
            var item = iron.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = iron; item.m_stack = 10000;
            inv.GetAllItems().Add(item);
            var before = new ZPackage(); inv.Save(before);
            var again = new ZPackage(); inv.Save(again);
            if (!before.GetArray().SequenceEqual(again.GetArray())) throw new Exception("Inventory snapshot is not deterministic.");
            inv.RemoveItem(item, 7500);
            if (inv.CountItems(item.m_shared.m_name) != 2500) throw new Exception("Oversized drawer withdrawal lost items.");
            var storage = typeof(Plugin).GetField("Storage", flags)!.GetValue(plugin);
            var protect = storage.GetType().GetMethod("Protected", flags)!;
            item.m_gridPos = new Vector2i(0, 0);
            if ((bool)protect.Invoke(storage, new object[] { item })) throw new Exception("Chest top row treated as hotbar.");
            item.m_equipped = true;
            if (!(bool)protect.Invoke(storage, new object[] { item })) throw new Exception("Equipped item not protected.");
            var undoType = typeof(Plugin).Assembly.GetType("EpicLootContainerAccess.InventoryUndo")!;
            var undo = Activator.CreateInstance(undoType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { inv }, null);
            item.m_stack = 1; item.m_equipped = false; item.m_customData["eca-test"] = "changed";
            inv.GetAllItems().Clear();
            undoType.GetMethod("Restore", flags)!.Invoke(undo, null);
            if (!ReferenceEquals(inv.GetAllItems().Single(), item) || item.m_stack != 2500 || !item.m_equipped || item.m_customData.ContainsKey("eca-test"))
                throw new Exception("Synchronous undo did not preserve item identity and data.");
            var swordPrefab = ObjectDB.instance.GetItemPrefab("SwordIron");
            var sword = swordPrefab.GetComponent<ItemDrop>().m_itemData.Clone(); sword.m_dropPrefab = swordPrefab;
            EpicLoot.API.ApplyMagicItemJson(sword, "{\"Rarity\":1,\"Effects\":[]}");
            inv.GetAllItems().Add(sword);
            string magic = EpicLoot.API.GetMagicItemJson(sword);
            undo = Activator.CreateInstance(undoType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { inv }, null);
            EpicLoot.API.ApplyMagicItemJson(sword, "{\"Rarity\":3,\"Effects\":[]}");
            undoType.GetMethod("Restore", flags)!.Invoke(undo, null);
            if (EpicLoot.API.GetMagicItemJson(sword) != magic) throw new Exception("Undo did not restore cached enchantment data.");
            CheckBaselines(iron, swordPrefab);
            var packet = new ZPackage(); packet.Write("request"); packet.Write(new ZDOID(45, 90)); packet.SetPos(0);
            if (packet.ReadString() != "request" || packet.ReadZDOID() != new ZDOID(45, 90)) throw new Exception("Local RPC package roundtrip failed.");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS\nPlugin loaded; inventory/sacrifice providers registered; 7 action panels guarded; " + patched.Length + " Harmony targets patched; inventory snapshots stable; 10,000-item withdrawal correct; equipped/chest-row protections correct; undo preserves item identity, oversized stacks and cached magic data; RPC package roundtrip correct; 10 inventory baseline regression checks passed (old false rejection reproduced, protected changes accepted, material/protection/container changes rejected).\n" + drawers + "\nNo world or server was opened.\n");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL\n" + e); Logger.LogError(e); }
        Application.Quit();
    }

    private static void CheckBaselines(GameObject iron, GameObject swordPrefab)
    {
        var type = typeof(Plugin).Assembly.GetType("EpicLootContainerAccess.InventoryBaseline")!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var inv = new Inventory("Validation regression", null, 8, 4);
        ItemDrop.ItemData Add(GameObject prefab, int x, int y, bool equipped = false)
        {
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_gridPos = new Vector2i(x, y); item.m_equipped = equipped;
            inv.GetAllItems().Add(item); return item;
        }
        object Capture(bool player = true) => Activator.CreateInstance(type, flags, null, new object[] { inv, player, player ? "Player inventory" : "Container test" }, null)!;
        string? Diff(object snapshot) => (string?)type.GetMethod("Difference", flags)!.Invoke(snapshot, new object[] { inv });
        void Check(bool condition, string name) { if (!condition) throw new Exception("Baseline regression: " + name); }
        var material = Add(iron, 0, 1); material.m_stack = 20;
        var equipped = Add(swordPrefab, 1, 1, true);
        var hotbar = Add(swordPrefab, 0, 0);
        var snapshot = Capture();
        var old = new ZPackage(); inv.Save(old);
        equipped.m_durability -= 1; hotbar.m_durability -= 1;
        equipped.m_customData["eca-passive"] = "changed";
        var changed = new ZPackage(); inv.Save(changed);
        Check(!old.GetArray().SequenceEqual(changed.GetArray()), "old full-inventory check reproduces false rejection");
        Check(Diff(snapshot) == null, "protected durability/custom data must not reject");
        material.m_stack--;
        Check(Diff(snapshot)?.Contains("stack") == true, "material count must reject"); material.m_stack++;
        material.m_gridPos = new Vector2i(2, 0);
        Check(Diff(snapshot) != null, "move material to hotbar must reject"); material.m_gridPos = new Vector2i(0, 1);
        material.m_equipped = true;
        Check(Diff(snapshot) != null, "equip available item must reject"); material.m_equipped = false;
        inv.GetAllItems().Remove(material);
        Check(Diff(snapshot) != null, "removed material must reject"); inv.GetAllItems().Insert(0, material);
        material.m_customData["eca-change"] = "value";
        Check(Diff(snapshot)?.Contains("custom data") == true, "available item metadata must reject"); material.m_customData.Remove("eca-change");
        material.m_durability -= 1;
        Check(Diff(snapshot)?.Contains("durability") == true, "available item durability must reject");
        var container = Capture(false);
        hotbar.m_stack++;
        Check(Diff(container)?.Contains("Container test") == true, "container top row must remain validated");
        container = Capture(false); equipped.m_durability -= 1;
        Check(Diff(container) != null, "container equipped items must remain validated");
    }
}
