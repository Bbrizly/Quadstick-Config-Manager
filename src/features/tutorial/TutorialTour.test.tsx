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

  // Every title is a catalog word: in French none of the eight is English.
  it("titles every step in the chosen language", async () => {
    render(
      <I18nProvider initialPreference="fr">
        <TutorialTour open onDone={vi.fn()} />
      </I18nProvider>,
    );
    await screen.findByText("Étape 1 sur 8");
    const titles: string[] = [];
    for (let step = 0; step < 8; step += 1) {
      titles.push(screen.getByRole("dialog").querySelector("h2, h1, h3")?.textContent ?? "");
      fireEvent.click(screen.getAllByRole("button").at(-1)!);
    }
    for (const english of ["Welcome", "Your QuadStick", "Save", "Install", "Done"]) {
      expect(titles).not.toContain(english);
    }
    expect(titles[0]).toBe("Bienvenue");
  });
});
