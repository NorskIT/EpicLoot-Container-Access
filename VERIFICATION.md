# Verification — 0.1.0 test build

## Completed

- Release build against the installed Valheim 1.0.16 and EpicLoot 0.14.13 assemblies: zero compiler warnings/errors.
- 15 automated behavior tests: passed. Includes every hotbar slot, equipped/extra-slot coordinates, range boundaries, large drawer counts, randomized conservation checks, all-or-nothing reservations, replay rejection, unauthorized release, renewal, expiration, and disconnect.
- Isolated Unity/Mono startup smoke test: passed with BepInEx 5.4.2350, Jötunn 2.30.2 and EpicLoot 0.14.13.
- Repeated the smoke test with RossItemDrawers 1.0.12: both mods loaded, its Container subclass was verified, and all synthetic tests passed. The earlier run without Ross confirms it is not a dependency. This does not replace an actual drawer persistence/multiplayer test.
- Both inventory and sacrifice providers registered; all seven table action panels guarded; 21 Harmony targets patched successfully.
- Synthetic inventory tests inside Valheim: deterministic inventory snapshots, withdrawal of 7,500 from a 10,000-item stack, equipped protection and usable chest top row.
- Synchronous undo preserves the actual item references, oversized stacks, equipment flags, custom data and EpicLoot's cached magic data.
- Local RPC packet serialization roundtrip passed.

The smoke test opens the main menu with an isolated BepInEx installation and save directory, then exits automatically. It does not open a world or connect to any server. Generated logs live under `artifacts/smoke-*` and are not shipped or committed.

## Required gameplay acceptance before release

These are **not yet verified** by the startup test. Use a disposable world/test server with two clients and the same mod versions.

- Compare opening the table and repeatedly switching Convert Materials with no bridge, the old bridge, and this mod. Use the same base, inventories, and game settings. Record initial and repeated timings; enable `Diagnostics` to capture this mod's cache rebuild times. A measured speedup or absence of 5–15 second freezes has not yet been established in the user's base.
- Identify with all iron in RossItemDrawers 1.0.12; repeat with a normal chest and split costs. Test each drawer tier, large counts, empty drawers and persistence after reconnecting. RossItemDrawers is not required to load this mod.
- Test inside, exactly at and outside the 10 m table radius, including vertical distance and a changed server radius.
- Test private/ward-protected storage, an open chest, a cart being used, a destroyed container, and a tombstone.
- Test Identify, Sacrifice, Enchant, Augment, Disenchant, Rune extraction/application, Convert Materials and table upgrades. Check materials, output amounts and stored gear persistence.
- Put two same-name items with different enchantments in storage; ensure only the selected instance changes.
- Verify equipped gear and hotbar 1–8 are absent and cannot be consumed. Move a selected item to the hotbar or equip it during the countdown/augment dialog. Repeat with ExtraSlots.
- Let two clients attempt to use the last materials or the same stored item. Also try opening the reserved chest and writing to a drawer through another mod.
- Test closing the table, switching scenes and disconnecting during reservation and during the augment choice dialog. No reward should be granted for an uncompleted payment.
- Repeatedly open/close the table in a large base and check that containers do not accumulate after scene unloading.

## Boundaries

- The first build supports exactly EpicLoot's plugin version 0.14.13. Its assembly version is still 0.13.0.0; the runtime check intentionally uses BepInEx metadata instead.
- A table action conservatively reserves the table and all eligible containers in its current snapshot, up to 255 containers. This prevents competing ECA actions changing inputs during validation but can cause contention at large shared storage areas.
- Ownership transfer and authoritative owner inventory fingerprints must agree before execution. A changed or stale snapshot cancels the action and asks the player to retry.
- Augment keeps EpicLoot's normal payment/augmentation marking when choices are generated. The reservation remains held while choosing. Closing the dialog does not refund a roll; a subsequently protected item cannot receive the chosen change.
- Table upgrades execute while this client owns the reserved table, with the expected previous level checked, rather than paying in EpicLoot's later uncoordinated response callback.
- The in-memory undo point is only for exceptions during synchronous execution. It is not a world backup, does not restore production saves, and is not durable crash recovery.
- Reservations cannot make independent third-party inventory writers participate in a transaction. Drawer flushes and abrupt disconnects need the gameplay checks above. Two frames are allowed for deferred drawer persistence before releasing successful actions.
- Output is withheld until deductions and saves complete. Once output delivery begins, the mod does not automatically refund inputs on an output exception, to avoid duplicating already delivered output.

No production profile changes, server deployment, restart, save restoration or release publication are part of this build.
