import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

// MainWindow.Registry.cs: a qcm://profile/<id> launch opens that registry
// profile in the editor, unsaved, and a failure says so on the home screen.
describe("qcm://profile links", () => {
  it("opens the launch link's profile once", async () => {
    const client = new MockQcmClient();
    const asked: string[] = [];
    let link: string | null = "minecraft-basic";
    Object.assign(client, {
      takeLaunchProfileLink: () => {
        const taken = link;
        link = null;
        return Promise.resolve(taken);
      },
      openRegistryProfile: (id: string) => {
        asked.push(id);
        return client.newProfile("minecraft.csv");
      },
    });
    render(<App client={client} />);
    await waitFor(() => expect(asked).toEqual(["minecraft-basic"]));
    expect(await screen.findByRole("region", { name: "Profile" })).toBeInTheDocument();
  });

  it("says the list could not be reached when the profile cannot be opened", async () => {
    const client = new MockQcmClient();
    Object.assign(client, {
      takeLaunchProfileLink: () => Promise.resolve("gone"),
      openRegistryProfile: () => Promise.reject(new Error("offline")),
    });
    render(<App client={client} />);
    expect(await screen.findByText(/The community list could not be downloaded/u)).toBeInTheDocument();
  });
});
