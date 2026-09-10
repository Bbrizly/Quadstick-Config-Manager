# Device model

## Do not model “the QuadStick” as one magical persistent socket

Current QCM interacts through at least two independent capabilities:

- mounted USB mass storage (`default.csv` marker and profile files);
- HID gamepad reports for live input/practice.

A serial dependency exists in the project, but production serial use is not proven by the audited paths yet. Treat serial as optional until `OQ-001` is resolved.

## IDs

Native code creates opaque IDs per discovered resource:

```rust
StorageDeviceId
HidDeviceId
DeviceFileId
ProfileSessionId
OperationId
```

Do not send raw mount paths as object identity to the frontend. A DTO can include a sanitized user-facing volume label/location hint, but mutation commands operate on IDs.

## Storage candidate

```rust
pub struct StorageCandidate {
  id: StorageDeviceId,
  display_name: String,
  capabilities: StorageCapabilities,
  generation: u64,
}
```

Private adapter state maps ID → canonical root/path fingerprint. Each destructive call validates that mapping is current and that `default.csv` still proves the target.

## HID candidate

```rust
pub struct HidCandidate {
  id: HidDeviceId,
  product_name: String,
  vid: u16,
  pid: u16,
  live_capable: bool,
  limitation: Option<LiveInputLimitation>,
}
```

Avoid exposing OS device paths unless diagnostics explicitly request sanitized details.

## Aggregation

For v1 target UI, show capability status independently:

```text
Storage: QuadStick drive available / absent
Live input: reading <product> / unavailable / XInput-only limitation
```

Do not claim the storage candidate and HID candidate are physically the same unit unless the platform exposes a tested stable correlation mechanism.

## Device capabilities

The application asks capabilities rather than OS type:

- `can_list_profiles`
- `can_install_profile`
- `can_manage_profile_order`
- `can_edit_preferences`
- `can_stream_live_input`
- `live_input_limitation`
- future `can_use_serial_console`

## Hotplug generation

Every rediscovery increments a generation. Long operations capture the expected generation/fingerprint and revalidate immediately before destructive I/O. A stale UI ID cannot point at a newly mounted unrelated drive simply because the OS reused a mount letter/path.