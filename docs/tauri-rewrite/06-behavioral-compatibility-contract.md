# Behavioral compatibility contract

## Prime rule

A rewrite may change architecture freely; it may change **observable behavior only deliberately**.

Every current behavior is one of:

- **A REQUIRED COMPATIBILITY** — match before legacy retirement.
- **B INTENTIONAL IMPROVEMENT** — separate decision, test old+new expectation.
- **C EXISTING BUG TO FIX** — reproduce with a characterization test, then add corrected test.
- **D UNRESOLVED** — no implementation until evidence/decision exists.
- **E RETIRE** — prove unreachable/tooling-only/unwanted and record why.

## Compatibility hierarchy

For config/device semantics, evidence authority is:

1. QuadStick firmware behavior + existing `FirmwareOracle` evidence.
2. Current passing `DeviceAgreementTests`, format/install/file-management tests.
3. Current C# implementation at audited SHA.
4. `docs/FORMAT.md` when consistent with 1–3.
5. User-visible tests / App tests.
6. README prose.

If sources disagree, do not average them; open a D item.

## Byte/semantic parity

### Exact where required

- `Csv.Write` CRLF/escaping rules where golden fixtures depend on them.
- `ProfileFile.ToCsvText` device-safe output and header/separator normalization.
- install temp read-back must compare exact intended text/bytes according to selected encoding.
- columns K/L and unknown/comment columns must survive unrelated edits.

### Semantic where exact bytes are not a product contract

UI snapshots, internal JSON IPC shape, log formatting and target implementation details need not mirror C# bytes.

## Editor contract

Rust owns canonical profile session state:

```text
ProfileSession { id, source, raw_grid, parsed_document, issues, revision, dirty, undo }
```

Frontend mutation request contains `sessionId`, `expectedRevision`, and one or more typed editor operations. Rust either:

1. applies all operations and returns revision N+1 + snapshot/delta; or
2. applies none and returns a typed validation/concurrency error.

No TS code independently rewrites CSV.

## Destructive-operation contract

A UI confirmation is not a boolean trusted forever. Core issues a short-lived typed `ConfirmationRequirement` (e.g. overwrite-default, install-prefs, delete-profile), UI displays the exact risk, then returns the requirement ID. Core revalidates device identity/path/state immediately before mutation.

## Oracle retirement gate

Do not delete C# format/device oracle code until:

- fixture corpus is copied/versioned;
- Rust parser results match canonical C#/firmware results;
- serializer/device normalization matches expected bytes/semantics;
- mutation/property tests pass;
- hardware install/read-back cases pass;
- porting ledger entries are `PARITY-TESTED` or `HARDWARE-VERIFIED`.

## Improvement isolation

A prettier error, different sort, stricter parser, new normalization, new drive detector, different preference default, changed focus order or changed save timing is **not automatically parity**. Mark B/C and land after parity evidence whenever possible.