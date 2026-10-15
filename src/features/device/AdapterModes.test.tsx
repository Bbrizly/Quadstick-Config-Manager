import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { AdapterModes } from "./DevicePreferencesPage";

describe("AdapterModes", () => {
  it("says which mode each console adapter needs", () => {
    render(
      <I18nProvider initialPreference="en">
        <AdapterModes />
      </I18nProvider>,
    );
    expect(screen.getByText(/Playing through a console adapter/u)).toBeInTheDocument();
    expect(screen.getByText(/Mayflash Magic-X.*mode 0/u)).toBeInTheDocument();
    expect(screen.getByText(/Brook adapters for the PS5.*mode 4/u)).toBeInTheDocument();
  });
});
