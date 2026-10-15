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
