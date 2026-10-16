import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { I18nProvider } from "../../i18n";
import { MockQcmClient } from "../../platform";
import { CommunityProfilesPage } from "./CommunityProfilesPage";

// CommunityProfilesView.Load: offline with no saved copy says so and points
// at Refresh, instead of "Loading..." forever.
describe("community profiles offline", () => {
  it("says the list could not be downloaded when there is no network and no copy", async () => {
    const client = new MockQcmClient();
    Object.assign(client, {
      loadCommunityCatalog: () => Promise.reject(new Error("offline")),
      importCommunityProfile: () => Promise.reject(new Error("offline")),
    });
    render(
      <I18nProvider>
        <CommunityProfilesPage client={client} onReview={() => undefined} />
      </I18nProvider>,
    );
    expect(
      await screen.findByText(
        "The community list could not be downloaded, and there is no saved copy on this computer.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText("Loading the community list...")).not.toBeInTheDocument();
    expect(screen.getByText(/Check your internet connection and press Refresh/u)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh" })).toBeEnabled();
  });
});
