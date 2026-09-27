# EpicLoot Container Access

![EpicLoot Container Access](https://raw.githubusercontent.com/NorskIT/EpicLoot-Container-Access/main/images/epicloot-container-access.png)

Use materials and equipment from nearby storage at EpicLoot's enchanting table.

- Identify, enchant, augment, sacrifice, disenchant, work with runes, convert materials, and upgrade the table using nearby storage.
- Equipped items and items in **your hotbar slots 1–8** are protected from selection, modification, and consumption. Move an item into your backpack to use it at the table.
- Container discovery and material totals are cached, avoiding repeated scene searches for every recipe.
- Server-coordinated reservations prevent this mod's users from spending the same shared storage simultaneously.
- Accesses chests and drawers through the standard container inventory interface, with **no dependency on RossItemDrawers or other crafting mods**.

## Install

Install on **the server and every client**, with BepInExPack Valheim, EpicLoot **0.14.13**, and Jötunn **2.30.0 or newer**.

Remove EpicLootContainerBridge and other EpicLoot storage bridges first. Keep your normal crafting mod if desired; CraftFromChests/CraftFromContainers are not required.

Import the package in your mod manager. For manual installation, extract its `BepInEx` directory into your mod profile.

## Range and behavior

`BepInEx/config/norskit.epiclootcontaineraccess.cfg`:

```ini
[Server]
ContainerRange = 10

[Local]
Diagnostics = false
```

Range is measured in metres **from the table**, including height. The server controls it; accepted values are 1–100. Storage must also be loaded, accessible, and not in use. Graves are excluded.

Unprotected carried materials are spent first. Equipment modified in a chest stays there; new items use EpicLoot's normal delivery behavior. A chest's top row is usable—the hotbar rule only applies to your inventory. Augment retains EpicLoot's normal payment when choices are generated; closing the choice dialog does not refund a roll.

For Identify, put the required iron or other materials in a chest/drawer within range. If they are not shown, check distance, permissions, and whether another player is using the container. Enable diagnostics for cache timings in the BepInEx log.

## Development

Copy `Environment.props.example` to `Environment.props` and set paths to your local game and mod assemblies. Requires the .NET 10 SDK and the .NET Framework 4.8.1 targeting pack.

```powershell
dotnet build src/Plugin/Plugin.csproj -c Release
dotnet test tests/Core.Tests/Core.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/Build-Package.ps1
```

The package is written to `artifacts/EpicLootContainerAccess-0.1.1.zip`. Build scripts never install into a profile or deploy to a server.

[Source and issues](https://github.com/NorskIT/EpicLoot-Container-Access)
