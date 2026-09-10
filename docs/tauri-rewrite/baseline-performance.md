# Avalonia baseline performance

Status: **TASK-005 complete as a reproducible baseline record; runtime/hardware metrics remain explicitly unknown until measured on reference machines.**

The task contract allows unknown metrics when they are named rather than guessed. This document therefore separates published/package evidence from measurements that cannot be truthfully produced in headless GitHub CI.

## Comparison identity

- Rewrite compatibility base: `main@f7783944387202bcafaeb7ff3f67789098fa6a4e`
- Latest published stable release available at freeze: `v1.6.2` (2026-08-11)
- Rewrite CI runs with `QSCM_TELEMETRY=0`.

The v1.6.2 assets are a **published distribution-size reference**, not a claim that their commit is byte-identical to the frozen 2026-08-29 source.

## Published package-size reference

GitHub release `v1.6.2` reports:

| Artifact | Compressed release bytes |
|---|---:|
| Linux x64 | 38,985,605 |
| macOS Apple Silicon | 43,803,356 |
| macOS Intel | 45,436,569 |
| Windows x64 | 41,169,906 |

These numbers are suitable for a gross before/after distribution-size comparison. A Tauri release should additionally record installed/uncompressed size because compressed ZIP/TAR ratios differ by runtime composition.

## Runtime baseline matrix

| Metric | Frozen Avalonia baseline | State |
|---|---|---|
| cold launch → usable home | unknown | **NEEDS LOCAL MEASUREMENT** |
| open representative profile | unknown | **NEEDS LOCAL MEASUREMENT** |
| parse representative profile | legacy behavior covered by tests; wall time unknown | **NEEDS BENCHMARK** |
| save representative profile | atomic behavior covered by tests; wall time unknown | **NEEDS BENCHMARK** |
| idle resident memory | unknown | **NEEDS LOCAL MEASUREMENT** |
| live-input resident memory | unknown | **NEEDS HARDWARE** |
| HID report → visual update latency | unknown | **NEEDS HARDWARE/INSTRUMENTATION** |
| 30-minute live session stability | unknown | **NEEDS HARDWARE SOAK** |

No numeric target may be justified as an “improvement” against an unknown row.

## Reproduction protocol

For every desktop comparison, record:

1. exact git SHA and build configuration;
2. OS version, CPU, RAM and architecture;
3. whether the build is packaged or `dotnet run`/`cargo tauri dev`;
4. telemetry disabled (`QSCM_TELEMETRY=0`);
5. the same profile fixture and same QuadStick/firmware for hardware tests;
6. five cold-launch/profile runs after one warm-up, recording median and range;
7. idle memory after 60 seconds with no profile, then after profile open;
8. live memory and report latency after a five-minute HID stream;
9. a 30-minute live-input session with reconnect at minute 15;
10. package compressed bytes and installed/uncompressed bytes.

### Recommended commands

macOS:

```bash
/usr/bin/time -l ./QuadStick\ Config\ Manager.app/Contents/MacOS/QuadStick.App
ps -o pid,rss,command -p <pid>
```

Windows PowerShell:

```powershell
Measure-Command { Start-Process -Wait <app> }
Get-Process <process-name> | Select-Object WorkingSet64,PrivateMemorySize64
```

Linux:

```bash
/usr/bin/time -v ./QuadStick.App
ps -o pid,rss,vsz,cmd -p <pid>
```

For launch timing, manual stopwatch is insufficient for final claims: add an app-ready marker to both implementations or use OS/process instrumentation that records process start and first usable-window signal.

## Target-budget rule

Set numeric Rust/Tauri performance budgets only after at least one exact frozen-Avalonia measurement exists for that row. Until then the requirement is **no obvious regression under equivalent workload**, backed by functional/soak tests rather than invented percentages.
