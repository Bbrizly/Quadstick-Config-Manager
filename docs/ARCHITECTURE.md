# Architecture

QuadStick Config Manager is intentionally split into four layers.

```text
QuadStick.App (Avalonia presentation)
          |
          v
QuadStick.Application (use cases + ports)
          |
          v
QuadStick.Core (QuadStick/profile semantics)
          ^
          |
QuadStick.Infrastructure (filesystem, mounted devices, Google, HTTP, OS, telemetry)
```

The dependency rules are enforced by project references:

- `QuadStick.Core` references no project in this repository.
- `QuadStick.Application` references `QuadStick.Core` only.
- `QuadStick.Infrastructure` references `QuadStick.Application` and `QuadStick.Core`.
- `QuadStick.App` may reference all three and is the composition root.

## Ownership

**Core knows QuadStick.** Parsing, validation, profile editing, serialization, preference definitions, workbook parsing and deterministic device rules live here. Core must not scan disks, open user files, call HTTP services, know Avalonia, or know provider APIs.

**Application knows operations.** Opening/saving profiles, discovering devices, installing/deleting profiles, backup/restore/share workflows and update/catalog workflows live here. Interfaces representing the outside world are defined here and implemented by Infrastructure.

**Infrastructure knows the outside world.** Mounted-volume access, filesystem persistence, Google APIs/authentication, HTTP catalogs, settings storage, credential storage, PostHog and OS-specific behavior live here.

**App knows the human.** Avalonia views, code-behind/ViewModels, dialogs, file pickers, clipboard/browser launching, focus, keyboard behavior, accessibility and theming live here.

## Hard rules

- No Avalonia reference from Core, Application, or Infrastructure.
- No `File`, `Directory`, `DriveInfo`, provider SDK/API, or network access from Core.
- No concrete Infrastructure construction from Application.
- No device filesystem writes, Google REST calls, validation rules, or PostHog implementation in views/windows.
- Dependencies arrive through constructors; no service locator.
- Expected user decisions (confirmation, remote conflict, missing remote, cancellation) are structured results rather than UI callbacks in business logic.
- Device transports expose focused capabilities; do not create one `IQuadStickEverything` interface.
- Preserve all existing safety, privacy, settings-compatibility, and accessibility behavior while moving code.

## Migration policy

This refactor is incremental. Move existing behavior first, keep tests green, then clean names/presentation code. Do not combine it with a .NET/Avalonia upgrade or UI redesign. Legacy namespaces may temporarily survive across new assemblies to keep diffs reviewable; the project boundary, not namespace spelling, is the architectural firewall.
