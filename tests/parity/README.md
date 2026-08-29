# QCM format parity runner

The rewrite has one compatibility entrypoint:

```bash
python3 scripts/parity.py
# or: make parity
```

It regenerates the current C# oracle outputs from the frozen legacy implementation, validates the oracle envelope, then runs the Rust format workspace with every oracle marked required. A missing oracle is therefore a failure rather than a skipped test.

Prerequisites: .NET 8, the repository's pinned Rust toolchain, Python 3, and `jsonschema==4.26.0`.

CI uses the same runner in two stages:

```bash
python3 scripts/parity.py --generate-only
python3 scripts/parity.py --rust-only
```

The split is only to transfer generated oracle artifacts between GitHub Actions jobs; the default local command executes both stages.

Current parity families are raw CSV, catalog/vocabulary, parser projection, validation issues, normalization/serialization, typed editor operations, and editor undo/dirty/revision state. Individual Rust tests include the fixture ID and parity field in assertion messages so failures identify the first contract area to inspect.

Do not add a new format behavior to CI by hand. Add its oracle generation and required Rust test to `scripts/parity.py`, then have CI continue calling this runner.
