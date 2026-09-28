using System;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

namespace EpicLootContainerAccess;

[BepInPlugin(Id, "EpicLoot Container Access", Version)]
[BepInDependency("randyknapp.mods.epicloot", "0.14.13")]
[BepInDependency(Jotunn.Main.ModGuid)]
[NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "norskit.epiclootcontaineraccess", Version = "0.1.4";
    internal static Plugin Instance = null!;
    internal ConfigEntry<float> Radius = null!;
    internal ConfigEntry<bool> Diagnostics = null!;
    internal Storage Storage = null!;
    internal Network Network = null!;
    internal Actions Actions = null!;
    internal bool Ready;
    private Harmony? harmony;
    private bool started;

    private void Awake()
    {
        Instance = this;
        Radius = Config.Bind("Server", "ContainerRange", 10f,
            new ConfigDescription("Distance in metres from the enchanting table. The server's value is authoritative.", new AcceptableValueRange<float>(1, 100)));
        Diagnostics = Config.Bind("Local", "Diagnostics", false, "Log cache rebuild and action timing. Disabled by default.");
        Storage = new Storage(this);
        Network = new Network(this);
        Actions = new Actions(this);
    }

    private void Start()
    {
        started = true;
        try
        {
            foreach (string id in new[] { "KlownKiller.EpicLootContainerBridge", "buldosik.EpicLootCraftFromContainers", "EmpressAlae.EpicLootRunicCraftingBridge" })
                if (Chainloader.PluginInfos.ContainsKey(id)) throw new InvalidOperationException("Remove the competing EpicLoot bridge: " + id);
            var competingNames = new[] { "epiclootcontainerbridge", "epiclootcraftfromcontainers", "epiclootruniccraftingbridge" };
            foreach (var info in Chainloader.PluginInfos.Values)
                if (info.Metadata.GUID != Id && competingNames.Contains(info.Metadata.Name.Replace(" ", "").Replace("_", "").ToLowerInvariant()))
                    throw new InvalidOperationException("Remove the competing EpicLoot bridge: " + info.Metadata.Name);
            if (Chainloader.PluginInfos["randyknapp.mods.epicloot"].Metadata.Version != new System.Version(0, 14, 13))
                throw new InvalidOperationException("This test build supports EpicLoot 0.14.13. Revalidate the action guards before using another version.");
            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(Plugin).Assembly);
            if (!EpicLoot.API.RegisterInventoryProvider(Id, Storage.Items, Storage.Count, Storage.Remove, Storage.RemoveExact))
                throw new InvalidOperationException("EpicLoot rejected the inventory provider.");
            EpicLoot.API.RegisterSacrificeFilter(Id, item => !Storage.Protected(item));
            Storage.Bootstrap();
            Ready = true;
            Logger.LogInfo("EpicLoot Container Access 0.1.4 ready. Equipped items and hotbar slots 1–8 are protected.");
        }
        catch (Exception e)
        {
            Ready = false;
            EpicLoot.API.UnregisterInventoryProvider(Id);
            EpicLoot.API.UnregisterSacrificeFilter(Id);
            harmony?.UnpatchSelf();
            Logger.LogError("Integration disabled: " + e);
        }
    }

    private void Update()
    {
        if (!started || !Ready) return;
        try { Network.Update(); Actions.Update(); Storage.Tick(); }
        catch (Exception e) { Logger.LogError(e); Actions.Abort("Storage access failed. Please reopen the table."); }
    }
    private void OnDestroy()
    {
        Ready = false;
        Actions?.Abort(null);
        EpicLoot.API.UnregisterInventoryProvider(Id);
        EpicLoot.API.UnregisterSacrificeFilter(Id);
        Storage?.Dispose();
        harmony?.UnpatchSelf();
    }
    internal void Notice(string message)
    {
        Logger.LogWarning(message);
        if (Player.m_localPlayer) Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
    }
    internal void Trace(string message) { if (Diagnostics.Value) Logger.LogInfo(message); }
    internal void Error(Exception e) => Logger.LogError(e);
    internal void Warn(string message) => Logger.LogWarning(message);
}
