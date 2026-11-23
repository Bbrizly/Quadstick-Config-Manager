import { describe, expect, it } from "vitest";

import motionCss from "../styles/motion.css?raw";
import { applyDocumentLocale } from "./index";

// A language switch started every control's hover transitions; WebKit left
// them unstarted in a covered window, and the idle check saw a busy app.
describe("language switch", () => {
  it("turns transitions off while the new language lands", async () => {
    const root = document.createElement("html");
    applyDocumentLocale("en", root);
    expect(root.dataset["settling"]).toBeUndefined();
    applyDocumentLocale("ar", root);
    expect(root.dataset["settling"]).toBe("true");
    await new Promise((done) => setTimeout(done, 100));
    expect(root.dataset["settling"]).toBeUndefined();
  });

  it("has the rule that does it", () => {
    expect(motionCss.length).toBeGreaterThan(1000);
    expect(motionCss).toMatch(/:root\[data-settling="true"\] \*[\s\S]*?transition:\s*none !important/u);
  });
});
