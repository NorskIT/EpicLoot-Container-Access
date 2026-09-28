using System;
using System.Collections.Generic;
using System.Linq;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootContainerAccess.RuntimeSmoke;

internal static class ConversionRegression
{
    internal static void Run(GameObject iron)
    {
        var root = new GameObject("Conversion regression"); root.SetActive(false);
        try
        {
            var panel = root.AddComponent<ConvertUI>();
            var list = root.AddComponent<MultiSelectItemList>();
            var content = new GameObject("Content"); content.transform.SetParent(root.transform);
            list.ListContainer = content.transform; panel.AvailableItems = list;
            var material = iron.GetComponent<ItemDrop>().m_itemData.Clone(); material.m_dropPrefab = iron;
            var recipe = new ConversionRecipeUnity { Product = material, Amount = 1,
                Cost = new List<ConversionRecipeCostUnity> { new ConversionRecipeCostUnity { Item = material, Amount = 2 } } };
            var row = new GameObject("Selected recipe"); row.transform.SetParent(content.transform);
            var element = row.AddComponent<MultiSelectItemListElement>();
            AccessTools.Field(typeof(MultiSelectItemListElement), "_item").SetValue(element, recipe);
            AccessTools.Field(typeof(MultiSelectItemListElement), "_selectedQuantity").SetValue(element, 1);
            void Check(bool ok, string message) { if (!ok) throw new Exception("Conversion regression: " + message); }
            void Rejected(Action action, string text)
            {
                try { action(); } catch (InvalidOperationException e) { Check(e.Message.Contains(text), "unexpected rejection: " + e.Message); return; }
                throw new Exception("Expected a clear rejection for invalid selection.");
            }
            Check(ActionPlan.CalculateCosts(panel)[material.m_shared.m_name] == 2, "one recipe must cost two materials");
            AccessTools.Field(typeof(MultiSelectItemListElement), "_selectedQuantity").SetValue(element, 3);
            Check(ActionPlan.CalculateCosts(panel)[material.m_shared.m_name] == 6, "three recipes must cost six materials");
            var secondRow = new GameObject("Second recipe"); secondRow.transform.SetParent(content.transform);
            var secondElement = secondRow.AddComponent<MultiSelectItemListElement>();
            var secondRecipe = new ConversionRecipeUnity { Product = material, Amount = 2,
                Cost = new List<ConversionRecipeCostUnity> { new ConversionRecipeCostUnity { Item = material, Amount = 5 } } };
            AccessTools.Field(typeof(MultiSelectItemListElement), "_item").SetValue(secondElement, secondRecipe);
            AccessTools.Field(typeof(MultiSelectItemListElement), "_selectedQuantity").SetValue(secondElement, 2);
            Check(ActionPlan.CalculateCosts(panel)[material.m_shared.m_name] == 16, "multiple selected recipes must aggregate their costs");
            Check(ConvertUI.GetConversionProducts(ActionPlan.ReadSelection<ConversionRecipeUnity>(panel)).Single().GetItem().m_stack == 7, "batch product quantity must remain correct");
            secondRow.SetActive(false);
            AccessTools.Field(typeof(MultiSelectItemListElement), "_selectedQuantity").SetValue(element, 0);
            Rejected(() => ActionPlan.CalculateCosts(panel), "Select an item or recipe");
            AccessTools.Field(typeof(MultiSelectItemListElement), "_selectedQuantity").SetValue(element, 1);
            AccessTools.Field(typeof(MultiSelectItemListElement), "_item").SetValue(element, new InventoryItemListElement { Item = material });
            Rejected(() => ActionPlan.CalculateCosts(panel), "not valid for this action");
            var sacrifice = root.AddComponent<SacrificeUI>(); sacrifice.AvailableItems = list;
            var mode = AccessTools.Field(typeof(SacrificeUI), "_sacrificeMode"); mode.SetValue(sacrifice, Enum.Parse(mode.FieldType, "Sacrifice"));
            Check(ActionPlan.CalculateCosts(sacrifice).Count == 0, "inventory-based sacrifice must retain its cost branch");
            var enchant = root.AddComponent<EnchantUI>(); enchant.AvailableItems = list;
            var swordPrefab = ObjectDB.instance.GetItemPrefab("SwordIron");
            var sword = swordPrefab.GetComponent<ItemDrop>().m_itemData.Clone(); sword.m_dropPrefab = swordPrefab;
            AccessTools.Field(typeof(MultiSelectItemListElement), "_item").SetValue(element, new InventoryItemListElement { Item = sword });
            Check(ActionPlan.CalculateCosts(enchant).Count > 0, "inventory-based enchanting must calculate its costs");
            AccessTools.Field(typeof(MultiSelectItemListElement), "_item").SetValue(element, recipe);
            Rejected(() => ActionPlan.CalculateCosts(enchant), "not valid for this action");
            var upgrade = root.AddComponent<UpgradeTableUI>(); upgrade.AvailableItems = list;
            AccessTools.Field(typeof(UpgradeTableUI), "_selectedFeature").SetValue(upgrade, -1);
            Rejected(() => ActionPlan.CalculateCosts(upgrade), "Select a table feature");

        }
        finally { UnityEngine.Object.Destroy(root); }
    }
}
