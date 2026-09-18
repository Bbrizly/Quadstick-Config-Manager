# Serial transport

## Status: **UNRESOLVED / NOT A PARITY BLOCKER UNTIL PROVEN**

`System.IO.Ports` exists in `QuadStick.App.csproj`, and historical/product documentation discusses QuadStick serial console/Bluetooth serial. The first audited production paths for install, device library and live input use **mass storage + HID**, not a demonstrated serial service.

Do not add a Rust serial runtime simply because the old project references the package.

## Required Phase-0 investigation (`OQ-001`)

Search all source, tests, git history and release notes for:
- `SerialPort`, COM/tty paths, baud/parity/stop bits;
- RN-42/Bluetooth console behavior;
- firmware commands/responses;
- user-visible flows that require serial;
- dead/removed experiments.

Classify any found behavior A/B/D/E.

## If serial is required

Create a port isolated from UI/Tauri:

```rust
#[async_trait]
pub trait SerialTransportPort {
  async fn list(&self) -> Result<Vec<SerialCandidate>, SerialError>;
  async fn open(&self, id: SerialCandidateId, settings: SerialSettings) -> Result<Box<dyn SerialSession>, SerialError>;
}
```

Then run a dependency/platform spike comparing current stable `serialport` and async alternatives. The `serialport` project currently exposes cross-platform APIs but has publicly sought additional maintainers, especially for Windows; maintenance risk must be recorded rather than ignored.

## Required serial spec before shipping it

Document from source/hardware evidence:
- baud/data/parity/stop/flow settings;
- enumeration/filtering and identity;
- framing/delimiters;
- command/response correlation;
- read/write timeout;
- cancellation;
- reconnect;
- Bluetooth Classic differences;
- Windows COM, macOS `/dev/cu.*` vs `/dev/tty.*`, Linux tty permissions;
- simultaneous HID/storage/serial behavior;
- actual hardware tests.

## Forbidden shortcut

Never expose `open_serial(portName)` or `serial_write(bytes)` to arbitrary frontend code. Even if serial is added, commands remain domain-level (`read_console_info`, `start_device_telemetry`, etc.).