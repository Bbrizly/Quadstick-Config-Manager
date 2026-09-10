# Target domain model

Names are target defaults; change only through ADR/API-ledger update.

## Config domain (`qcm-config`)

```rust
pub struct RawGrid(pub Vec<Vec<String>>);

pub struct ProfileFile {
    raw: RawGrid,
    document: ProfileDocument,
    issues: Vec<Issue>,
    revision: u64,
    dirty: bool,
    undo: Vec<RawGrid>,
    header_sheet_id: Option<String>,
}

pub struct ProfileDocument {
    pub sheets: Vec<ModeSheet>,
    pub csv_file_name: Option<String>,
    pub header: Option<VersionHeader>,
}

pub enum SheetType { ProfileName, Preferences, Infrared }
pub enum Severity { Warning, Error }
```

**Invariant:** parsed `ProfileDocument` is a projection of `RawGrid`, never the sole persistence representation. Unknown/comment cells survive unrelated mutations.

## Editor operations

Expose an enum instead of arbitrary JSON patching:

```rust
pub enum EditorOp {
    SetCell { row: u32, col: u32, value: String },
    SetOutput { row: u32, token: String, action_name: String },
    SetBinding { row: u32, output: String, function: String, inputs: Vec<String>, action: String },
    AddBindingRow { sheet: u32 },
    DeleteRow { row: u32 },
    MoveRows { rows: Vec<u32>, destination: RowDestination },
    AddMode { name: String },
    RenameMode { sheet: u32, name: String },
    AddPreferencesSheet,
    Undo,
    NormalizeForDevice,
    // add every current ProfileFile mutation explicitly during Phase 0
}
```

Do not expose `replace_raw_grid` to normal UI/agent code. Raw import is a separate trust-boundary use case.

## Core profile session

```rust
pub struct ProfileSessionId(Uuid);

pub struct ProfileSession {
    id: ProfileSessionId,
    source: ProfileSource,
    profile: qcm_config::ProfileFile,
    last_persisted_revision: u64,
}

pub enum ProfileSource {
    Unsaved,
    Local(LocalFileRef),
    Device(DeviceFileRef),
    Community(CommunityProfileRef),
    Cloud(CloudProfileRef),
    Imported(ImportOrigin),
}
```

`LocalFileRef` and `DeviceFileRef` are **native opaque handles/IDs**, not arbitrary frontend paths.

## Snapshot DTO

The frontend receives a serializable projection:

```ts
export interface EditorSnapshot {
  sessionId: string;
  revision: number;
  dirty: boolean;
  title: string;
  csvFileName?: string;
  source: ProfileSourceDto;
  sheets: SheetDto[];
  issues: IssueDto[];
  actionNames: ActionNameDto[];
  capabilities: EditorCapabilitiesDto;
}
```

Raw grid may be exposed only to an **advanced grid view** through a bounded typed DTO because that is a legitimate product surface; it still cannot be mutated outside typed editor operations.

## Device domain

Treat interfaces as capabilities, not assume one permanent connection:

```rust
pub struct StorageDeviceId(Uuid);
pub struct HidDeviceId(Uuid);
pub struct DeviceFileRef { device: StorageDeviceId, file_id: DeviceFileId }

pub struct DevicePresence {
    pub storage: Vec<StorageCandidate>,
    pub hid: Vec<HidCandidate>,
}
```

Current app does not prove a stable cross-interface identifier tying a mounted drive to a HID interface. Do not invent one. A future `LogicalQuadStickId` requires evidence.

## Storage capability states

`Absent → Discovering → Available → Busy(op) → Available`, with `Error` and hot-unplug transitions. Mount storage does not require a persistent “connected” handle in the same sense as serial.

## HID capability states

`Stopped → Scanning → Streaming → Backoff → Scanning`, with cancellation to `Stopped`. One active live-input stream initially matches current UI behavior.

## Confirmation domain

```rust
pub struct ConfirmationRequirement {
  id: ConfirmationId,
  kind: ConfirmationKind,
  summary: String,
  expires_at: Instant,
  operation_fingerprint: String,
}
```

Core creates this after validation. UI acknowledges ID. Core recomputes/revalidates the target before writing and rejects stale confirmations.

## Error domain

Internal errors carry source/debug context; IPC uses stable codes. See `33-error-model-and-recovery.md`.

## Cloud/link identity

Preserve profile-carried Google Sheet identity (header C1) separately from local path mappings. Path rename must not orphan the cloud identity, matching current recovery intent.