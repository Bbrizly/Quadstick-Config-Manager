# ADR-015 — Live input coalesces to one latest snapshot

**Status:** ACCEPTED

## Decision
The buffer between the HID reader and the window holds exactly one snapshot. Publishing over one that has not been taken replaces it and counts a coalesce. There is no queue and no ring, and nothing waits for the window to catch up.

Every snapshot carries the whole state, not a delta, and the motion is inside the `Reading` variant so no other state can express a held button.

## Why
The reader runs on the device's clock, a few hundred reports a second, and the window paints on the screen's. Any queue between them grows for as long as they disagree, and live input is state, not an audit log: an intermediate position nobody drew is not worth a byte of memory.

Latest-wins is also the safe direction, and that is the real reason. A disconnect publishes a snapshot with nothing held. Under a first-wins or bounded-ring buffer that snapshot queues behind the pressed frames already in it, so a slow window can be handed a held button after the device has gone and never be told otherwise. On a sip-and-puff controller that is somebody's input stuck down with no way to let go. With one slot and latest-wins, the release cannot be overtaken.

A sequence number is the compensation. A window that sees `seq` jump by more than one knows intermediate states were dropped, and the one it holds is still current.

## Consequence
Nothing downstream may assume it sees every report. Anything that needs the full stream, a recorder or a practice replay, gets its own sink at the port rather than a bigger buffer here.

`LiveStream::stats` reports published, delivered and coalesced, so back pressure is measurable rather than guessed at.

## Revisit trigger
A consumer that genuinely needs every intermediate report.
