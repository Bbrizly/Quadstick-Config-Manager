# Device discovery

## Storage parity algorithm

Current behavior effectively:

```text
enumerate drives
→ keep ready drives
→ accept removable; accept fixed only on macOS under /Volumes
→ candidate iff <root>/default.csv exists
→ display scan cache ~3 s
→ install/delete performs fresh validation
```

Target may improve detection later, but this exact algorithm is the Phase 1/3 parity baseline.

## Target interface

```rust
#[async_trait]
pub trait DeviceStoragePort {
  async fn discover(&self) -> Result<Vec<StorageProbe>, StorageError>;
  async fn stat_target(&self, candidate: &StorageHandle) -> Result<TargetFingerprint, StorageError>;
  // scoped file operations; no arbitrary UI path API
}
```

The native adapter can use blocking OS/filesystem enumeration inside `spawn_blocking`/dedicated workers; core turns raw probes into opaque candidates.

## Discovery lifecycle

1. App starts with cached-none/unknown.
2. Core begins discovery on demand and after relevant window startup.
3. Native enumerates.
4. Core compares set to previous set and emits a **low-frequency `devices-changed` invalidation** only if membership/capability changed.
5. Frontend calls `list_devices()` for authoritative snapshot, or event carries the small snapshot if ADR chooses that shape.
6. Poll interval initially mirrors current low-cost behavior; platform hotplug APIs may replace polling after spike.

Never send a global event every polling tick.

## User-selected folder fallback

Current install flow permits a folder picker if no candidate is found, then calls `Device.IsInstallTarget` and rejects anything without `default.csv`.

Target:
- frontend invokes `choose_device_folder()` domain command;
- native picker returns directly to core, not a reusable arbitrary path string;
- core validates marker + root properties and creates a temporary `StorageDeviceId`/confirmation candidate;
- frontend only receives opaque candidate.

## Multiple devices

If >1 candidate, UI must ask. If exactly 1, auto-selection may preserve current behavior. A cancelled multi-device picker cancels operation; it must not fall through to generic folder choice.

## Wrong/stale device protections

Before install/delete/reorder:
- resolve opaque ID;
- confirm current mount still exists;
- canonicalize root if supported;
- confirm `default.csv` still exists;
- compare target fingerprint/generation if available;
- ensure target file remains direct child of root;
- enforce safe filename/protected filename rules.

## Tests

Fake discovery must cover no device, one, two, stale ID, path reused, marker removed, permission error, slow mount, volume disappears between list and operation, and manual-picker invalid root.