import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { BindingInspector } from "./BindingInspector";

function inspector(output: string, fn: string) {
  const cells = [output, fn, "puff", "", "", "", "", "", "", ""];
  return render(
    <I18nProvider initialPreference="en">
      <BindingInspector row={4} cells={cells} disabled={false} onSetCell={() => undefined} />
    </I18nProvider>,
  );
}

describe("wording with a home (88eda16, 1cc0cd8, 2b0552c)", () => {
  it("names the button on the pad when the firmware word is not printed on it", () => {
    inspector("start", "normal");
    expect(screen.getByText("Options on a PlayStation pad, Menu on an Xbox pad")).toBeInTheDocument();
  });

  it("says what each function number does, then its default", () => {
    inspector("cross", "repeat 5");
    expect(
      screen.getByText("First number: How many presses per second while you hold the input. Default: 10 a second."),
    ).toBeInTheDocument();
    expect(screen.getByText(/^Second number: How long to hold the button normally/u)).toBeInTheDocument();
  });

  it("one number reads without a First/Second prefix, and normal has none", () => {
    const view = inspector("cross", "less_than 40");
    expect(screen.getByText("The output is on while the input is below this percentage. Default: 50 percent.")).toBeInTheDocument();
    view.unmount();
    inspector("cross", "normal");
    expect(screen.queryByText(/Default:/u)).toBeNull();
  });
});
