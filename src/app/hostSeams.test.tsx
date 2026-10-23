import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { MockQcmClient, type EditorSnapshot } from "../platform";
import { EditorHost } from "../host";
import { profileLabels } from "../host/labels";
import { TauriQcmClient } from "../host/tauri";

async function mount(locale?: "fr") {
  const client = new MockQcmClient();
  const snapshot: EditorSnapshot = await client.newProfile("untitled.csv");
  render(<EditorHost client={client} snapshot={snapshot} locale={locale} />);
  await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
}

afterEach(() => {
  document.documentElement.lang = "";
  document.documentElement.dir = "";
});

describe("the host's language", () => {
  it("follows the locale it is given and leaves the page's lang and dir alone", async () => {
    document.documentElement.lang = "ar";
    document.documentElement.dir = "rtl";
    await mount("fr");
    expect(await screen.findByRole("button", { name: /^Installer ce profil sur un QuadStick/u })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe("ar");
    expect(document.documentElement.dir).toBe("rtl");
  });

  it("without one, picks its own and sets the page's as the app does", async () => {
    document.documentElement.lang = "ar";
    document.documentElement.dir = "rtl";
    await mount();
    expect(screen.getByRole("button", { name: /^Install this profile onto a/u })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe("en");
    expect(document.documentElement.dir).toBe("ltr");
  });
});

describe("profileLabels", () => {
  it("names tokens as the editor's grid does", async () => {
    const en = await profileLabels("en");
    expect(en.input("mp_left_sip")).toBe("Left · sip");
    expect(en.input("mp_center_soft_puff")).toBe("Center · soft puff");
    expect(en.input("lip")).toBe("Lip switch · lip");
    expect(en.input("mp_left_center_sip")).toBe("Left + Center · sip");
    expect(en.output("mouse_left")).toBe("Mouse left");
    expect(en.output("left_1")).toBe("Left 1");
    expect(en.output("ps3")).toBe("PS");
  });

  it("speaks the locale it was asked for", async () => {
    const fr = await profileLabels("fr");
    expect(fr.input("mp_left_sip")).toBe("Gauche · sip");
  });
});

describe("host/tauri", () => {
  it("hands a host the native client", () => {
    expect(typeof TauriQcmClient).toBe("function");
  });
});
