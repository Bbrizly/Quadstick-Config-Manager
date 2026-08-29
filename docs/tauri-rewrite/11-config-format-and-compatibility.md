# Config format and compatibility — release-critical

This is the highest-risk part of the rewrite.

## Current facts to freeze

At audited HEAD:

1. `Csv.Parse` accepts quoted fields and doubled quotes, ignores CR outside quotes, and ends rows on LF.
2. `Csv.Write` quotes comma/quote/CR/LF fields and emits CRLF after every row.
3. `ProfileFile` stores the **raw CSV grid** and reparses after every mutation.
4. Device-visible keyword columns are A..J; K is notes and L is QCM action name. Extra columns may contain user/community information and must survive.
5. `ToCsvText` trims A..J and flattens embedded newlines because firmware line/keyword scanning otherwise misreads apparently valid spreadsheet content.
6. `NormalizeForDeviceCsv` adds/corrects a `QuadStick Configuration,Version 1.5,...` header and true empty separator rows between sheets.
7. Firmware section parsing is case/position sensitive and stops a segment on a **true blank line**, not a row of commas.
8. Keyword safe length is 63 characters; firmware line reader holds at most 1023 bytes before misframing.
9. Profiles are limited by firmware behavior including 16 profiles / 128 accepted bindings per segment; preferences and vocabulary vary by firmware.
10. One-input row is a trigger; multi-input cells encode input history/sequence, not simultaneously held controls.
11. `FirmwareOracle.cs` and `DeviceAgreementTests.cs` are authoritative migration assets.

Read `docs/FORMAT.md` and the corresponding C# tests before porting any parser rule.

## Port structure

```text
qcm-config/src/
  csv.rs
  model.rs
  parser.rs
  validator.rs
  profile.rs
  vocab.rs
  preferences.rs
  function_parameters.rs
  mode_lights.rs
  xlsx.rs             # after CSV parity
```

### Port order

1. Raw CSV parse/write.
2. Plain model types.
3. vocab/data generated fixtures.
4. parser section discovery.
5. binding/preferences/IR projection.
6. validator + issue identities.
7. device-normalizing serializer.
8. `ProfileFile` mutation semantics + undo/revision.
9. XLSX import.
10. qsf CLI.

Do not start by selecting a Rust CSV crate and reshaping behavior around it. The current parser is small and behavior-specific; direct port gives more controllable parity.

## Differential harness

Create a C# oracle executable or extend `qsf` with deterministic commands that emit canonical JSON:

```text
oracle inspect <fixture>
oracle serialize <fixture>
oracle normalize <fixture>
oracle apply <fixture> <ops.json>
oracle firmware <fixture>
```

Rust parity test invokes oracle during migration CI or compares checked-in canonical oracle outputs if cross-runtime process execution is too expensive for every test.

Canonical JSON must include:
- complete raw grid;
- parsed header/document/sheets/bindings;
- row/column identities;
- issue severity/cell/kind;
- action names;
- normalized serialized UTF-8 bytes represented as hash + escaped text fixture where useful.

## Required fixture classes

- official/default templates;
- every existing test fixture;
- representative community profiles;
- headerless config;
- wrong-case header;
- missing separator;
- comma-only pseudo-blank row;
- quoted commas/quotes;
- newline inside quoted comment and keyword cell;
- exactly 63/64/65 character fields;
- 1022/1023/1024+ byte rows;
- unknown/legacy input/output names;
- duplicate action names;
- notes/action columns K/L;
- arbitrary columns M+;
- profile/preferences/infrared mixes;
- `default.csv`, `prefs.csv`;
- malformed/unclosed quote inputs according to current parser behavior;
- LF-only and CRLF;
- non-ASCII comments and names;
- old firmware vocabulary cases.

## Test classes

### Golden parse

For every fixture: C# canonical projection == Rust canonical projection.

### Golden serialize

For every expected-safe fixture: exact UTF-8 serialized bytes when current behavior is contractually relevant.

### Round trip

`parse(write(parse(input)))` must preserve required raw/semantic information; explicitly list intentional normalization deltas.

### Mutation parity

Replay every current editor operation against C# and Rust and compare resulting raw grid, projection, issues, revision/dirty semantics.

### Property tests

Generate grids containing safe arbitrary comment columns, quoting and benign whitespace. Properties:
- parse/write does not panic;
- unknown/comment columns survive unrelated ops;
- applying no ops is identity;
- undo returns prior raw grid;
- normalization is idempotent;
- serialized device-visible cells obey firmware line/field safety checks when validator reports no blocking errors.

### Fuzzing

Fuzz parser and mutation entry points with size/time budgets. No panic, runaway allocation or quadratic blowup for bounded input. Seed corpus from malformed/community fixtures.

## Encoding decision

Current app commonly reads/writes text through .NET UTF-8 defaults. Before locking Rust I/O, characterize BOM handling and invalid UTF-8 behavior. Do not silently replace invalid bytes with lossy Unicode. Open question `OQ-003`.

## Version header / cloud source

Preserve header C1 semantics used for Google Sheet identity. Stamping this bookkeeping field must not become a normal dirty/undo edit unless intentionally changed.

## Completion gate

`qcm-config` cannot be declared parity-complete until **all current `QuadStick.Format.Tests` relevant to parsing/editing/device agreement have an explicit Rust equivalent or remain as a passing cross-language oracle test**.