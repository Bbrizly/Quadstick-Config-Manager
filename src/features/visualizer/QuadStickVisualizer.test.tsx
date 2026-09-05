import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type LiveSnapshot } from "../../platform";
import { QuadStickVisualizer, type VisualizerBinding } from "./QuadStickVisualizer";

const rows: readonly VisualizerBinding[] = [
  { row: 4, cells: ["cross", "normal", "mp_left_sip", "", "", "", "", "", "", ""] },
  { row: 5, cells: ["left_joy_left", "normal", "left", "", "", "", "", "", "", ""] },
];

function Harness({ client }: { readonly client: MockQcmClient }) {
  const [selected, setSelected] = useState<number | null>(null);
  return (
    <I18nProvider initialPreference="en">
      <QuadStickVisualizer
        client={client}
        rows={rows}
        selectedRow={selected}
        modeName="Racing"
        modeNumber={1}
        onSelectRow={setSelected}
      />
      <output data-testid="selected">{selected}</output>
    </I18nProvider>
  );
}

describe("TASK-039 QuadStick visualizer", () => {
  it("selects the same canonical binding through the photo and semantic list", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    fireEvent.click(screen.getByRole("button", { name: /Left.*1 mapping/u }));
    expect(screen.getByTestId("selected")).toHaveTextContent("4");
    fireEvent.click(screen.getByTestId("binding-row-5"));
    expect(screen.getByTestId("selected")).toHaveTextContent("5");
  });

  it("owns one live subscription and clears the active joystick on stale", async () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    fireEvent.click(screen.getByRole("button", { name: /Joystick travel/u }));
    await waitFor(() => expect(client.liveListenerCount).toBe(1));

    const reading: LiveSnapshot = {
      seq: 1,
      atMillis: 1,
      status: {
        kind: "reading",
        product: "QuadStick",
        motion: { x: 0.8, y: 0, buttons: [] },
      },
    };
    client.emitLive(reading);
    await waitFor(() => expect(document.querySelector('[data-live-active="true"]')).not.toBeNull());

    client.emitLive({ seq: 2, atMillis: 2, status: { kind: "stale", product: "QuadStick" } });
    await waitFor(() => expect(document.querySelector('[data-live-active="true"]')).toBeNull());
  });

  it("supports arrow-key navigation between physical hotspots", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    const joystick = screen.getByRole("button", { name: /Joystick.*1 mapping/u });
    joystick.focus();
    fireEvent.keyDown(joystick, { key: "ArrowRight" });
    expect(document.activeElement).toHaveTextContent("Left");
  });
});
