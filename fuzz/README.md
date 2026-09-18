# qcm-config fuzzing

Coverage-guided fuzz targets for the pure Rust configuration core. This is an
independent Cargo workspace so libFuzzer/nightly never becomes a dependency of
normal Windows, macOS or Linux product builds.

Pinned tooling used for TASK-018:

- cargo-fuzz 0.13.2
- libfuzzer-sys 0.4.13
- nightly-2026-08-20

Targets:

- `parse` — bounded UTF-8 CSV parse/write/reparse.
- `normalize` — load/normalize/serialize and assert normalization idempotence.
- `apply_ops` — bounded sequences of editor mutations, undo and normalization.

The committed corpus is seeded from rewrite parity fixtures that represent
wrong-case headers, missing true blank separators and arbitrary M+ columns, plus
an unclosed-quote parser seed.

A bounded smoke run is intentionally finite:

```sh
cargo +nightly-2026-08-20 install cargo-fuzz --version 0.13.2 --locked
cargo +nightly-2026-08-20 fuzz build
cargo +nightly-2026-08-20 fuzz run parse fuzz/corpus/parse -- -runs=256 -max_len=65536 -timeout=2
cargo +nightly-2026-08-20 fuzz run normalize fuzz/corpus/normalize -- -runs=256 -max_len=65536 -timeout=2
cargo +nightly-2026-08-20 fuzz run apply_ops fuzz/corpus/apply_ops -- -runs=256 -max_len=65536 -timeout=2
```

Long fuzz campaigns should increase `-runs` or use `-max_total_time`, while
retaining the 64 KiB input cap. Any crashing input belongs in the corresponding
corpus directory as a permanent regression seed.
