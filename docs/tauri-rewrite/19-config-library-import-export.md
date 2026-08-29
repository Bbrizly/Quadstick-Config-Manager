# Config library, import and export

## Surfaces to preserve

- local open/save/save-as;
- mounted QuadStick profile list/manage/install/delete/reorder as currently supported;
- special `default.csv` / `prefs.csv` protections;
- CSV import;
- XLSX workbook import and skipped-tab/limitation reporting;
- community profile open/import;
- Google Drive restore/import/share flows;
- backup/recovery directories;
- qsf machine-readable operations.

## Native file picker model

Use Tauri/native dialog functionality behind **domain commands**, not frontend filesystem capability:

```text
choose_and_open_profile()
choose_save_destination(sessionId)
choose_import_file()
export_profile(sessionId, format)
```

The picker result becomes an opaque `LocalFileRef`. Do not return a reusable unrestricted file handle/path to JavaScript.

## Import trust boundary

Every external CSV/XLSX/cloud/community file is untrusted:
- bound maximum bytes, rows, cells and workbook expansion before full processing;
- reject/diagnose invalid UTF-8 according to compatibility decision;
- never execute spreadsheet formulas/macros;
- XLSX is treated as ZIP/XML data only;
- sanitize displayed filenames/text;
- no HTML injection from notes/action names;
- run parser/validator before enabling device install.

## XLSX

Port current `Xlsx` behavior only after CSV core is stable. Preserve:
- exported sheet ordering;
- helper/skipped tab semantics;
- limitation strings/structured warnings;
- conversion to device-normalized row numbering used by qsf/import review.

## Device library DTO

```ts
interface DeviceLibrarySnapshot {
  deviceId: string;
  generation: number;
  files: DeviceProfileEntry[];
  protectedFiles: string[];
  ordering: DeviceOrderingInfo;
}
```

Mutations carry expected generation/revision and return a fresh snapshot.

## Export

Local export must preserve current serialized profile exactly unless user explicitly chooses a transformed format. A “share” export must not silently strip K/L/custom columns.

## Conflict behavior

For local save, device install and cloud restore, define conflict separately. Never use one generic “overwrite?” branch for all because current backup/recovery semantics differ.

## Tests

File-name collisions, case-insensitive filesystem collisions, missing extension, reserved names, read-only folder, external edit between open/save, XLSX with formulas/macros, huge ZIP expansion, cloud malformed sheet, device unplug during library refresh, and lossless K/L/M+ roundtrip.