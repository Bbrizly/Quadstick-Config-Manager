import { describe, expect, it } from "vitest";

import ar from "./catalogs/ar.json";
import baselineKeys from "./catalogs/baseline-keys.json";
import de from "./catalogs/de.json";
import en from "./catalogs/en.json";
import es from "./catalogs/es.json";
import fr from "./catalogs/fr.json";
import hi from "./catalogs/hi.json";
import itCatalog from "./catalogs/it.json";
import ja from "./catalogs/ja.json";
import ko from "./catalogs/ko.json";
import nl from "./catalogs/nl.json";
import pl from "./catalogs/pl.json";
import pt from "./catalogs/pt.json";
import pseudo from "./catalogs/qps-ploc.json";
import rewriteKeys from "./catalogs/rewrite-keys.json";
import zhHans from "./catalogs/zh-Hans.json";
import { applyDocumentLocale, formatMessage, localeDirection, pluralCategory, resolveLocale } from "./index";
import { LOCALIZED_ERROR_CODES, localizedErrorMessage } from "./errors";

const catalogs = [en, ar, de, es, fr, hi, itCatalog, ja, ko, nl, pl, pt, zhHans, pseudo];

describe("TASK-037 localization migration", () => {
  it("gives every runtime catalog exactly the generated keyset", () => {
    const expected = Object.keys(en).sort();
    for (const catalog of catalogs) expect(Object.keys(catalog).sort()).toEqual(expected);
    expect(baselineKeys.length).toBeGreaterThan(500);
    expect(rewriteKeys).toContain("Rewrite_SkipToMainContent");
  });

  it("preserves positional placeholders and the legacy one/other rule", () => {
    expect(formatMessage("{0} / {1}", ["A", "B"])).toBe("A / B");
    expect(pluralCategory("fr", 0)).toBe("one");
    expect(pluralCategory("fr", 2)).toBe("other");
    expect(pluralCategory("ar", 1)).toBe("one");
    expect(pluralCategory("ar", 2)).toBe("other");
  });

  it("resolves supported system locales and treats Arabic as RTL", () => {
    expect(resolveLocale("system", ["fr-CA", "en-CA"])).toBe("fr");
    expect(resolveLocale("system", ["zh-CN"])).toBe("zh-Hans");
    expect(resolveLocale("system", ["xx-ZZ"])).toBe("en");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("de")).toBe("ltr");
  });

  it("sets language and direction without mirroring data itself", () => {
    const root = document.createElement("html");
    applyDocumentLocale("ar", root);
    expect(root.lang).toBe("ar");
    expect(root.dir).toBe("rtl");
    expect(root.dataset["locale"]).toBe("ar");
  });

  it("keeps pseudo placeholders intact while expanding readable text", () => {
    const key = "Community_NoProfilesMatchQuery";
    expect(pseudo[key]).toContain("{0}");
    expect(pseudo[key].length).toBeGreaterThan(en[key].length);
    expect(pseudo[key]).not.toBe(en[key]);
  });

  it("localizes every stable Rust error code by code, not fallback English", () => {
    expect(LOCALIZED_ERROR_CODES).toHaveLength(35);
    const translated = localizedErrorMessage(
      {
        code: "QCM_DEVICE_STALE",
        message: "That drive is not the one this window was showing.",
        recoverable: true,
        action: { kind: "refresh_devices" },
        operationId: null,
        targetState: null,
        backup: null,
      },
      (key) => ar[key],
    );
    expect(translated).toBe(ar.Rewrite_ErrorDevice);
    expect(translated).not.toContain("drive is not");
  });
});
