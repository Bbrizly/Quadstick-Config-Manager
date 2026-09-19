import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import axe from "axe-core";
import { afterEach, describe, expect, it } from "vitest";

import arCatalog from "../i18n/catalogs/ar.json";
import appCss from "../styles/app.css?raw";
import tokenCss from "../styles/tokens.css?raw";
import { App } from "./App";

afterEach(() => {
  delete document.documentElement.dataset["theme"];
  delete document.documentElement.dataset["locale"];
  delete document.documentElement.dataset["reduceMotion"];
  document.documentElement.style.removeProperty("zoom");
  document.documentElement.removeAttribute("lang");
  document.documentElement.removeAttribute("dir");
});

describe("TASK-036/037 app shell", () => {
  it("renders stable landmarks and localized shell navigation", () => {
    render(<App />);
    expect(screen.getByRole("main")).toHaveAttribute("id", "qcm-main");
    expect(screen.getByRole("heading", { level: 2, name: "Start a profile" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Skip to main content" })).toHaveAttribute("href", "#qcm-main");

    const home = screen.getByRole("button", { name: "Home" });
    const device = screen.getByRole("button", { name: "Manage files on your QuadStick" });
    expect(home).toHaveAttribute("aria-current", "page");
    fireEvent.click(device);
    expect(device).toHaveAttribute("aria-current", "page");
    expect(home).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("On your QuadStick");
  });

  it("applies explicit themes and returns cleanly to system preference", () => {
    render(<App />);
    const appearance = screen.getByRole("combobox", { name: "Appearance" });
    fireEvent.change(appearance, { target: { value: "dark" } });
    expect(document.documentElement.dataset["theme"]).toBe("dark");
    fireEvent.change(appearance, { target: { value: "light" } });
    expect(document.documentElement.dataset["theme"]).toBe("light");
    fireEvent.change(appearance, { target: { value: "system" } });
    expect(document.documentElement).not.toHaveAttribute("data-theme");
  });

  it("switches the document and shell to Arabic RTL", async () => {
    render(<App />);
    fireEvent.click(screen.getByRole("button", { name: "Open Settings" }));
    const language = await waitFor(() => screen.getByRole("combobox", { name: "Language" }));
    fireEvent.change(language, { target: { value: "ar" } });
    expect(document.documentElement).toHaveAttribute("lang", "ar");
    expect(document.documentElement).toHaveAttribute("dir", "rtl");
    expect(screen.getByRole("button", { name: arCatalog.Shell_Home })).toBeInTheDocument();
  });

  it("opens settings as an in-shell page and closes with Done", async () => {
    render(<App />);
    const settings = screen.getByRole("button", { name: "Open Settings" });
    fireEvent.click(settings);
    expect(settings).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => screen.getByRole("combobox", { name: "Language" }));
    expect(screen.getByRole("heading", { level: 1, name: "Settings" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => {
      expect(screen.queryByRole("heading", { level: 1, name: "Settings" })).not.toBeInTheDocument();
    });
    expect(screen.getByRole("heading", { level: 2, name: "Start a profile" })).toBeInTheDocument();
  });

  it("keeps minimum targets, reduced motion and forced colors in the substrate", () => {
    expect(tokenCss).toContain("--qcm-control-height: 48px");
    expect(tokenCss).toContain("--qcm-shell-nav-button: 64px");
    expect(tokenCss).toContain("--qcm-live-tint:");
    expect(tokenCss).toContain("@media (forced-colors: active)");
    expect(tokenCss).toContain("--qcm-focus: Highlight");
    expect(appCss).toContain("@media (prefers-reduced-motion: reduce)");
    expect(appCss).toContain("@media (forced-colors: active)");
    expect(appCss).toContain(".shell-nav-button.active");
  });

  it("has no automated accessibility violations in shell and settings", async () => {
    const { container } = render(<App />);
    let result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations).toEqual([]);
    fireEvent.click(screen.getByRole("button", { name: "Open Settings" }));
    await waitFor(() => screen.getByRole("combobox", { name: "Language" }));
    result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations).toEqual([]);
  });
});
