# Verification - 0.1.4

## Conversion regression

- Reproduced the reported InvalidCastException with the unmodified 0.1.3 CalculateCosts method using an actual MultiSelectItemList, selected ConversionRecipeUnity and ConvertUI in isolated Unity (artifacts/smoke-014-reproduce). The reproduction test passed by observing that exception.
- Regression checks exercise the repaired cost path for one recipe, quantity three and multiple recipes sharing an ingredient; verify batch product amounts; reject empty/wrong-type selections; and check inventory-based Sacrifice/Enchant plus the Upgrade validation branch.
- These tests run actual EpicLoot UI selection and cost/product calculation code, but do not execute a complete world action or prove player/chest payment and delivery end to end. The world scenarios below remain pending.

## Automated checks

- Release build and eight core tests: passed. Core tests cover protected equipment/hotbar slots, range boundaries, oversized stacks and withdrawal allocation/conservation.
- Isolated Unity smoke checks passed with and without RossItemDrawers 1.0.12. They cover provider registration, all seven table action hooks, item-level availability checks, exact input plans, rebound network item references, withdrawal budgets, rollback and output-change detection.
- New input checks accept unrelated inventory changes and a reduced stack that still covers the cost; reject missing quantities, removed inputs and changed metadata; accept unchanged reloaded items. A Harmony inspection checks that ECA does not patch chest-opening/TakeAll RPCs.
- Removed lease/reservation tests along with the removed implementation. Tests no longer imply reservation guarantees.

## Behavior and boundaries

- No reservation broadcasts, leases, renewals or chest-opening locks. Only needed storage uses a short ownership handoff, and only when it is not already owned locally. Final input checks, withdrawal and saving run synchronously on the client that owns those containers. Do not interpret this as crash-safe distributed transactions or protection against independent inventory-writing mods.
- A stored augment item is withdrawn when the paid roll starts and held by that action until the choice is confirmed or closed. It is then delivered to the player, or dropped if inventory is full. Normal cancellation returns the paid item without refunding the roll. Abrupt process termination during that dialog is not covered by durable recovery.
- Client/server protocol v3 requires matching 0.1.4 installations.

## Gameplay still to verify

The isolated runner opens the menu only. It does not establish that the reported production failure has been reproduced or resolved.

Run Identify, Sacrifice, Convert, Enchant, Augment, Disenchant, both Rune modes and Upgrade in a disposable world. Cover player-only, chest-only, drawers and split materials; unrelated unavailable storage; full inventory; augment close/cancel; remote ownership; two simultaneous users; and persistence after reconnect. Check exact payment and one delivery to the player. Check table/tab performance in a large base.

No production profile or server changes are included.
