import { act, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { usePresence } from "./presence";

/** jsdom has no Web Animations. A stand-in CSS animation whose `finished`
 *  the test settles by hand, or never, as a frozen timeline does. */
class FakeCSSAnimation {
  readonly finished: Promise<FakeCSSAnimation>;
  resolve!: () => void;
  reject!: (reason: unknown) => void;
  readonly animationName: string;
  readonly durationMs: number;
  constructor(animationName: string, durationMs = 120) {
    this.animationName = animationName;
    this.durationMs = durationMs;
    this.finished = new Promise((resolve, reject) => {
      this.resolve = () => resolve(this);
      this.reject = reject;
    });
  }
  readonly currentTime = 0;
  get effect() {
    return { getComputedTiming: () => ({ endTime: this.durationMs }) };
  }
}

let running: FakeCSSAnimation[] = [];
let visibility: DocumentVisibilityState = "visible";

beforeEach(() => {
  running = [];
  visibility = "visible";
  (globalThis as { CSSAnimation?: unknown }).CSSAnimation = FakeCSSAnimation;
  Element.prototype.getAnimations = function getAnimations(this: Element) {
    return this.classList.contains("closing") ? (running as unknown as Animation[]) : [];
  };
  vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  delete (Element.prototype as { getAnimations?: unknown }).getAnimations;
  delete (globalThis as { CSSAnimation?: unknown }).CSSAnimation;
  delete document.documentElement.dataset["reduceMotion"];
});

function Overlay({ open }: { readonly open: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  const { mounted, closing } = usePresence(open, ref);
  return mounted ? <div ref={ref} data-testid="overlay" className={closing ? "closing" : ""} /> : null;
}

const overlay = () => document.querySelector('[data-testid="overlay"]');

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}

describe("usePresence", () => {
  it("stays for its exit and leaves when it finishes", async () => {
    const exit = new FakeCSSAnimation("qcm-fade-out");
    running = [exit];
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    expect(overlay()?.classList.contains("closing")).toBe(true);
    exit.resolve();
    await flush();
    expect(overlay()).toBeNull();
  });

  it("leaves when its exit is cancelled (finished rejects with AbortError)", async () => {
    const exit = new FakeCSSAnimation("qcm-fade-out");
    running = [exit];
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    exit.reject(new DOMException("cancelled", "AbortError"));
    await flush();
    expect(overlay()).toBeNull();
  });

  // The tour that never closed: a timeline that does not advance (WebKit holds
  // a page it is not drawing) never settles `finished`, so the closed overlay
  // sat on the page, inert, for as long as that lasted.
  it("leaves once its exit should be over even if finished never settles", () => {
    vi.useFakeTimers();
    running = [new FakeCSSAnimation("qcm-fade-out", 120), new FakeCSSAnimation("qcm-panel-out", 140)];
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    expect(overlay()).not.toBeNull();
    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(overlay()).toBeNull();
  });

  it("goes at once when closed on a hidden page", () => {
    running = [new FakeCSSAnimation("qcm-fade-out")];
    visibility = "hidden";
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    expect(overlay()).toBeNull();
  });

  it("goes at once when the page hides mid-exit", () => {
    running = [new FakeCSSAnimation("qcm-fade-out")];
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    expect(overlay()).not.toBeNull();
    visibility = "hidden";
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expect(overlay()).toBeNull();
  });

  it("an earlier exit finishing does not drop an overlay opened again", async () => {
    const first = new FakeCSSAnimation("qcm-fade-out");
    running = [first];
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    rerender(<Overlay open />);
    first.resolve();
    await flush();
    expect(overlay()).not.toBeNull();
    expect(overlay()?.classList.contains("closing")).toBe(false);
  });

  it("goes at once with reduced motion on", () => {
    running = [new FakeCSSAnimation("qcm-fade-out")];
    document.documentElement.dataset["reduceMotion"] = "true";
    const { rerender } = render(<Overlay open />);
    rerender(<Overlay open={false} />);
    expect(overlay()).toBeNull();
  });
});
