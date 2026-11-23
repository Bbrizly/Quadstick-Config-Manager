import { fireEvent, render, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import type { Issue } from "../../platform";
import { ProblemsDock } from "./ProblemsDock";

// Counts the rows React builds and renders, so a pick that rebuilds every row
// of a big file shows up as a number, not as a slow machine.
const built = vi.hoisted(() => ({ options: 0, rows: 0 }));

function counting<T extends (...args: never[]) => unknown>(make: T): T {
  return ((...args: Parameters<T>) => {
    const props = args[1] as unknown as { role?: string; severity?: string } | null;
    if (props?.role === "option") built.options += 1;
    if (props?.severity !== undefined) built.rows += 1;
    return make(...args);
  }) as T;
}

vi.mock("react/jsx-dev-runtime", async (original) => {
  const runtime = await original<typeof import("react/jsx-dev-runtime")>();
  return { ...runtime, jsxDEV: counting(runtime.jsxDEV) };
});

vi.mock("react/jsx-runtime", async (original) => {
  const runtime = await original<typeof import("react/jsx-runtime")>();
  return { ...runtime, jsx: counting(runtime.jsx), jsxs: counting(runtime.jsxs) };
});

describe("problems list with a big file", () => {
  it("rebuilds only the two rows whose pick changed on an arrow press", () => {
    const issues = Array.from(
      { length: 400 },
      (_, index) => ({ severity: "warning", kind: "x", cell: `A${String(index + 4)}`, message: "m", fix: "f" }) as unknown as Issue,
    );
    const view = render(
      <I18nProvider initialPreference="en">
        <ProblemsDock
          issues={issues}
          status={{ kind: "info", text: "" }}
          onFocusIssue={() => undefined}
          onStatus={() => undefined}
        />
      </I18nProvider>,
    );
    fireEvent.click(view.container.querySelector(".problems-toggle")!);
    const options = within(view.getByRole("listbox")).getAllByRole("option");
    expect(options).toHaveLength(400);
    expect(built.options).toBeGreaterThanOrEqual(400);

    options[0]!.focus();
    built.options = 0;
    built.rows = 0;
    for (let press = 0; press < 5; press += 1) fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(options[5]).toHaveFocus();
    expect(built.options).toBeLessThanOrEqual(10);
    expect(built.rows).toBe(0);
  });
});
