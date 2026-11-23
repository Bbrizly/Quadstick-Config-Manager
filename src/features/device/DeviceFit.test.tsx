import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import type { EditorSnapshot, PreferenceDefinition, QcmClient } from "../../platform";
import { MockQcmClient } from "../../platform/mockQcmClient";
import surfacesCss from "../../styles/surfaces.css?raw";
import { DevicePreferencesPage } from "./DevicePreferencesPage";
import { barFits, deviceFit, openingScroll, railFits, ROW_WIDTH, WIDE_WIDTH, windowIsShort } from "./deviceFit";

// B-124..B-127. Interface size is root zoom, which media queries never see:
// at 1280x860 and 200% the page showed no setting (list 0-14px tall), and at
// 150-200% it scrolled up to 309px sideways. jsdom does no layout, so the
// boxes below are the numbers WebKit gave.

const props = ["offsetWidth", "offsetHeight", "clientHeight"] as const;
const saved = props.map((name) => [name, Object.getOwnPropertyDescriptor(HTMLElement.prototype, name)] as const);
const savedInner = window.innerHeight;

afterEach(() => {
  for (const [name, descriptor] of saved) {
    if (descriptor !== undefined) Object.defineProperty(HTMLElement.prototype, name, descriptor);
  }
  window.innerHeight = savedInner;
  vi.restoreAllMocks();
});

function define(name: (typeof props)[number], get: (element: HTMLElement) => number): void {
  Object.defineProperty(HTMLElement.prototype, name, {
    configurable: true,
    get(this: HTMLElement) {
      return get(this);
    },
  });
}

interface Box {
  /** The page's CSS width and the window, in device pixels, at this zoom. */
  readonly width: number;
  readonly innerHeight: number;
  readonly zoom: number;
  /** The scroller's CSS height, the save bar's and the rail's. */
  readonly view: number;
  readonly bar: number;
  readonly rail: number;
  /** CSS tops from the scroller's top: layout, list, and the first row's bottom. */
  readonly layoutTop?: number;
  readonly listTop?: number;
  readonly rowBottom?: number;
}

function layOut(box: Box): void {
  window.innerHeight = box.innerHeight;
  define("offsetWidth", (element) => (element.classList.contains("device-preferences-page") ? box.width : 0));
  define("offsetHeight", (element) =>
    element.classList.contains("device-save-bar") ? box.bar : element.classList.contains("device-preference-categories") ? box.rail : 0,
  );
  define("clientHeight", (element) => (element.classList.contains("shell-main") ? box.view : 0));
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const z = box.zoom;
    const at = (top: number, height = 0) => new DOMRect(0, (87 + top) * z, 0, height * z);
    if (this.classList.contains("device-preferences-page")) return new DOMRect(0, 87 * z, box.width * z, 0);
    if (this.classList.contains("shell-main")) return at(0);
    if (this.classList.contains("device-preference-layout") || this.classList.contains("device-preference-main")) return at(box.layoutTop ?? 0);
    if (this.classList.contains("device-preference-list")) return at(box.listTop ?? 0);
    if (this.parentElement?.classList.contains("device-preference-list") === true && this.previousElementSibling === null) {
      return at(box.listTop ?? 0, (box.rowBottom ?? 0) - (box.listTop ?? 0));
    }
    return new DOMRect(0, 0, 0, 0);
  });
}

const SPEED: PreferenceDefinition = {
  name: "mouse_speed", label: "Mouse speed", category: "Mouse", editor: "integer", default: "100",
  minimum: 0, maximum: 200, unit: "", description: "", options: [], modeOverride: false, risk: "",
  source: "", firmwareMayAddMore: false, alsoCalled: "", advanced: false,
};

const PREFS = "Preferences\nprefs.csv\nPreference,Value,Units,Description\nmouse_speed,100,,\n";

/** With a stick, the page opens on prefs.csv read off it; without, on the catalog alone. */
async function renderPage(stick = false) {
  const client = new MockQcmClient();
  const writable: { -readonly [K in keyof QcmClient]: QcmClient[K] } = client;
  writable.getPreferenceCatalog = () => Promise.resolve({ categories: ["Mouse"], definitions: [SPEED] });
  let snapshot: EditorSnapshot | null = null;
  if (stick) {
    client.plugDevice("QUADSTICK", { "prefs.csv": PREFS });
    const device = (await client.refreshDevices()).devices[0]!;
    snapshot = await client.openDevicePreferences(device.deviceId, device.generation);
  }
  const view = render(
    <I18nProvider initialPreference="en">
      <main className="shell-main">
        <DevicePreferencesPage
          client={client}
          snapshot={snapshot}
          onSnapshot={() => undefined}
          onReload={() => undefined}
          onManageFiles={() => undefined}
        />
      </main>
    </I18nProvider>,
  );
  await screen.findByRole("navigation", { name: "Groups of settings" });
  const page = view.container.querySelector<HTMLElement>(".device-preferences-page")!;
  const scroller = view.container.querySelector<HTMLElement>(".shell-main")!;
  return { view, page, scroller };
}

