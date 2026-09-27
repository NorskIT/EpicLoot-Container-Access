# Changelog

## 0.1.3

- Remove storage reservations, lease renewal and chest-opening locks.
- Recheck only required items and quantities immediately before execution; unrelated inventory changes no longer cancel actions.
- Keep ordinary storage ownership handoff only where required to save changes.
- Withdraw a stored augment item when its paid roll starts, then deliver it when the choice is completed or closed; the chest is not held while choosing.
- Use matching 0.1.3 clients and server (protocol v3).

## 0.1.2

- Plan exact inputs and material sources before each action; unrelated nearby containers no longer participate.
- Player-only actions skip storage reservations. Only table upgrades require table ownership.
- Rebind unchanged, validated container items after network inventory reloads.
- Deliver processed equipment to the player, alongside new products; overflow follows EpicLoot's ground-drop behavior.
- Add detailed preparation failure reasons, object identifiers and ownership diagnostics, and one bounded retry when ownership changes.
- Update the coordination protocol; use 0.1.2 on both server and clients.

## 0.1.1

- Prevent changes to equipped items and hotbar contents from incorrectly cancelling table actions, including upgrades paid from containers.
- Continue rejecting changes to available materials and items moved into protected slots while an action waits.
- Log the affected inventory and changed item fields when validation cancels an action.

## 0.1.0 — test build

- Initial independent implementation for EpicLoot 0.14.13.
- Cached container lookup and material totals, with configurable server-controlled range.
- Stored equipment and material access; equipped/hotbar protection.
- Coordinated container and table reservations, guarded withdrawals, and protected augment choices.
- Package artwork supplied by NorskIT.
