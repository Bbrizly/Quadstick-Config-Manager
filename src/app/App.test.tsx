import { fireEvent, render, screen } from "@testing-library/react";
import axe from "axe-core";
import { afterEach, describe, expect, it } from "vitest";

import appCss from "../styles/app.css?raw";
import tokenCss from "../styles/tokens.css?raw";
import { App } from "./App";

afterEach(() => {
  delete document.documentElement.dataset.theme;
});

describe("TASK-036 app shell", () => {
  it("renders stable landmarks and shell navigation", () => {
    render(<App />);

    expect(screen.getByRole("main")).toHaveAttribute("id", "qcm-main");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "QuadStick Config Manager",
    );
    expect(screen.getByRole("link", { name: "Skip to main content" })).toHaveAttribute(
      "href",
      "#qcm-main",
    );

    const home = screen.getByRole("button", { name: "Home" });
    const device = screen.getByRole("button", { name: "Manage files on your QuadStick" });
    expect(home).toHaveAttribute("aria-current", "page");

    fireEvent.click(device);
    expect(device).toHaveAttribute("aria-current", "page");
    expect(home).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Your QuadStick");
  });

  it("applies explicit themes and returns cleanly to system preference", () => {
    render(<App />);
    const appearance = screen.getByRole("combobox", { name: "Appearance" });

    fireEvent.change(appearance, { target: { value: "dark" } });
    expect(document.documentElement.dataset.theme).toBe("dark");

    fireEvent.change(appearance, { target: { value: "light" } });
    expect(document.documentElement.dataset.theme).toBe("light");

    fireEvent.change(appearance, { target: { value: "system" } });
    expect(document.documentElement).not.toHaveAttribute("data-theme");
  });

  it("traps modal focus, closes on Escape and restores the invoking control", () => {
    render(<App />);
    const settings = screen.getByRole("button", { name: "Open settings" });
    settings.focus();

    fireEvent.click(settings);
    const dialog = screen.getByRole("dialog", { name: "Settings" });
    const done = screen.getByRole("button", { name: "Done" });
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(done).toHaveFocus();

    fireEvent.keyDown(document, { key: "Tab" });
    expect(done).toHaveFocus();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(settings).toHaveFocus();
  });

  it("keeps minimum targets, reduced motion and forced colors in the substrate", () => {
    expect(tokenCss).toContain("--qcm-control-height: 48px");
    expect(tokenCss).toContain("--qcm-shell-nav-button: 64px");
    expect(tokenCss).toContain("@media (forced-colors: active)");
    expect(tokenCss).toContain("--qcm-focus: Highlight");
    expect(appCss).toContain("@media (prefers-reduced-motion: reduce)");
    expect(appCss).toContain("@media (forced-colors: active)");
    expect(appCss).toContain(".shell-nav-button[aria-current=\"page\"]::after");
  });

  it("has no automated accessibility violations in the shell", async () => {
    const { container } = render(<App />);
    const result = await axe.run(container, {
      rules: {
        // jsdom has no layout/paint engine; the palette's real contrast pairs
        // remain gated by the existing C# contrast suite.
        "color-contrast": { enabled: false },
      },
    });
    expect(result.violations).toEqual([]);
  });

  it("has no automated accessibility violations with the modal open", async () => {
    const { container } = render(<App />);
    fireEvent.click(screen.getByRole("button", { name: "Open settings" }));

    const result = await axe.run(container, {
      rules: { "color-contrast": { enabled: false } },
    });
    expect(result.violations).toEqual([]);
  });
});
