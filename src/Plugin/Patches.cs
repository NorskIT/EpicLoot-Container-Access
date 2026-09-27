using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot_UnityLib;
using HarmonyLib;
using Item = ItemDrop.ItemData;

namespace EpicLootContainerAccess;

[HarmonyPatch(typeof(Container), "Awake")]
internal static class TrackContainer
{
    private static void Postfix(Container __instance) => Plugin.Instance?.Storage?.Add(__instance);
}
[HarmonyPatch(typeof(Container), "OnDestroyed")]
internal static class UntrackContainer
{
    private static void Prefix(Container __instance) => Plugin.Instance?.Storage?.Remove(__instance);
}
[HarmonyPatch]
internal static class ProtectOpening
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Container), "Interact");
        yield return AccessTools.Method(typeof(Container), "TakeAll");
    }
    private static bool Prefix(Container __instance, ref bool __result)
    {
        if (!Plugin.Instance.Ready || !Plugin.Instance.Network.AnyReservation(Storage.Id(__instance))) return true;
        __result = false; Plugin.Instance.Notice("Storage is being used by the enchanting table."); return false;
    }
}
[HarmonyPatch]
internal static class ProtectRemoteOpening
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "RPC_RequestOpen", "RPC_RequestTakeAll", "RPC_RequestStack" })
        {
            var method = AccessTools.Method(typeof(Container), name);
            if (method != null) yield return method;
        }
    }
    private static bool Prefix(Container __instance) => !Plugin.Instance.Ready || !Plugin.Instance.Network.AnyReservation(Storage.Id(__instance));
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.GetAllItems))]
internal static class FilterItems
{
    private static void Postfix(ref List<Item> __result)
    {
        if (Plugin.Instance.Ready && Storage.Table && __result != null)
            __result = __result.Where(x => x != null && !Plugin.Instance.Storage.Protected(x)).ToList();
    }
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.CountItem), new[] { typeof(string) })]
internal static class CountItems
{
    private static bool Prefix(string __0, ref int __result)
    {
        if (!Plugin.Instance.Ready || !Storage.Table) return true;
        __result = Plugin.Instance.Storage.Total(__0); return false;
    }
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.RemoveItem), new[] { typeof(string), typeof(int) })]
internal static class ConsumeItems
{
    private static bool Prefix(string __0, int __1)
    {
        if (!Plugin.Instance.Ready || !Storage.Table) return true;
        Plugin.Instance.Storage.Withdraw(__0, __1, true); return false;
    }
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.RemoveExactItem))]
internal static class ConsumeExact
{
    private static bool Prefix(Item __0, int __1, ref int __result)
    {
        if (!Plugin.Instance.Ready || !Storage.Table) return true;
        __result = Plugin.Instance.Storage.RemoveExact(__0, __1); return false;
    }
}
[HarmonyPatch]
internal static class CoordinateAction
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(EnchantingTableUIPanelBase).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && typeof(EnchantingTableUIPanelBase).IsAssignableFrom(x)))
        {
            var method = type.GetMethod("DoMainAction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (method != null) yield return method;
        }
    }
    private static bool Prefix(EnchantingTableUIPanelBase __instance, MethodBase __originalMethod) => Plugin.Instance.Actions.Intercept(__instance, __originalMethod);
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.GiveItem), new[] { typeof(Item) })]
internal static class DeferItemOutput
{
    private static bool Prefix(Item __0, ref bool __result)
    {
        if (!Plugin.Instance.Actions.Executing) return true;
        Plugin.Instance.Actions.Queue(__0); __result = true; return false;
    }
}
[HarmonyPatch(typeof(InventoryManagement), nameof(InventoryManagement.GiveItem), new[] { typeof(string), typeof(int) })]
internal static class DeferNamedOutput
{
    private static bool Prefix(string __0, int __1)
    {
        if (!Plugin.Instance.Actions.Executing) return true;
        Plugin.Instance.Actions.Queue(__0, __1); return false;
    }
}
[HarmonyPatch(typeof(AugmentChoiceDialog), nameof(AugmentChoiceDialog.Show))]
internal static class GuardAugmentChoice
{
    private static void Prefix(AugmentChoiceDialog __instance, Item __0, ref Action<Item, int, MagicItemEffect> __2) => Plugin.Instance.Actions.WrapChoice(__instance, __0, ref __2);
}
