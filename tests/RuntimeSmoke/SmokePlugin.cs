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
            var packet = new ZPackage(); packet.Write("request"); packet.Write(new ZDOID(45, 90)); packet.SetPos(0);
            if (packet.ReadString() != "request" || packet.ReadZDOID() != new ZDOID(45, 90)) throw new Exception("Local RPC package roundtrip failed.");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS\nPlugin loaded; inventory/sacrifice providers registered; 7 action panels guarded; " + patched.Length + " Harmony targets patched; inventory snapshots stable; 10,000-item withdrawal correct; equipped/chest-row protections correct; undo preserves item identity, oversized stacks and cached magic data; RPC package roundtrip correct.\n" + drawers + "\nNo world or server was opened.\n");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL\n" + e); Logger.LogError(e); }
        Application.Quit();
    }
}
