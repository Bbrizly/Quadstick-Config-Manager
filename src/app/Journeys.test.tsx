import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

// Whole journeys through the shell, start to finish, against the mock. Each
// step is what a person does; each check is what they would read next.

async function firstRunClient(): Promise<MockQcmClient> {
  const client = new MockQcmClient();
  client.enableUsageData();
  const settings = await client.getSettings();
  await client.updateSettings(settings.revision, { tutorialSeen: false });
  return client;
}

describe("first run to an installed profile", () => {
  it("answers usage, walks the tour, maps a sip, saves, installs, and reopens from Home", async () => {
    const client = await firstRunClient();
    render(<App client={client} />);

    // Usage question first, then the tour, never both at once.
    fireEvent.click(await screen.findByRole("button", { name: "No thanks, do not share usage data" }));
    await screen.findByText("Step 1 of 8");
    for (let step = 1; step < 8; step += 1) {
      fireEvent.click(screen.getByRole("button", { name: "Next step" }));
    }
    await screen.findByText("Step 8 of 8");
    fireEvent.click(screen.getByRole("button", { name: "Next step" }));
    await waitFor(() => expect(screen.queryByText(/Step \d of 8/u)).toBeNull());
    await waitFor(async () => expect((await client.getSettings()).tutorialSeen).toBe(true));

    // New profile, then the left hole's own add button maps its sip.
    fireEvent.click(screen.getAllByRole("button", { name: /^Create a new profile/u })[0]!);
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    fireEvent.click(screen.getByRole("button", { name: /^Left\. Soft Puff/u }));
    fireEvent.click(await screen.findByRole("button", { name: "Add a new mapping for the Left mouthpiece hole" }));
    const panel = screen.getByRole("complementary", { name: "Left mouthpiece hole" });
    await within(panel).findByText("1 mapping");
    // Focus lands on the seeded input, not the page.
    await waitFor(() => expect(document.activeElement).toBe(within(panel).getByLabelText("Input 1 for row 6")));
    expect(within(panel).getByLabelText("Input 1 for row 6")).toHaveValue("mp_left_sip");

    // Typing a button name, one keystroke at a time, then leaving the field.
    const output = within(panel).getByLabelText("Output for row 6");
    fireEvent.change(output, { target: { value: "c" } });
    fireEvent.change(output, { target: { value: "cross" } });
    fireEvent.blur(output);
    await waitFor(() => expect(within(panel).getByLabelText("Output for row 6")).toHaveValue("cross"));
    await screen.findByLabelText("This profile has unsaved changes. Save them before leaving?");

    // Validate: the problems dock still says it installs.
    expect(screen.getByText(/so this still installs/u)).toBeInTheDocument();

    // Save. A new profile has no file yet, so it asks where.
    client.willSaveAs("untitled.csv");
    fireEvent.click(screen.getByRole("button", { name: /^Save this profile\./u }));
    await waitFor(() =>
      expect(screen.queryByLabelText("This profile has unsaved changes. Save them before leaving?")).toBeNull(),
    );

    // Install to the (mock) stick.
    const deviceId = client.plugDevice("QUADSTICK");
    fireEvent.click(screen.getByRole("button", { name: /^Install this profile onto a connected QuadStick/u }));
    const install = await screen.findByRole("dialog", { name: "Installing profile" });
    await waitFor(() => expect(within(install).getByRole("combobox")).toHaveValue(deviceId));
    fireEvent.click(within(install).getByRole("button", { name: "Install to QuadStick" }));
    await within(install).findByText("Installed untitled.csv to QUADSTICK.");
    // The native stage tokens are not words a person reads.
    expect(within(install).queryByText("temp_read_back")).toBeNull();
    fireEvent.click(within(install).getByRole("button", { name: "Close" }));

    // Home lists it; its card opens the same mapping again.
    fireEvent.click(screen.getByRole("button", { name: "Home" }));
    await screen.findByRole("heading", { level: 2, name: "Start a profile" });
    await screen.findByRole("button", { name: "More actions for untitled" });
    const card = screen.getAllByRole("button", { name: /untitled/u }).find((button) => !button.getAttribute("aria-label")?.startsWith("More actions"));
    fireEvent.click(card!);
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    fireEvent.click(screen.getByRole("button", { name: /^Left\. Soft Puff/u }));
    const reopened = screen.getByRole("complementary", { name: "Left mouthpiece hole" });
    expect(within(reopened).getByText("1 mapping")).toBeInTheDocument();
  });
});
