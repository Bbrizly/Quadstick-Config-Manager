import { act, render, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MockQcmClient, type LiveSnapshot } from "../../platform";
import { createFrameCoalescer, useLiveInput } from "./useLiveInput";

const frame = (seq: number): LiveSnapshot => ({
  seq,
  atMillis: seq,
  status: { kind: "reading", product: "QuadStick", motion: { x: seq / 10, y: 0, buttons: [] } },
});

describe("createFrameCoalescer", () => {
  it("delivers only the newest frame once per animation frame", () => {
    const runs: (() => void)[] = [];
    const delivered: number[] = [];
    const coalescer = createFrameCoalescer(
      (next) => delivered.push(next.seq),
      (run) => runs.push(run),
      () => undefined,
    );
    coalescer.push(frame(1));
    coalescer.push(frame(2));
    coalescer.push(frame(3));
    expect(runs).toHaveLength(1);
    runs[0]?.();
    expect(delivered).toEqual([3]);
    coalescer.push(frame(4));
    expect(runs).toHaveLength(2);
    runs[1]?.();
    expect(delivered).toEqual([3, 4]);
  });

  // The stray frame request on a still screen: with no QuadStick, the worker
  // still says "searching" or "unavailable" now and then (a rescan, a window
  // coming back). A status is one render, not a stream, so it needs no frame,
  // and one that says what the screen already shows needs nothing at all.
  it("asks for no animation frame for a status, only for motion", () => {
    const runs: (() => void)[] = [];
    const delivered: string[] = [];
    const coalescer = createFrameCoalescer(
      (next) => delivered.push(next.status.kind),
      (run) => runs.push(run),
      () => undefined,
    );
    coalescer.push({ seq: 1, atMillis: 1, status: { kind: "searching" } });
    coalescer.push({ seq: 2, atMillis: 2, status: { kind: "unavailable", code: "QCM_DEVICE_IO" } });
    expect(runs).toHaveLength(0);
    expect(delivered).toEqual(["searching", "unavailable"]);
  });

  it("delivers nothing for a frame that draws what the last one drew", () => {
    const runs: (() => void)[] = [];
    const delivered: number[] = [];
    const coalescer = createFrameCoalescer(
      (next) => delivered.push(next.seq),
      (run) => runs.push(run),
      () => undefined,
    );
    coalescer.push({ seq: 1, atMillis: 1, status: { kind: "unavailable", code: "QCM_DEVICE_IO" } });
    coalescer.push({ seq: 2, atMillis: 9, status: { kind: "unavailable", code: "QCM_DEVICE_IO" } });
    coalescer.push(frame(3));
    runs[0]?.();
    coalescer.push(frame(4));
    coalescer.push({ ...frame(3), seq: 5 });
    // Frame 4 was overtaken within its frame by one the screen already shows.
    runs[1]?.();
    expect(delivered).toEqual([1, 3]);
    expect(runs).toHaveLength(2);
  });

  it("a status cancels motion still waiting for its frame, so order holds", () => {
    const cancel = vi.fn();
    const runs: (() => void)[] = [];
    const delivered: string[] = [];
    const coalescer = createFrameCoalescer(
      (next) => delivered.push(next.status.kind),
      (run) => runs.push(run) - 1,
      cancel,
    );
    coalescer.push(frame(1));
    coalescer.push({ seq: 2, atMillis: 2, status: { kind: "stale", product: "QuadStick" } });
    expect(cancel).toHaveBeenCalledWith(0);
    runs[0]?.();
    expect(delivered).toEqual(["stale"]);
  });

  it("after a cancel the next status is delivered even if it repeats", () => {
    const delivered: number[] = [];
    const coalescer = createFrameCoalescer((next) => delivered.push(next.seq), () => 0, () => undefined);
    coalescer.push({ seq: 1, atMillis: 1, status: { kind: "searching" } });
    coalescer.cancel();
    coalescer.push({ seq: 2, atMillis: 2, status: { kind: "searching" } });
    expect(delivered).toEqual([1, 2]);
  });

  it("drops a pending frame when cancelled", () => {
    const cancel = vi.fn();
    const delivered: number[] = [];
    const runs: (() => void)[] = [];
    const coalescer = createFrameCoalescer(
      (next) => delivered.push(next.seq),
      (run) => runs.push(run) - 1,
      cancel,
    );
    coalescer.push(frame(1));
    coalescer.cancel();
    expect(cancel).toHaveBeenCalledWith(0);
    runs[0]?.();
    expect(delivered).toEqual([]);
  });
});

function Probe({ client, seen }: { readonly client: MockQcmClient; readonly seen: (LiveSnapshot | null)[] }) {
  useLiveInput(client, (next) => seen.push(next));
  return null;
}

describe("useLiveInput", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("unsubscribes while the window is unfocused or hidden and resumes on return", async () => {
    const client = new MockQcmClient();
    const seen: (LiveSnapshot | null)[] = [];
    let hasFocus = true;
    let visibility: DocumentVisibilityState = "visible";
    vi.spyOn(document, "hasFocus").mockImplementation(() => hasFocus);
    vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
    render(<Probe client={client} seen={seen} />);
    await waitFor(() => expect(client.liveListenerCount).toBe(1));

    hasFocus = false;
    act(() => {
      window.dispatchEvent(new Event("blur"));
    });
    await waitFor(() => expect(client.liveListenerCount).toBe(0));
    expect(seen.at(-1)).toBeNull();

    hasFocus = true;
    act(() => {
      window.dispatchEvent(new Event("focus"));
    });
    await waitFor(() => expect(client.liveListenerCount).toBe(1));

    visibility = "hidden";
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await waitFor(() => expect(client.liveListenerCount).toBe(0));

    visibility = "visible";
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await waitFor(() => expect(client.liveListenerCount).toBe(1));
  });

  it("turns a burst of reports into one update", async () => {
    const client = new MockQcmClient();
    const seen: (LiveSnapshot | null)[] = [];
    vi.spyOn(document, "hasFocus").mockReturnValue(true);
    render(<Probe client={client} seen={seen} />);
    await waitFor(() => expect(client.liveListenerCount).toBe(1));
    act(() => {
      for (let seq = 1; seq <= 20; seq += 1) client.emitLive(frame(seq));
    });
    await waitFor(() => expect(seen.filter((next) => next !== null).length).toBeGreaterThan(0));
    const frames = seen.filter((next): next is LiveSnapshot => next !== null);
    expect(frames).toHaveLength(1);
    expect(frames[0]?.seq).toBe(20);
  });
});
