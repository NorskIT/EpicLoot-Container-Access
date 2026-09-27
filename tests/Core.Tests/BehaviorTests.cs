using System;
using System.Linq;
using EpicLootContainerAccess.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EpicLootContainerAccess.Tests;

[TestClass]
public class BehaviorTests
{
    [TestMethod]
    public void AllEightHotbarSlotsAreProtectedButChestTopRowIsUsable()
    {
        for (int x = 0; x < 8; x++)
        {
            Assert.IsTrue(Rules.Protected(false, true, x, 0));
            Assert.IsFalse(Rules.Protected(false, false, x, 0));
        }
        Assert.IsFalse(Rules.Protected(false, true, 0, 1));
        Assert.IsFalse(Rules.Protected(false, true, 8, 0));
    }
    [TestMethod]
    public void EquippedFlagProtectsExtraEquipmentSlotsRegardlessOfCoordinates()
    {
        foreach (int x in new[] { -1, 0, 7, 20 }) foreach (int y in new[] { -1, 0, 5, 20 })
            Assert.IsTrue(Rules.Protected(true, true, x, y));
        Assert.IsTrue(Rules.Protected(true, false, 3, 5));
    }
    [TestMethod]
    public void InventoryMoveChangesProtectionImmediately()
    {
        Assert.IsFalse(Rules.Protected(false, true, 3, 1));
        Assert.IsTrue(Rules.Protected(false, true, 3, 0));
        Assert.IsTrue(Rules.Protected(true, true, 3, 1));
    }
    [TestMethod]
    public void RangeIncludesBoundaryAndRejectsInvalidValues()
    {
        Assert.IsTrue(Rules.InRange(100, 10));
        Assert.IsFalse(Rules.InRange(100.01, 10));
        Assert.IsFalse(Rules.InRange(double.NaN, 10));
        Assert.IsFalse(Rules.InRange(double.PositiveInfinity, 10));
        Assert.IsFalse(Rules.InRange(1, double.NaN));
        Assert.IsFalse(Rules.InRange(1, 101));
    }
    [TestMethod]
    public void LargeDrawerTotalsNeverWrapNegative()
    {
        Assert.AreEqual(int.MaxValue, Rules.Sum(new[] { int.MaxValue, 10000 }));
        Assert.AreEqual(30000, Rules.Sum(new[] { 10000, -1, 20000 }));
    }
    [TestMethod]
    public void WithdrawalSpansPlayerChestAndDrawerInOrder()
    {
        CollectionAssert.AreEqual(new[] { 2, 3, 7 }, Rules.Allocate(new[] { 2, 3, 10000 }, 12));
        CollectionAssert.AreEqual(new[] { 0, 0 }, Rules.Allocate(new[] { 0, 0 }, 0));
    }
    [TestMethod]
    public void InsufficientMaterialsProduceNoPartialPlan()
    {
        Assert.Throws<InvalidOperationException>(() => Rules.Allocate(new[] { 2, 3 }, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Allocate(new[] { 2 }, -1));
    }
    [TestMethod]
    public void AllocationsConserveMaterialsAcrossRandomInventories()
    {
        var random = new Random(93);
        for (int n = 0; n < 2000; n++)
        {
            var stocks = Enumerable.Range(0, random.Next(1, 40)).Select(_ => random.Next(0, 10000)).ToArray();
            int request = random.Next(0, stocks.Sum() + 1);
            var take = Rules.Allocate(stocks, request);
            Assert.AreEqual(request, take.Sum());
            for (int i = 0; i < take.Length; i++) Assert.IsTrue(take[i] >= 0 && take[i] <= stocks[i]);
        }
    }
}
