# Verification - 0.1.3

## Automated checks

- Release build and eight core tests: passed. Core tests cover protected equipment/hotbar slots, range boundaries, oversized stacks and withdrawal allocation/conservation.
- Isolated Unity smoke checks passed with and without RossItemDrawers 1.0.12. They cover provider registration, all seven table action hooks, item-level availability checks, exact input plans, rebound network item references, withdrawal budgets, rollback and output-change detection.
- New input checks accept unrelated inventory changes and a reduced stack that still covers the cost; reject missing quantities, removed inputs and changed metadata; accept unchanged reloaded items. A Harmony inspection checks that ECA does not patch chest-opening/TakeAll RPCs.
- Removed lease/reservation tests along with the removed implementation. Tests no longer imply reservation guarantees.

## Behavior and boundaries

- No reservation broadcasts, leases, renewals or chest-opening locks. Only needed storage uses a short ownership handoff, and only when it is not already owned locally. Final input checks, withdrawal and saving run synchronously on the client that owns those containers. Do not interpret this as crash-safe distributed transactions or protection against independent inventory-writing mods.
- A stored augment item is withdrawn when the paid roll starts and held by that action until the choice is confirmed or closed. It is then delivered to the player, or dropped if inventory is full. Normal cancellation returns the paid item without refunding the roll. Abrupt process termination during that dialog is not covered by durable recovery.
- Client/server protocol v3 requires matching 0.1.3 installations.

## Gameplay still to verify

The isolated runner opens the menu only. It does not establish that the reported production failure has been reproduced or resolved.

Run Identify, Sacrifice, Convert, Enchant, Augment, Disenchant, both Rune modes and Upgrade in a disposable world. Cover player-only, chest-only, drawers and split materials; unrelated unavailable storage; full inventory; augment close/cancel; remote ownership; two simultaneous users; and persistence after reconnect. Check exact payment and one delivery to the player. Check table/tab performance in a large base.

No production profile or server changes are included.
