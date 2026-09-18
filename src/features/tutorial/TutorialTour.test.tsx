import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { I18nProvider } from "../../i18n";
import { TutorialTour } from "./TutorialTour";

describe("TutorialTour", () => {
  it("walks steps then finishes", () => {
    const onDone = vi.fn();
    render(
      <I18nProvider initialPreference="en">
        <TutorialTour open onDone={onDone} />
      </I18nProvider>,
    );
    expect(screen.getByText(/This shows you how to make/u)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Next step" }));
    expect(screen.getByText(/Set light or dark/u)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Skip the tutorial" }));
    expect(onDone).toHaveBeenCalled();
  });
});
