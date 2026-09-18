# Performance and reliability

## Measure before promising

TASK-006 records Avalonia baseline on representative Windows/macOS hardware. Do not claim Tauri is faster because the stack sounds lighter.

## Metrics

- process cold start → first interactive shell;
- open representative 50/500-binding profile;
- parse/validate/serialize time;
- editor operation latency;
- install staging/readback duration separated from USB speed;
- device discovery latency;
- live HID packet → visual update latency;
- idle CPU/memory;
- live-input CPU/memory;
- 2-hour memory growth;
- packaged size;
- shutdown time.

## Initial budgets

Set hard budgets **after baseline**. Before numbers exist, use relative gates:
- no >20% regression in common editor operation p95 without approved reason;
- live input must visually stay within one render frame + transport scheduling under normal load;
- memory must plateau in long-run stream/connect cycles;
- no unbounded queues/caches.

Replace relative placeholders with absolute product budgets at Gate 1.

## Reliability soak

Automate fake 10,000 cycles where cheap:
- editor mutation/undo;
- device discovery set changes;
- live stream start/stop.

Physical soak:
- >=100 connect/unplug/replug cycles per primary desktop OS during beta;
- 2+ hours live HID;
- repeated device installs to sacrificial test files with byte verification;
- sleep/wake while live stream active;
- app reopen with rescued/dirty state.

## Resource leak checks

Track open handles/thread count where OS tooling allows. A live stream stop must release HID handle promptly enough for restart/other software. Device temp files should not accumulate after clean successful operations.

## Frontend performance

React Profiler/manual instrumentation around visualizer and large grid. Virtualize raw grid only if measured need; virtualization must not break keyboard/screen-reader traversal. Prefer structural rendering fixes over premature memoization everywhere.