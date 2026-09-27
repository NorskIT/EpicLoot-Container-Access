using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using EpicLootContainerAccess.Core;
using HarmonyLib;
using UnityEngine;

namespace EpicLootContainerAccess;

internal sealed class Network
{
    private const string Rpc = "NorskIT.ECA.v1";
    private readonly Plugin plugin;
    private ZRoutedRpc? router;
    private LeaseBook leases = new();
    private readonly Dictionary<string, Request> requests = new();
    private readonly Dictionary<ZDOID, (string Token, long Peer, float Until)> reservations = new();
    private readonly Dictionary<long, float> rate = new();
    private float nextHello, nextRenew, leaseValidUntil;
    internal string Token { get; private set; } = "";
    internal bool Granted { get; private set; }
    internal string? Failure { get; private set; }
    internal readonly Dictionary<ZDOID, string> Fingerprints = new();
    internal bool ConfigReady { get; private set; }
    internal float Range { get; private set; } = 10;
    private bool Server => ZNet.instance && ZNet.instance.IsServer();
    private long Local => ZNet.instance ? ZNet.GetUID() : 0;
    private long ServerId => Server ? Local : ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
    private sealed class Request
    {
        internal string Token = "";
        internal long Peer, Player;
        internal ZDOID Table;
        internal float Until;
        internal ZDOID[] Ids = Array.Empty<ZDOID>();
        internal readonly Dictionary<ZDOID, long> Waiting = new();
        internal readonly Dictionary<ZDOID, string> Hashes = new();
    }
    internal Network(Plugin plugin) { this.plugin = plugin; }
    internal static string Hash(Inventory inv)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Storage.Snapshot(inv)));
    }
    private static string Key(ZDOID id) => id.UserID + ":" + id.ID;
    private static Container? Find(ZDOID id) => ZNetScene.instance ? ZNetScene.instance.FindInstance(id)?.GetComponent<Container>() : null;
    private void Send(long peer, string kind, Action<ZPackage>? body = null)
    {
        if (router == null || peer == 0) return;
        var p = new ZPackage(); p.Write(kind); body?.Invoke(p);
        // Local dispatch is explicit: it also keeps single-player behavior identical to multiplayer.
        if (peer == Local) { p.SetPos(0); Receive(Local, p); } else router.InvokeRoutedRPC(peer, Rpc, p);
    }
    internal void Update()
    {
        if (!ZNet.instance || ZRoutedRpc.instance == null)
        {
            if (router != null)
            {
                router = null; ConfigReady = false; Token = ""; Granted = false;
                requests.Clear(); reservations.Clear(); rate.Clear(); leases = new LeaseBook();
                plugin.Storage.DisposeWorld();
            }
            return;
        }
        if (router != ZRoutedRpc.instance)
        {
            plugin.Actions.Abort(null);
            requests.Clear(); reservations.Clear(); rate.Clear(); leases = new LeaseBook();
            plugin.Storage.DisposeWorld();
            router = ZRoutedRpc.instance; router.Register<ZPackage>(Rpc, Receive); ConfigReady = false; nextHello = 0;
        }
        float now = Time.unscaledTime;
        if (Granted && now >= leaseValidUntil) { Granted = false; Failure = "Storage reservation expired. Nothing more will be consumed."; }
        if (Server) { Range = plugin.Radius.Value; ConfigReady = true; }
        if (now >= nextHello) { nextHello = now + 2; Send(ServerId, "hello"); }
        if (Token != "" && Granted && now >= nextRenew) { nextRenew = now + 2; Send(ServerId, "renew", p => p.Write(Token)); }
        foreach (var id in reservations.Where(x => x.Value.Until < now).Select(x => x.Key).ToArray()) reservations.Remove(id);
        if (Server)
        {
            foreach (var request in requests.Values.ToArray())
            {
                if (request.Until < now || (request.Peer != Local && ZNet.instance.GetPeer(request.Peer) == null))
                    End(request, "Storage request expired. Please try again.");
            }
            leases.Expire(now);
        }
    }
    internal bool ReservedByOther(ZDOID id) => reservations.TryGetValue(id, out var r) && r.Until >= Time.unscaledTime && r.Peer != Local;
    internal bool AnyReservation(ZDOID id) => reservations.TryGetValue(id, out var r) && r.Until >= Time.unscaledTime;
    internal bool OwnsReservation(ZDOID id) => Granted && Time.unscaledTime < leaseValidUntil && Fingerprints.ContainsKey(id) && Token != "";
    internal void Begin(IEnumerable<Container> containers)
    {
        Release();
        Token = Guid.NewGuid().ToString("N"); Failure = null; Granted = false; Fingerprints.Clear();
        var table = Storage.Table;
        var view = table ? table.GetComponent<ZNetView>() : null;
        if (!ConfigReady || !view || !view.IsValid()) { Failure = "Waiting for server configuration."; return; }
        var ids = containers.Select(Storage.Id).Distinct().ToArray();
        Send(ServerId, "acquire", p => { p.Write(Token); p.Write(view.GetZDO().m_uid); p.Write(ids.Length); foreach (var id in ids) p.Write(id); });
    }
    internal void Release()
    {
        if (Token != "") Send(ServerId, "release", p => p.Write(Token));
        Token = ""; Granted = false; Fingerprints.Clear(); Failure = null;
    }
    private void Receive(long sender, ZPackage p)
    {
        try
        {
            string kind = p.ReadString();
            if (kind == "hello" && Server) { Send(sender, "config", q => q.Write(plugin.Radius.Value)); return; }
            if (kind == "config" && sender == ServerId)
            {
                float value = p.ReadSingle();
                if (float.IsNaN(value) || value < 1 || value > 100) return;
                if (Range != value) plugin.Storage.Invalidate();
                Range = value; ConfigReady = true; return;
            }
            if (kind == "acquire" && Server) { Acquire(sender, p); return; }
            if (kind == "prepare" && sender == ServerId) { Prepare(p); return; }
            if (kind == "prepared" && Server) { Prepared(sender, p); return; }
            if (kind == "grant" && sender == ServerId)
            {
                if (p.ReadString() != Token || Token == "") return;
                int count = p.ReadInt(); if (count < 0 || count > 256) return;
                for (int i = 0; i < count; i++) Fingerprints[p.ReadZDOID()] = p.ReadString();
                leaseValidUntil = Time.unscaledTime + 8; Granted = true; return;
            }
            if (kind == "renewed" && sender == ServerId)
            {
                if (p.ReadString() == Token && Granted) leaseValidUntil = Time.unscaledTime + 8;
                return;
            }
            if (kind == "deny" && sender == ServerId)
            {
                if (p.ReadString() == Token) { Failure = p.ReadString(); Granted = false; }
                return;
            }
            if (kind == "reserved" && sender == ServerId)
            {
                string token = p.ReadString(); long peer = p.ReadLong(); int count = p.ReadInt();
                if (count < 0 || count > 256) return;
                for (int i = 0; i < count; i++) reservations[p.ReadZDOID()] = (token, peer, Time.unscaledTime + 10);
                plugin.Storage.Invalidate(); return;
            }
            if (kind == "released" && sender == ServerId)
            {
                string token = p.ReadString();
                foreach (var id in reservations.Where(x => x.Value.Token == token).Select(x => x.Key).ToArray()) reservations.Remove(id);
                plugin.Storage.Invalidate(); return;
            }
            if ((kind == "renew" || kind == "release") && Server)
            {
                string token = p.ReadString();
                if (!requests.TryGetValue(token, out var request) || request.Peer != sender) return;
                if (kind == "release") End(request, null);
                else if (leases.Renew(sender, token, Time.unscaledTime))
                {
                    request.Until = Time.unscaledTime + 10; BroadcastReservation(request);
                    Send(sender, "renewed", q => q.Write(token));
                }
            }
        }
        catch (Exception e) { plugin.Error(e); }
    }
    private void Acquire(long sender, ZPackage p)
    {
        float now = Time.unscaledTime;
        string token = p.ReadString(); ZDOID tableId = p.ReadZDOID(); int count = p.ReadInt();
        if (token.Length != 32 || count < 0 || count > 256) return;
        if (rate.TryGetValue(sender, out var last) && now - last < .2f) { Deny(sender, token, "Please wait before trying again."); return; }
        rate[sender] = now;
        ZDO? player = sender == Local && Player.m_localPlayer ? Player.m_localPlayer.GetComponent<ZNetView>().GetZDO()
            : ZDOMan.instance.GetZDO(ZNet.instance.GetPeer(sender)?.m_characterID ?? ZDOID.None);
        var table = ZDOMan.instance.GetZDO(tableId);
        var tablePrefab = table != null && ZNetScene.instance ? ZNetScene.instance.GetPrefab(table.GetPrefab()) : null;
        if (player == null || table == null || !tablePrefab || !tablePrefab.GetComponent<EpicLoot_UnityLib.EnchantingTable>() || (player.GetPosition() - table.GetPosition()).sqrMagnitude > 100)
        { Deny(sender, token, "The enchanting table is no longer in reach."); return; }
        var request = new Request { Token = token, Peer = sender, Player = player.GetLong(ZDOVars.s_playerID), Table = tableId, Until = now + 5 };
        if (request.Player == 0) { Deny(sender, token, "Player identity is unavailable."); return; }
        var ids = new List<ZDOID>();
        for (int i = 0; i < count; i++)
        {
            var id = p.ReadZDOID(); var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !Rules.InRange((zdo.GetPosition() - table.GetPosition()).sqrMagnitude, Range))
            { Deny(sender, token, "A container moved outside the table's range."); return; }
            ids.Add(id);
            request.Waiting[id] = zdo.GetOwner() == 0 ? Local : zdo.GetOwner();
        }
        ids.Add(tableId);
        request.Waiting[tableId] = table.GetOwner() == 0 ? Local : table.GetOwner();
        request.Ids = ids.Distinct().ToArray();
        if (requests.ContainsKey(token) || requests.Values.Any(x => x.Peer == sender) || !leases.Acquire(sender, token, request.Ids.Select(Key), now))
        { Deny(sender, token, "Storage is busy. Please try again."); return; }
        requests.Add(token, request);
        BroadcastReservation(request);
        foreach (var pair in request.Waiting.ToArray())
            if (requests.ContainsKey(token)) Send(pair.Value, "prepare", q => { q.Write(token); q.Write(sender); q.Write(request.Player); q.Write(pair.Key); });
    }
    private void Prepare(ZPackage p)
    {
        string token = p.ReadString(); long requester = p.ReadLong(); long playerId = p.ReadLong(); var id = p.ReadZDOID();
        if (!reservations.TryGetValue(id, out var reservation) || reservation.Token != token || reservation.Peer != requester || reservation.Until < Time.unscaledTime) return;
        var c = Find(id);
        bool ok = false; string hash = "";
        var obj = ZNetScene.instance ? ZNetScene.instance.FindInstance(id) : null;
        var enchanting = obj ? obj.GetComponent<EpicLoot_UnityLib.EnchantingTable>() : null;
        if (enchanting)
        {
            var view = enchanting.GetComponent<ZNetView>();
            if (view && view.IsValid() && (view.IsOwner() || (Server && view.GetZDO().GetOwner() == 0)))
            {
                view.GetZDO().SetOwner(requester);
                ZDOMan.instance.ForceSendZDO(requester, id);
                ok = true; hash = "table";
            }
        }
        if (c && !c.GetComponent<TombStone>() && !c.IsInUse() && !(c.m_wagon && c.m_wagon.InUse()))
        {
            var view = Storage.View(c);
            if (view && view.IsValid() && (view.IsOwner() || (Server && view.GetZDO().GetOwner() == 0)) &&
                (bool)AccessTools.Method(typeof(Container), "CheckAccess").Invoke(c, new object[] { playerId }))
            {
                Storage.Load(c);
                var inv = c.GetInventory();
                if (inv != null)
                {
                    hash = Hash(inv);
                    Storage.Save(c);
                    view.GetZDO().SetOwner(requester);
                    ZDOMan.instance.ForceSendZDO(requester, id);
                    ok = true;
                }
            }
        }
        Send(ServerId, "prepared", q => { q.Write(token); q.Write(id); q.Write(ok); q.Write(hash); });
    }
    private void Prepared(long sender, ZPackage p)
    {
        string token = p.ReadString(); var id = p.ReadZDOID(); bool ok = p.ReadBool(); string hash = p.ReadString();
        if (!requests.TryGetValue(token, out var request) || !request.Waiting.TryGetValue(id, out long expected) || expected != sender) return;
        if (!ok || (hash.Length != 44 && !(id == request.Table && hash == "table"))) { End(request, "A container is busy or unavailable."); return; }
        request.Waiting.Remove(id); request.Hashes[id] = hash;
        if (request.Waiting.Count == 0) Grant(request);
    }
    private void Grant(Request request)
    {
        request.Until = Time.unscaledTime + 10;
        Send(request.Peer, "grant", p => { p.Write(request.Token); p.Write(request.Hashes.Count); foreach (var pair in request.Hashes) { p.Write(pair.Key); p.Write(pair.Value); } });
    }
    private void BroadcastReservation(Request request)
    {
        void Body(ZPackage p) { p.Write(request.Token); p.Write(request.Peer); p.Write(request.Ids.Length); foreach (var id in request.Ids) p.Write(id); }
        Send(Local, "reserved", Body);
        foreach (var peer in ZNet.instance.GetPeers()) Send(peer.m_uid, "reserved", Body);
    }
    private void Deny(long peer, string token, string reason) => Send(peer, "deny", p => { p.Write(token); p.Write(reason); });
    private void End(Request request, string? reason)
    {
        requests.Remove(request.Token); leases.Release(request.Peer, request.Token);
        if (reason != null) Deny(request.Peer, request.Token, reason);
        Send(Local, "released", p => p.Write(request.Token));
        foreach (var peer in ZNet.instance.GetPeers()) Send(peer.m_uid, "released", p => p.Write(request.Token));
    }
}
