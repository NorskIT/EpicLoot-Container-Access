using System;
using System.Collections.Generic;
using System.Linq;

namespace EpicLootContainerAccess.Core;

public static class Rules
{
    public static bool Protected(bool equipped, bool playerInventory, int x, int y) =>
        equipped || (playerInventory && y == 0 && x >= 0 && x < 8);

    public static bool InRange(double squaredDistance, double radius) =>
        !double.IsNaN(squaredDistance) && squaredDistance >= 0 && radius >= 1 && radius <= 100 && squaredDistance <= radius * radius;

    public static int Sum(IEnumerable<int> amounts)
    {
        long total = 0;
        foreach (var n in amounts) { if (n > 0) total = Math.Min(int.MaxValue, total + n); }
        return (int)total;
    }

    // Calculate the complete withdrawal before touching any live inventory.
    public static int[] Allocate(IReadOnlyList<int> available, int requested)
    {
        if (requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        var result = new int[available.Count];
        int remaining = requested;
        for (int i = 0; i < available.Count; i++)
        {
            result[i] = Math.Min(Math.Max(0, available[i]), remaining);
            remaining -= result[i];
        }
        if (remaining != 0) throw new InvalidOperationException("The materials changed. Please select the action again.");
        return result;
    }
}
