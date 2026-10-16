import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MockQcmClient } from "../platform";
import { App } from "./App";

describe("Home card menu (ShareMenu(path))", () => {
  it("forgets a card's link without opening the profile", async () => {
    const client = new MockQcmClient();
    client.seedLibrary([{ name: "Forza", follows: true }]);
    const forget = vi.fn((_session: string) => Promise.resolve({ follows: false }));
    Object.assign(client, {
      getFollowState: () => Promise.resolve({ follows: true }),
      checkLatestFromLink: () => Promise.resolve({ kind: "up_to_date" }),
      replaceWithLatest: () => Promise.resolve({ kind: "changed_meanwhile" }),
      forgetFollowLink: forget,
    });
    render(<App client={client} />);
    fireEvent.click(await screen.findByRole("button", { name: "More actions for Forza" }));
    fireEvent.click(screen.getByRole("button", { name: /Forget|Stop following/u }));
    await waitFor(() => expect(forget).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("This profile no longer follows its link.")).toBeInTheDocument();
    // Still Home: the editor never opened.
    expect(screen.getByRole("heading", { level: 2, name: "Start a profile" })).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: /Profile name/u })).toBeNull();
  });
});
