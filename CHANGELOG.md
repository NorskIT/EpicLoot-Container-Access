# Changelog

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
