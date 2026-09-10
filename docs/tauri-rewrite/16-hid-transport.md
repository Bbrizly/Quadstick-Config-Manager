# HID transport — required for live/practice parity

## Current contract

`LiveInput.cs` uses HidSharp and a known QuadStick VID/PID allowlist. A physical QuadStick can expose mouse, keyboard and gamepad interfaces; current code inspects the HID report descriptor and only reads a joystick/gamepad/multi-axis top-level collection.

Current known identities include QuadStick native IDs and compatible controller-emulation IDs (DS3/DS4/Switch/HORI modes). Native Xbox 360/XInput mode is explicitly not handled by this HID reader.

The current reader:
- opens the selected HID stream;
- derives axis/button fields from descriptor usages;
- normalizes X/Y to [-1, 1];
- reports pressed button usage numbers;
- suppresses tiny axis jitter (< ~0.01) and duplicate frames;
- treats disconnect/errors as nonfatal and rescans.

## Target adapter

```rust
pub trait LiveInputPort {
  fn supported_candidates(&self) -> Result<Vec<HidCandidate>, HidError>;
  fn start(&self, candidate: Option<HidDeviceId>, sink: LiveFrameSink, cancel: CancellationToken)
      -> Result<LiveInputTask, HidError>;
}
```

Use a dedicated blocking worker/thread for blocking HID reads. Do not block Tauri's async command executor. The worker owns the device handle and exits on cancellation/unplug.

## Dependency

`hidapi` 2.6.6 is a current Rust candidate with cross-platform backends. **TASK spike must prove**:
- report descriptor availability/quality on Windows/macOS/Linux;
- enumeration parity for all current VID/PIDs;
- behavior with composite interfaces;
- cancellation of blocking reads;
- packaging/native dependencies.

If its descriptor API is insufficient, port/introduce a focused HID descriptor parser rather than fall back to magic byte offsets.

## Streaming pipeline

```text
blocking HID read
→ descriptor-based normalized LiveFrame
→ dedupe/jitter filter
→ bounded/latest-state core channel
→ Tauri `Channel<LiveFrameDto>`
→ `useLiveInput()` hook
→ requestAnimationFrame coalescing
→ SVG + textual status
```

Tauri Channels are selected because official Tauri IPC guidance says event system is not designed for low-latency/high-throughput streaming and channels are ordered/streaming-oriented.

## Live frame DTO

```ts
interface LiveFrame {
  seq: number;
  monotonicMs: number;
  x: number;
  y: number;
  buttons: number[];
  product: string;
}
```

Do not assume button number implies mouthpiece input: current code explicitly notes the loaded profile determines how physical inputs map to output report buttons.

## Accessibility

Live visual motion is supplementary. Text “Reading <product>; pressed 1, 4” remains queryable but is **not** an assertive live region, matching current anti-spam intent. Provide pause/stop live input and reduced-motion behavior.

## Tests

- synthetic descriptor parsing;
- composite device chooses gamepad collection;
- axis min/max normalization;
- missing axis centers at zero;
- button arrays;
- jitter dedupe;
- disconnect/backoff;
- XInput-only limitation state;
- 30-minute synthetic high-rate stream with bounded memory;
- real hardware matrix for each practical mode.