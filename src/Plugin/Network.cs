using System;
using System.Collections.Generic;
using System.Linq;
using EpicLootContainerAccess.Core;
using HarmonyLib;
using UnityEngine;

namespace EpicLootContainerAccess;

internal sealed class Network
{
    private const string Rpc = "NorskIT.ECA.v3";
    private readonly Plugin plugin;
    private ZRoutedRpc? router;
    private readonly Dictionary<string, Request> requests = new();
    private readonly Dictionary<long, float> rate = new();
    private float nextHello;
    internal string Token { get; private set; } = "";
    internal bool Granted { get; private set; }
    internal string? Failure { get; private set; }
    internal bool ConfigReady { get; private set; }
    internal float Range { get; private set; } = 10;
    private bool Server => ZNet.instance && ZNet.instance.IsServer();
    private long Local => ZNet.instance ? ZNet.GetUID() : 0;
    private long ServerId => Server ? Local : ZNet.instance?.GetServerPeer()?.m_uid ?? 0;
    private sealed class Request
    {
        internal string Token = "", Action = "";
        internal readonly HashSet<ZDOID> Retried = new();
        internal long Peer, Player;
        internal ZDOID Table;
        internal float Until;
        internal ZDOID[] Ids = Array.Empty<ZDOID>();
        internal readonly Dictionary<ZDOID, long> Waiting = new();
    }
    internal Network(Plugin plugin) { this.plugin = plugin; }
    private static Container? Find(ZDOID id)
    {
        var obj = ZNetScene.instance ? ZNetScene.instance.FindInstance(id) : null;
        if (!obj) return null;
        return obj.GetComponentsInChildren<Container>(true).FirstOrDefault(c => Storage.Id(c) == id);
    }
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
                requests.Clear(); rate.Clear();
                plugin.Storage.DisposeWorld();
            }
            return;
        }
        if (router != ZRoutedRpc.instance)
        {
            plugin.Actions.Abort(null);
            requests.Clear(); rate.Clear();
            plugin.Storage.DisposeWorld();
            router = ZRoutedRpc.instance; router.Register<ZPackage>(Rpc, Receive); ConfigReady = false; nextHello = 0;
        }
        float now = Time.unscaledTime;
        if (Server) { Range = plugin.Radius.Value; ConfigReady = true; }
        if (now >= nextHello) { nextHello = now + 2; Send(ServerId, "hello"); }
        if (Server)
        {
            foreach (var request in requests.Values.ToArray())
            {
                if (request.Until < now || (request.Peer != Local && ZNet.instance.GetPeer(request.Peer) == null))
                    End(request, "Storage request expired. Please try again.");
            }
        }
    }
    internal void Begin(IEnumerable<Container> containers, bool upgrade, string action)
    {
        Release();
        Token = Guid.NewGuid().ToString("N"); Failure = null; Granted = false;
        var table = Storage.Table;
        var view = table ? table.GetComponent<ZNetView>() : null;
        if (!ConfigReady || !view || !view.IsValid()) { Failure = "Waiting for server configuration."; return; }
        var ids = containers.Select(Storage.Id).Distinct().ToArray();
        Send(ServerId, "acquire", p => { p.Write(Token); p.Write(view.GetZDO().m_uid); p.Write(upgrade); p.Write(action); p.Write(ids.Length); foreach (var id in ids) p.Write(id); });
    }
    internal void Release()
    {
        if (Token != "") Send(ServerId, "release", p => p.Write(Token));
        Token = ""; Granted = false; Failure = null;
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
                Granted = true; return;
            }
            if (kind == "deny" && sender == ServerId)
            {
                if (p.ReadString() == Token) { Failure = p.ReadString(); Granted = false; }
                return;
            }
            if (kind == "release" && Server)
            {
                string token = p.ReadString();
                if (requests.TryGetValue(token, out var request) && request.Peer == sender) requests.Remove(token);
            }
        }
        catch (Exception e) { plugin.Error(e); }
    }
    private void Acquire(long sender, ZPackage p)
    {
        float now = Time.unscaledTime;
        string token = p.ReadString(); ZDOID tableId = p.ReadZDOID(); bool upgrade = p.ReadBool(); string action = p.ReadString(); int count = p.ReadInt();
        if (token.Length != 32 || count < 0 || count > 256) return;
        if (rate.TryGetValue(sender, out var last) && now - last < .2f) { Deny(sender, token, "Please wait before trying again."); return; }
        rate[sender] = now;
        ZDO? player = sender == Local && Player.m_localPlayer ? Player.m_localPlayer.GetComponent<ZNetView>().GetZDO()
            : ZDOMan.instance.GetZDO(ZNet.instance.GetPeer(sender)?.m_characterID ?? ZDOID.None);
        var table = ZDOMan.instance.GetZDO(tableId);
        var tablePrefab = table != null && ZNetScene.instance ? ZNetScene.instance.GetPrefab(table.GetPrefab()) : null;
        if (player == null || table == null || !tablePrefab || !tablePrefab.GetComponent<EpicLoot_UnityLib.EnchantingTable>() || (player.GetPosition() - table.GetPosition()).sqrMagnitude > 100)
        { Deny(sender, token, "The enchanting table is no longer in reach."); return; }
        var request = new Request { Token = token, Action = action, Peer = sender, Player = player.GetLong(ZDOVars.s_playerID), Table = tableId, Until = now + 5 };
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
        if (upgrade)
        {
            ids.Add(tableId);
            request.Waiting[tableId] = table.GetOwner() == 0 ? Local : table.GetOwner();
        }
        if (ids.Count == 0) { Deny(sender, token, "Empty storage request."); return; }
        request.Ids = ids.Distinct().ToArray();
        if (requests.ContainsKey(token) || requests.Values.Any(x => x.Peer == sender))
        { Deny(sender, token, "A storage request is already pending. Please try again."); return; }
        requests.Add(token, request);
        foreach (var pair in request.Waiting.ToArray())
            if (requests.ContainsKey(token)) Send(pair.Value, "prepare", q => { q.Write(token); q.Write(sender); q.Write(request.Player); q.Write(pair.Key); });
    }
    private void Prepare(ZPackage p)
    {
        string token = p.ReadString(); long requester = p.ReadLong(); long playerId = p.ReadLong(); var id = p.ReadZDOID();
        var obj = ZNetScene.instance ? ZNetScene.instance.FindInstance(id) : null;
        var c = Find(id);
        var enchanting = obj ? obj.GetComponent<EpicLoot_UnityLib.EnchantingTable>() : null;
        var view = c ? Storage.View(c) : enchanting ? enchanting.GetComponent<ZNetView>() : null;
        string reason = "";
        long actualOwner = view && view.IsValid() ? view.GetZDO().GetOwner() : 0;
        try
        {
            if (!obj) reason = "object_not_loaded";
            else if (!c && !enchanting) reason = "component_missing";
            else if (!view || !view.IsValid()) reason = "network_view_missing";
            else if (!view.IsOwner() && !(Server && actualOwner == 0)) reason = "owner_changed";
            else if (c && (c.IsInUse() || (c.m_wagon && c.m_wagon.InUse()))) reason = "in_use";
            else if (c && (c.GetComponent<TombStone>() || !(bool)AccessTools.Method(typeof(Container), "CheckAccess").Invoke(c, new object[] { playerId }))) reason = "access_denied";
            else
            {
                if (c)
                {
                    Storage.Load(c);
                    var inv = c.GetInventory();
                    if (inv == null) reason = "inventory_missing";
                    else Storage.Save(c);
                }
                if (reason == "")
                {
                    view!.GetZDO().SetOwner(requester);
                    ZDOMan.instance.ForceSendZDO(requester, id);
                }
            }
        }
        catch (Exception e) { reason = "prepare_exception"; plugin.Error(e); }
        if (reason != "") plugin.Warn($"Prepare {token}: {id}, prefab={(obj ? obj.name : "unloaded")}, expected owner={Local}, actual owner={actualOwner}, reason={reason}");
        Send(ServerId, "prepared", q => { q.Write(token); q.Write(id); q.Write(reason == ""); q.Write(reason); q.Write(actualOwner); });
    }
    private void Prepared(long sender, ZPackage p)
    {
        string token = p.ReadString(); var id = p.ReadZDOID(); bool ok = p.ReadBool();
        string reason = p.ReadString(); long reportedOwner = p.ReadLong();
        if (!requests.TryGetValue(token, out var request) || !request.Waiting.TryGetValue(id, out long expected) || expected != sender) return;
        var zdo = ZDOMan.instance.GetZDO(id);
        long currentOwner = zdo?.GetOwner() ?? 0;
        if (!ok && reason == "owner_changed" && currentOwner != 0 && currentOwner != sender && currentOwner == reportedOwner &&
            Time.unscaledTime < request.Until && request.Retried.Add(id))
        {
            request.Waiting[id] = currentOwner;
            Send(currentOwner, "prepare", q => { q.Write(token); q.Write(request.Peer); q.Write(request.Player); q.Write(id); });
            return;
        }
        if (!ok)
        {
            string kind = id == request.Table ? "Table" : "Container";
            plugin.Warn($"{request.Action} {token}: {kind} {id}, prefab={zdo?.GetPrefab()}, expected owner={expected}, reported owner={reportedOwner}, server owner={currentOwner}, reason={reason}");
            End(request, $"{kind} {id}: {reason}. Nothing was consumed."); return;
        }
        request.Waiting.Remove(id);
        if (request.Waiting.Count == 0) Grant(request);
    }
    private void Grant(Request request)
    {
        requests.Remove(request.Token);
        Send(request.Peer, "grant", p => p.Write(request.Token));
    }
    private void Deny(long peer, string token, string reason) => Send(peer, "deny", p => { p.Write(token); p.Write(reason); });
    private void End(Request request, string? reason)
    {
        requests.Remove(request.Token);
        if (reason != null) Deny(request.Peer, request.Token, reason);
    }
}
