# What the rewrite still needs from a human

TASK-001 through TASK-019 are done and gated. This file records what is left and
what genuinely cannot be finished at a desk, so nobody marks a task DONE on a
green unit test that never touched the thing it claims to prove.

## Read the code, not the task wording

Several task rows in `50-first-implementation-tasks.md` say "real device" or
"on actual QuadStick". Taken literally they suggest most of Phase 3 is blocked
on hardware. The shipped C# implementation says otherwise, and it is the
authority:

    public static bool IsInstallTarget(string deviceRoot) =>
        File.Exists(Path.Combine(deviceRoot, "default.csv"));

A device is a directory with `default.csv` in its root. `DeviceTests` builds one
with `Directory.CreateTempSubdirectory` and writes that marker. The whole
install, backup, readback, mid-swap restore, delete and ordering surface is
covered that way today, with no hardware, and that is how this app was built.

The same applies to HID. TASK-026 is written as a spike to discover report
descriptors on a real unit, but that discovery already happened: `LiveInput.cs`
uses HidSharp, enumerates by vendor and product, and its comments already record
which interface is the stick and that the 0xFF interface is XInput rather than
HID. The rewrite ports a known answer instead of rediscovering it.

So treat those rows as porting work with a hardware acceptance step at the end,
not as hardware-blocked development.

## Actually needs a QuadStick plugged in

Only these, and all of them are release validation rather than development:

- The `FindCandidates()` removable and macOS `/Volumes` heuristic. An unmounted
  volume cannot be enumerated. The logic is small and the root is injectable, so
  everything above it still tests without hardware.
- TASK-053 soak: repeated physical reconnects, sleep and wake.
- TASK-056 release candidate matrix: install, readback, delete, order,
  preferences and live input on a real unit before shipping.
- TASK-060 mobile feasibility, which needs phones as well.

## Actually needs credentials

The credentials exist and already ship the product. `GOOGLE_CLIENT_ID`,
`GOOGLE_CLIENT_SECRET` and `POSTHOG_PROJECT_TOKEN` are GitHub Actions secrets.
There is no `codesign`, `notarize` or `signtool` step anywhere in the workflows
or the Makefile, so store signing is interactive on the owner's machine.

The constraint is therefore access, not existence: an agent working locally
cannot read those secrets, so TASK-044 Google auth is written against mocks and
proved in CI or by hand, and the signing tasks (050, 051, 055, 058) stay owner
operated exactly as they are today.

## Needs a person

TASK-049 is real assistive technology: NVDA, Narrator and VoiceOver driven by
someone who uses them. `25-accessibility.md` is explicit that a critical blocker
there cannot be waived by an automated green check. TASK-059, retiring the
Avalonia source, is a judgment call the definition of done already puts in its
own phase.

## The route to something you can click

Gate 4 asks for "a minimal web UI can open/edit/save via native core". Shortest
honest path, none of it hardware bound: TASK-020, TASK-021, TASK-030, TASK-031,
TASK-032, TASK-038, with TASK-035 and TASK-036 straight after for capabilities
and the accessible shell.

## Open questions that change code, not just docs

The full list is in `51-open-questions.md`. These reshape a task rather than
decorate it:

- **OQ-004 shapes TASK-020.** Does local save stay at parity with the C#
  `ProfileFile` path, or adopt the device grade backup and readback contract?
  Being written to parity for now, because
  `06-behavioral-compatibility-contract.md` governs and improvements are a later
  phase. Revisit before TASK-032 freezes the command surface.
- **OQ-005 shapes TASK-025.** What really sets device load order and file
  numbering, and is reorder a rename or a metadata write? Alphabetical display
  sort is not evidence of firmware order.
- **OQ-001 shapes TASK-029.** Is the serial path a live production route or
  vestigial? Wrong either way ships dead code or drops a feature.
- **OQ-008 shapes TASK-050.** Does the Windows Store package continue?
- **OQ-015 shapes TASK-051 and TASK-052.** What are the minimum supported OS
  versions under Tauri, WebView and HID?
