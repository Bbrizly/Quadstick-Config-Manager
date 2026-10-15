import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import type { BridgeState, ControllerClient, ControllerOptions } from "../../platform";
import { ControllerRow } from "./ControllerRow";

const BASE: ControllerOptions = {
  kind: "bridge", bridgeOn: false, bridgeState: null, macOn: false, macSandboxed: false,
  macCommand: null, macUndoCommand: null,
};

function fake(options: Partial<ControllerOptions>, macTakes = true) {
  let push: ((state: BridgeState | null) => void) | null = null;
  const calls: string[] = [];
  const controller: ControllerClient = {
    getControllerOptions: () => Promise.resolve({ ...BASE, ...options }),
    setControllerBridge: (on) => {
      calls.push(`bridge:${on}`);
      push?.(on ? "waiting" : null);
      return Promise.resolve(on);
    },
    setMacGamepad: (on) => {
      calls.push(`mac:${on}`);
      return Promise.resolve({ on: on && macTakes, took: macTakes });
    },
    subscribeControllerBridge: (onState) => {
      push = onState;
      return Promise.resolve({ dispose: () => calls.push("disposed") });
    },
  };
  return { controller, calls, push: (state: BridgeState | null) => push?.(state) };
}

function show(controller: ControllerClient) {
  return render(
    <I18nProvider initialPreference="en">
      <ControllerRow controller={controller} />
    </I18nProvider>,
  );
}

describe("controller bridge and Mac gamepad rows", () => {
  it("shows nothing where neither can run", async () => {
    const { controller } = fake({ kind: "none" });
    const view = show(controller);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(view.container.innerHTML).toBe("");
  });

  it("switches the bridge and says each state in words", async () => {
    const { controller, calls, push } = fake({});
    const view = show(controller);
    const box = await screen.findByRole("checkbox", { name: "Xbox controller bridge" });
    fireEvent.click(box);
    expect(await screen.findByText("Waiting for a QuadStick in emulation mode 2.")).toBeInTheDocument();
    push("no_access");
    expect(await screen.findByText(/does not let the app read the QuadStick/u)).toHaveClass("feature-error");
    push("bridging");
    expect(await screen.findByText("On. Games see an Xbox 360 controller.")).toBeInTheDocument();
    view.unmount();
    expect(calls).toEqual(["bridge:true", "disposed"]);
  });

  it("the Mac switch reports a change that did not take", async () => {
    const { controller, calls } = fake({ kind: "mac" }, false);
    show(controller);
    fireEvent.click(await screen.findByRole("checkbox", { name: "Gamepad in mode 0" }));
    expect(await screen.findByText("This Mac did not take the change, so nothing was changed.")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Gamepad in mode 0" })).not.toBeChecked();
    expect(calls).toEqual(["mac:true"]);
  });

  it("the store build offers the commands to copy instead", async () => {
    const { controller } = fake({ kind: "mac", macSandboxed: true, macCommand: "cmd", macUndoCommand: "undo" });
    show(controller);
    expect(await screen.findByRole("button", { name: "Copy the command" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy the undo command" })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).toBeNull();
    await waitFor(() => expect(screen.getByText(/App Store version cannot make this change/u)).toBeInTheDocument());
  });
});
