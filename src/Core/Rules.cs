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

// Server-only, all-or-nothing leases. Requests are single-use within a connection.
public sealed class LeaseBook
{
    private sealed class Lease
    {
        internal long Peer;
        internal string Token = "";
        internal string[] Keys = Array.Empty<string>();
        internal double Until;
    }
    private readonly Dictionary<string, Lease> leases = new();
    private readonly HashSet<string> seen = new();
    private readonly Dictionary<string, string> owners = new();
    private static string Request(long peer, string token) => peer + ":" + token;

    public bool Acquire(long peer, string token, IEnumerable<string> keys, double now)
    {
        Expire(now);
        string request = Request(peer, token);
        if (token.Length != 32 || seen.Contains(request)) return false;
        var all = keys.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (all.Length > 256 || all.Any(x => owners.ContainsKey(x))) return false;
        seen.Add(request);
        var lease = new Lease { Peer = peer, Token = token, Keys = all, Until = now + 10 };
        leases.Add(request, lease);
        foreach (var key in all) owners[key] = request;
        return true;
    }
    public bool Holds(long peer, string token, string key, double now)
    {
        Expire(now);
        return owners.TryGetValue(key, out var owner) && owner == Request(peer, token);
    }
    public bool Busy(string key, double now) { Expire(now); return owners.ContainsKey(key); }
    public bool Renew(long peer, string token, double now)
    {
        Expire(now);
        if (!leases.TryGetValue(Request(peer, token), out var lease)) return false;
        lease.Until = now + 10;
        return true;
    }
    public void Release(long peer, string token)
    {
        string request = Request(peer, token);
        if (!leases.TryGetValue(request, out var lease)) return;
        foreach (var key in lease.Keys) owners.Remove(key);
        leases.Remove(request);
    }
    public void Disconnect(long peer)
    {
        foreach (var lease in leases.Values.Where(x => x.Peer == peer).ToArray()) Release(peer, lease.Token);
        seen.RemoveWhere(x => x.StartsWith(peer + ":", StringComparison.Ordinal));
    }
    public void Expire(double now)
    {
        foreach (var lease in leases.Values.Where(x => x.Until <= now).ToArray()) Release(lease.Peer, lease.Token);
    }
}
