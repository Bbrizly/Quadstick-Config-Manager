import { afterEach, describe, expect, it } from "vitest";
import { describeTarget, IdleWatch, judge, settle, type AnimationLike } from "./idle";

/** An animation that finishes after `ms`, or never. */
function fake(name: string, ms: number | null, startTime = 0): AnimationLike & { done: boolean } {
  const target = document.createElement("button");
  target.className = "homeaction";
  target.setAttribute("aria-label", name);
  const state = { done: false };
  const finished = ms === null
    ? new Promise<void>(() => undefined)
    : new Promise<void>((resolve) => setTimeout(() => {
      state.done = true;
      resolve();
    }, ms));
  return {
    get done() {
      return state.done;
    },
    animationName: name,
    playState: "running",
    startTime,
    finished,
    effect: { target, getTiming: () => ({ iterations: ms === null ? Infinity : 1 }) },
  };
}

describe("idle check", () => {
  let watch: IdleWatch | null = null;
  afterEach(() => {
    watch?.stop();
    watch = null;
  });

  it("waits for an entrance to finish instead of sampling it mid-flight", async () => {
    const entrance = fake("qcm-fade-rise", 30);
    let list: AnimationLike[] = [entrance];
    const stuck = await settle(() => (list = list.filter((a) => !(a as typeof entrance).done)), 1000);
    expect(stuck).toEqual([]);
    expect(entrance.done).toBe(true);
  });

  it("reports an endless animation by name, target and start", async () => {
    const stuck = await settle(() => [fake("spin", null, 1234)], 50);
    expect(stuck).toHaveLength(1);
    expect(stuck[0]).toMatchObject({ name: "spin", startedAt: 1234, endless: true });
    expect(stuck[0]?.target).toContain("button.homeaction");
  });

  it("fails a transition that starts at rest, naming property, target and time", () => {
    watch = new IdleWatch(document);
    watch.start();
    const card = document.createElement("button");
    card.className = "card";
    document.body.append(card);
    card.dispatchEvent(Object.assign(new Event("transitionrun", { bubbles: true }), { propertyName: "border-color" }));
    const result = judge({ raf: 0, started: watch.stop(), stuck: [] });
    expect(result.pass).toBe(false);
    expect(result.busy[0]).toMatch(/^transition border-color on button\.card at \d+ms$/u);
  });

  it("does not blame the app for a hover a real pointer caused", () => {
    watch = new IdleWatch(document);
    watch.start();
    const card = document.createElement("button");
    document.body.append(card);
    // A person moving the mouse: a trusted event the walk cannot fake.
    watch.notePointer(performance.now());
    card.dispatchEvent(Object.assign(new Event("transitionrun", { bubbles: true }), { propertyName: "background-color" }));
    const started = watch.stop();
    expect(started[0]?.byPointer).toBe(true);
    const result = judge({ raf: 0, started, stuck: [] });
    expect(result.pass).toBe(true);
    expect(result.excused[0]).toMatch(/real pointer/u);
  });

  it("names elements the way a person finds them", () => {
    const element = document.createElement("button");
    element.id = "go";
    element.className = "primary big";
    element.setAttribute("aria-label", "Go home");
    expect(describeTarget(element)).toBe('button#go.primary.big "Go home"');
    expect(describeTarget(null)).toBe("(no element)");
  });
});

describe("idle sampling", () => {
  it("catches an element.animate() animation that fires no event", () => {
    const watch = new IdleWatch(document);
    const old = fake("old", 10);
    watch.start([old]);
    const { animationName: _css, ...rest } = fake("x", 10);
    const scripted: AnimationLike = { ...rest, id: "glow" };
    watch.sample([old, scripted]);
    watch.sample([old, scripted]);
    const started = watch.stop();
    expect(started).toHaveLength(1);
    expect(started[0]).toMatchObject({ name: "glow", byPointer: false });
  });
});
