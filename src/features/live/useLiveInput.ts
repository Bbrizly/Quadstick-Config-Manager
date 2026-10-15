import { useEffect, useRef, useState } from "react";

import type { LiveSnapshot, QcmClient } from "../../platform";

type Schedule = (run: () => void) => number;
type Cancel = (handle: number) => void;

export interface FrameCoalescer {
  push(frame: LiveSnapshot): void;
  cancel(): void;
}

/**
 * At most one delivery per animation frame, always the newest frame. The
 * stick can report a few hundred times a second; the screen only draws sixty,
 * so every report past the first in a frame would be a React render nobody
 * sees.
 */
export function createFrameCoalescer(
  deliver: (frame: LiveSnapshot) => void,
  schedule: Schedule = (run) => requestAnimationFrame(run),
  cancel: Cancel = (handle) => cancelAnimationFrame(handle),
): FrameCoalescer {
  let pending: LiveSnapshot | null = null;
  let handle: number | null = null;
  return {
    push(frame) {
      pending = frame;
      if (handle !== null) return;
      handle = schedule(() => {
        handle = null;
        const latest = pending;
        pending = null;
        if (latest !== null) deliver(latest);
      });
    },
    cancel() {
      if (handle !== null) cancel(handle);
      handle = null;
      pending = null;
    },
  };
}

/**
 * Whether this window is worth drawing live input into. A QuadStick is usually
 * driving a game while this app sits behind it, so a hidden, minimised or
 * unfocused window stops reading altogether: the native worker sees no
 * subscriber, stops enumerating HID and closes the device.
 *
 * DOM focus and visibility, not Tauri window events: the WebView forwards the
 * native window's focus and minimise, and listening through Tauri would need an
 * event permission the main capability deliberately does not grant.
 */
export function useWindowActive(): boolean {
  const [active, setActive] = useState(
    () => typeof document === "undefined" || (document.visibilityState !== "hidden" && document.hasFocus()),
  );
  useEffect(() => {
    const update = (): void => {
      setActive(document.visibilityState !== "hidden" && document.hasFocus());
    };
    const blur = (): void => setActive(false);
    document.addEventListener("visibilitychange", update);
    window.addEventListener("focus", update);
    window.addEventListener("blur", blur);
    return () => {
      document.removeEventListener("visibilitychange", update);
      window.removeEventListener("focus", update);
      window.removeEventListener("blur", blur);
    };
  }, []);
  return active;
}

/**
 * Live input for one view: subscribed only while the window is active,
 * coalesced to one update per animation frame. `onFrame(null)` means there is
 * no reading to draw (paused, or the stream failed), so the view must let go
 * of anything it shows as held. Returns whether the window is active, so a
 * view can stop its own idle animations with it.
 */
export function useLiveInput(client: QcmClient, onFrame: (frame: LiveSnapshot | null) => void): boolean {
  const active = useWindowActive();
  const latest = useRef(onFrame);
  useEffect(() => {
    latest.current = onFrame;
  });

  useEffect(() => {
    if (!active) {
      latest.current(null);
      return undefined;
    }
    let disposed = false;
    let subscription: { dispose(): void } | null = null;
    const coalescer = createFrameCoalescer((frame) => {
      if (!disposed) latest.current(frame);
    });
    void client
      .startLiveInput((frame) => {
        if (!disposed) coalescer.push(frame);
      })
      .then((value) => {
        if (disposed) value.dispose();
        else subscription = value;
      })
      .catch(() => {
        if (!disposed) latest.current(null);
      });
    return () => {
      disposed = true;
      coalescer.cancel();
      subscription?.dispose();
    };
  }, [active, client]);
  return active;
}
