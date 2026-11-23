import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { seedDevicePreview } from "../platform/mockPreviews";
import { App } from "./App";

// Screen-reader users jump by heading. axe in Chrome flagged Home with no
// level-one heading and the device page skipping from h1 to h3.
function levels(): number[] {
  return [...document.querySelectorAll("h1, h2, h3, h4, h5, h6")].map((h) => Number(h.tagName[1]));
}

describe("heading outline", () => {
  it("gives Home one level-one heading", () => {
    render(<App client={new MockQcmClient()} />);
    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
  });

  // B-115: a "?" that only repeats its heading tells a heading-jumper nothing.
  it("names the Home files \"?\" by its question", () => {
    render(<App client={new MockQcmClient()} />);
    expect(
      screen.getByRole("button", { name: "How do you pick one of these files on the QuadStick?" }),
    ).toBeInTheDocument();
  });

  it("never skips a level on the device page", async () => {
    const client = new MockQcmClient();
    seedDevicePreview(client);
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    await screen.findByRole("heading", { level: 1, name: "Your QuadStick's settings" });
    await screen.findByRole("heading", { name: /Games on a Mac/u });
    const seen = levels();
    for (let i = 1; i < seen.length; i += 1) {
      expect(seen[i]! - seen[i - 1]!).toBeLessThanOrEqual(1);
    }
  });
});