function resize(): void {
  act(() => {
    window.dispatchEvent(new Event("resize"));
  });
}

describe("deviceFit (B-124, B-125)", () => {
  it("cuts on the page's CSS width, which zoom divides", () => {
    expect(ROW_WIDTH).toBe(598);
    expect(WIDE_WIDTH).toBe(824);
    // 1280x860 and 1024x700 at 100%, and 900x620: Avalonia's two columns.
    for (const width of [1224, 968, 844]) expect(deviceFit(width)).toBe("wide");
    // 1280x860@150 and 1024x700@150: the groups go above, rows stay side by side.
    for (const width of [797, 627]) expect(deviceFit(width)).toBe("single");
    // 1280x860@200, 900x620@150, 1024x700@200, 900x620@200: rows stack too.
    for (const width of [584, 544, 456, 394]) expect(deviceFit(width)).toBe("tight");
    expect(deviceFit(0)).toBe("wide");
  });

  it("calls the window short on its CSS height, as the 780px media query did at 100%", () => {
    expect(windowIsShort(700, 1)).toBe(true);
    expect(windowIsShort(860, 1)).toBe(false);
    // 1280x860 at 150% and 200%: the media query saw 860 and kept the
    // window-tall page, so the settings list was 0-14px tall.
    expect(windowIsShort(860, 1.5)).toBe(true);
    expect(windowIsShort(860, 2)).toBe(true);
    expect(windowIsShort(0, 1)).toBe(false);
  });
});

describe("what stays pinned (B-126, B-127)", () => {
  it("pins the bar only while it takes a third of the view or less", () => {
    // 1024x700@100: 70 of 613. 900x620@200: two lines, 97 of 223.
    expect(barFits(70, 613)).toBe(true);
    expect(barFits(97, 223)).toBe(false);
    expect(barFits(70, 0)).toBe(false);
  });

  it("pins the rail only if all of it shows between its top and the bar", () => {
    // 1024x700@100 and 900x620@100: 446 of 613 and 533 less a 70 bar.
    expect(railFits(446, 613, 70)).toBe(true);
    expect(railFits(446, 533, 70)).toBe(true);
    // 900x620@200 two columns would be 446 in 223: Bluetooth and on off screen.
    expect(railFits(446, 223, 0)).toBe(false);
  });
});

describe("where the page opens (B-124)", () => {
  it("stays at the top when the first row already clears the bar", () => {
    expect(openingScroll(613, 70, 465, 540, [279, 279])).toBeNull();
  });

  it("nudges just enough when the first row runs under the bar", () => {
    // 1024x700@100: the list starts 465 down a 613 view with a 70 bar, and
    // its first row ended 45 under the bar's top.
    expect(openingScroll(613, 70, 465, 588, [279, 279])).toBe(45);
  });

  it("scrolls to the first stop that shows the whole first row", () => {
    // 900x620@100: the list began at 470 of 463 open.
    expect(openingScroll(533, 70, 470, 594, [284, 284])).toBe(284);
    // 900x620@200, one column: only the list's own top shows a row.
    expect(openingScroll(223, 0, 1074, 1230, [571, 825])).toBe(1074);
  });
});

