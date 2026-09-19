import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient, type LiveSnapshot } from "../../platform";
import { QuadStickVisualizer, type VisualizerBinding } from "./QuadStickVisualizer";
import type { ZoneId } from "./deviceSummary";

const rows: readonly VisualizerBinding[] = [
  { row: 4, cells: ["cross", "normal", "mp_left_sip", "", "", "", "", "", "", ""] },
  { row: 5, cells: ["left_joy_left", "normal", "left", "", "", "", "", "", "", ""] },
];

function Harness({
  client,
  model = "fps",
}: {
  readonly client: MockQcmClient;
  readonly model?: "fps" | "original" | "singleton";
}) {
  const [selected, setSelected] = useState<number | null>(null);
  const [zone, setZone] = useState<ZoneId | null>(null);
  return (
    <I18nProvider initialPreference="en">
      <QuadStickVisualizer
        client={client}
        rows={rows}
        selectedRow={selected}
        selectedZone={zone}
        modeName="Racing"
        modeNumber={1}
        model={model}
        view="device"
        onSelectRow={setSelected}
        onSelectZone={setZone}
      />
      <output data-testid="selected">{selected}</output>
      <output data-testid="zone">{zone}</output>
    </I18nProvider>
  );
}

describe("TASK-039 QuadStick visualizer", () => {
  it("selects the left mouthpiece through its Sip gesture callout", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    fireEvent.click(screen.getByRole("button", { name: /Left.*Sip/u }));
    expect(screen.getByTestId("selected")).toHaveTextContent("4");
  });

  it("shows Soft Puff, Puff, Sip, Soft Sip on the left callout", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    const left = screen.getByRole("button", { name: /Left\. Soft Puff/u });
    expect(left).toHaveTextContent("Soft Puff");
    expect(left).toHaveTextContent("Puff");
    expect(left).toHaveTextContent("Sip");
    expect(left).toHaveTextContent("Soft Sip");
    expect(left.querySelector('img.output-prompt[title="Cross"]')).not.toBeNull();
  });

  it("starts live on mount and clears the active joystick on stale", async () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
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
    const joystick = screen.getByRole("button", { name: /^Joystick\./u });
    joystick.focus();
    fireEvent.keyDown(joystick, { key: "ArrowRight" });
    expect(document.activeElement).toHaveTextContent(/Left/u);
  });

  it("opens combo stage when Combos is pressed", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    fireEvent.click(screen.getByRole("button", { name: /^Combos$/u }));
    expect(screen.getByRole("button", { name: /Left \+ Center/u })).toBeTruthy();
  });

  it("shows the back panel for switch jacks", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    fireEvent.click(screen.getByRole("button", { name: /Switch jacks/i }));
    expect(screen.getByText("One switch: in 8")).toBeTruthy();
    expect(screen.getByRole("button", { name: /Main controls/i })).toBeTruthy();
  });

  it("lights mode 1 as the leftmost purple LED", () => {
    const client = new MockQcmClient();
    render(<Harness client={client} />);
    expect(document.querySelectorAll(".mode-light").length).toBeGreaterThan(0);
    expect(screen.getByText(/Device shows light 1 purple/u)).toBeTruthy();
  });
});
