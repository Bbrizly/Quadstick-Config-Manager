# Cutover plan

## Objective

Move users from Avalonia stable to Tauri stable without risking profile/device data or trapping them in a one-way settings format.

## Pre-cutover

- Tauri beta uses distinct bundle/app-data identity.
- Both apps open ordinary `.csv`/`.xlsx` inputs independently.
- Tauri never modifies Avalonia settings directory during beta.
- Device can only be mutated by one app at a time operationally; UI detects/handles file/port busy rather than trying to coordinate processes initially.
- backups remain readable ordinary files outside device.

## Data migration

Inventory all persisted app settings/paths/tokens/crash files before Gate 8. For each:

| Data | Strategy |
|---|---|
| profile CSV | file format itself; no migration |
| local recent/open state | optional import; never required for data access |
| theme/language/scale/reduced motion | read legacy once or ask user; validate values |
| analytics consent/install ID | preserve only if privacy semantics allow and migration is exact; do not silently opt in |
| crash-report consent/pending report | preserve consent only with explicit policy review; pending report stays local unless user sends |
| Google refresh token | platform-specific secure migration or one-time reconnect; never plaintext bridge |
| backup path mapping | migrate only validated references; profile header Sheet ID remains important identity |

## Stable release sequence

1. Freeze release candidate commit.
2. Run full CI + manual hardware/AT checklist.
3. Tag prerelease RC and distribute to final beta group.
4. Rehearse installation over prior stable on clean test machines **without publishing update**.
5. Verify data import/reconnect behavior.
6. Publish stable artifact/release notes.
7. Enable updater manifest/channel only after artifact/signature verification.
8. Monitor explicit support/crash reports; do not add hidden telemetry.

## User-facing fallback

Release notes include previous Avalonia download and instructions: profiles remain standard CSV; if Tauri has a problem, close it and reinstall/run previous stable. If beta app uses distinct identity, downgrade is trivial.

## Post-cutover criteria before legacy deletion

- stable Tauri has completed one release cycle;
- no open P0/P1 data-integrity issue;
- install/restore metrics from explicit tests/support acceptable;
- previous release artifact retained;
- C# oracle outputs preserved in fixtures even if build removed later.