describe("the page applies its fit", () => {
  it("sets the fit from its CSS width and follows a resize", async () => {
    layOut({ width: 394, innerHeight: 620, zoom: 2, view: 223, bar: 97, rail: 254 });
    const { page } = await renderPage();
    expect(page.dataset["fit"]).toBe("tight");
    expect(page.dataset["short"]).toBe("true");
    expect(page.dataset["pinBar"]).toBeUndefined();
    expect(page.dataset["pinRail"]).toBeUndefined();

    layOut({ width: 1224, innerHeight: 860, zoom: 1, view: 773, bar: 70, rail: 386 });
    resize();
    expect(page.dataset["fit"]).toBe("wide");
    expect(page.dataset["short"]).toBeUndefined();
  });

  it("publishes the pinned bar's height on the scroller and takes it back", async () => {
    layOut({ width: 968, innerHeight: 700, zoom: 1, view: 613, bar: 70, rail: 446, layoutTop: 279, listTop: 465, rowBottom: 588 });
    const { view, page, scroller } = await renderPage(true);
    expect(page.dataset["fit"]).toBe("wide");
    expect(page.dataset["pinBar"]).toBe("true");
    expect(page.dataset["pinRail"]).toBe("true");
    expect(scroller.style.getPropertyValue("--qcm-save-h")).toBe("70px");
    // 1024x700@100 keeps its layout; it opens just far enough down that the
    // first setting clears the pinned bar.
    expect(scroller.scrollTop).toBe(45);

    layOut({ width: 968, innerHeight: 700, zoom: 1, view: 180, bar: 70, rail: 446 });
    resize();
    expect(page.dataset["pinBar"]).toBeUndefined();
    expect(page.dataset["pinRail"]).toBeUndefined();
    expect(scroller.style.getPropertyValue("--qcm-save-h")).toBe("");

    layOut({ width: 968, innerHeight: 700, zoom: 1, view: 613, bar: 70, rail: 446 });
    resize();
    expect(scroller.style.getPropertyValue("--qcm-save-h")).toBe("70px");
    view.unmount();
    expect(scroller.style.getPropertyValue("--qcm-save-h")).toBe("");
  });

  it("opens with the first setting in view when the header fills the window", async () => {
    layOut({ width: 844, innerHeight: 620, zoom: 1, view: 533, bar: 70, rail: 446, layoutTop: 284, listTop: 470, rowBottom: 594 });
    const { scroller } = await renderPage(true);
    expect(scroller.scrollTop).toBe(284);
    expect(document.activeElement).toBe(document.body);
  });

  it("with no stick stays on the header, where the warn line and drive button are", async () => {
    layOut({ width: 844, innerHeight: 620, zoom: 1, view: 533, bar: 70, rail: 446, layoutTop: 284, listTop: 470, rowBottom: 594 });
    const { scroller } = await renderPage();
    expect(scroller.scrollTop).toBe(0);
  });

  it("leaves a window-tall page where it is", async () => {
    layOut({ width: 1224, innerHeight: 860, zoom: 1, view: 773, bar: 70, rail: 386, layoutTop: 260, listTop: 446, rowBottom: 569 });
    const { page, scroller } = await renderPage(true);
    expect(page.dataset["short"]).toBeUndefined();
    expect(page.dataset["pinBar"]).toBeUndefined();
    expect(scroller.scrollTop).toBe(0);
  });
});

describe("surfaces.css device page (B-124..B-127)", () => {
  const css = surfacesCss.replace(/\/\*[\s\S]*?\*\//gu, "");

  function body(selector: string): string {
    const escaped = selector.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
    return new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`, "u").exec(css)?.[1] ?? "";
  }

  it("reads the stylesheet", () => {
    expect(surfacesCss.length).toBeGreaterThan(1000);
  });

  it("has no size media query left, since zoom hides from them", () => {
    expect(css).not.toMatch(/@media[^{]*\b(?:max|min)-(?:width|height)/u);
  });

  it("goes one column on the measured fit", () => {
    expect(body('.device-preferences-page:not([data-fit="wide"]) .device-preference-layout')).toMatch(
      /grid-template-columns:\s*minmax\(0,\s*1fr\)/u,
    );
    expect(body('.device-preferences-page[data-fit="tight"] .device-preference-row')).toMatch(/grid-template-columns:\s*minmax\(0,\s*1fr\)/u);
    expect(body('.device-preferences-page:not([data-fit="wide"]) .device-page-actions')).toMatch(/flex-wrap:\s*wrap/u);
  });

  it("lets the tight slider narrow but never shorten", () => {
    const slider = body('.device-preferences-page[data-fit="tight"] .preference-number-control input[type="range"]');
    expect(slider).toMatch(/min-width:\s*0/u);
    expect(slider).toMatch(/max-width:\s*285px/u);
    expect(slider).not.toMatch(/(?:^|[\s;])height:/u);
  });

  it("scrolls the page as one when short or in one column", () => {
    expect(css).toMatch(
      /\.device-preferences-page\[data-short\],\s*\.device-preferences-page:not\(\[data-fit="wide"\]\)\s*\{\s*height:\s*auto;/u,
    );
  });

  it("pins the rail and the bar only under their measured flags", () => {
    const sticky = [...css.matchAll(/([^{}]+)\{[^}]*position:\s*sticky/gu)].map((match) => match[1]!.trim());
    const device = sticky.filter((selector) => selector.includes("device-"));
    expect(device).toEqual([
      ".device-preferences-page[data-pin-rail] .device-preference-categories",
      ".device-preferences-page[data-pin-bar] .device-save-bar",
    ]);
  });

  it("breaks a word too long for its column instead of scrolling sideways", () => {
    // The pseudo-locale's 40-letter runs pushed the band's words 27px past the
    // page at 1024x700@150, and the header 25px past the view at 900x620@200.
    expect(body(".device-preferences-page")).toMatch(/overflow-wrap:\s*break-word/u);
    expect(body(".device-band-words")).toMatch(/overflow-wrap:\s*anywhere/u);
  });

  it("keeps a focused control clear of the pinned bar", () => {
    expect(body(".shell-main:has(.device-preferences-page[data-pin-bar])")).toMatch(
      /scroll-padding-block-end:\s*calc\(var\(--qcm-save-h,\s*0px\)\s*\+\s*12px\)/u,
    );
  });
});
