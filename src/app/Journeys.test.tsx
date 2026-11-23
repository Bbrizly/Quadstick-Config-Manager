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

describe("templates", () => {
  it("saves a profile as a template, then starts a fresh profile from it on Home", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);

    fireEvent.click(await screen.findByRole("button", { name: "Start a new profile from one of your saved templates" }));
    await screen.findByText(/You have not saved any templates yet/u);

    fireEvent.click(screen.getAllByRole("button", { name: /^Create a new profile/u })[0]!);
    await screen.findByRole("heading", { level: 1, name: "untitled.csv" });
    fireEvent.click(screen.getByRole("button", { name: /^Save this profile as a reusable template/u }));
    const save = await screen.findByRole("dialog", { name: "Save as template" });
    fireEvent.change(within(save).getByLabelText("Name for this template"), { target: { value: "Racing base" } });
    fireEvent.click(within(save).getByRole("button", { name: "Save template" }));
    await screen.findByText(/^Saved template Racing base(\.csv)?\. Find it under Use template/u);

    fireEvent.click(screen.getByRole("button", { name: "Home" }));
    fireEvent.click(await screen.findByRole("button", { name: "Start a new profile from one of your saved templates" }));
    const pick = await screen.findByRole("dialog", { name: "Start from a template" });
    expect(within(pick).getByRole("radio", { name: "Racing base" })).toBeChecked();
    fireEvent.click(within(pick).getByRole("button", { name: "Use template" }));
    await screen.findByText("Started from template Racing base. Save to keep this as its own profile.");
  });
});

describe("language", () => {
  it("switches the whole shell to French from Settings and back", async () => {
    const client = new MockQcmClient();
    render(<App client={client} />);
    fireEvent.click(await screen.findByRole("button", { name: "Open Settings" }));
    const language = await screen.findByRole("combobox", { name: /Language/u });
    fireEvent.change(language, { target: { value: "fr" } });
    await waitFor(() => expect(document.documentElement.lang).toBe("fr"));
    expect(document.documentElement.dir).toBe("ltr");
    await waitFor(async () => expect((await client.getSettings()).language).toBe("fr"));
    expect(screen.queryByRole("button", { name: "Open Settings" })).toBeNull();
  });
});

describe("community import", () => {
  it("lists, imports through the review, and opens the copy with a true status", async () => {
    const client = new MockQcmClient();
    const review = {
      importId: "imp-1",
      name: "Forza",
      modes: [{ number: 1, name: "Drive", kind: "mode" as const, bindingCount: 3 }],
      skipped: [],
      limitation: null,
      renamed: [],
      errorCount: 0,
      warningCount: 0,
    };
    const imported: string[] = [];
    Object.assign(client, {
      loadCommunityCatalog: () =>
        Promise.resolve({
          profiles: [{ name: "Forza Horizon", sheetId: "sheet-1", csvName: "forza.csv", connection: "usb", notes: "", pointer: "" }],
          fromCache: false,
          skippedRows: 0,
        }),
      importCommunityProfile: (sheetId: string, csvName: string) => {
        imported.push(`${sheetId}/${csvName}`);
        return Promise.resolve(review);
      },
      acceptWorkbookImport: () => client.newProfile("forza.csv"),
      cancelWorkbookImport: () => Promise.resolve(),
    });
    render(<App client={client} />);

    fireEvent.click(await screen.findByRole("button", { name: "Browse community profiles" }));
    // The row and the detail heading both carry the name, so find the row.
    await screen.findByRole("option", { name: /^Forza Horizon\b/u, selected: true });
    fireEvent.click(screen.getByRole("button", { name: /^Import/u }));
    const dialog = await screen.findByRole("dialog", { name: "Import review" });
    expect(imported).toEqual(["sheet-1/forza.csv"]);
    expect(within(dialog).getByText(/Your sheet came in clean/u)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: /^Import/u }));
    await screen.findByRole("heading", { level: 1, name: "forza.csv" });
    await screen.findByText(/^Imported 1 mode from the spreadsheet\. This is a copy\./u);
  });
});

describe("Sheets link", () => {
  it("imports a pasted link, then Get the latest says it is up to date", async () => {
    const client = new MockQcmClient();
    const links: string[] = [];
    Object.assign(client, {
      importSheetsLink: (url: string) => {
        links.push(url);
        return Promise.resolve({
          kind: "review",
          review: {
            importId: "imp-2",
            name: "Halo",
            modes: [{ number: 1, name: "Walk", kind: "mode", bindingCount: 2 }],
            skipped: [],
            limitation: null,
            renamed: [],
            errorCount: 0,
            warningCount: 0,
          },
        });
      },
      acceptWorkbookImport: async () => {
        const opened = await client.newProfile("halo.csv");
        client.willSaveAs("halo.csv");
        await client.saveProfileAs(opened.sessionId, opened.revision);
        return client.getProfileSnapshot(opened.sessionId);
      },
      cancelWorkbookImport: () => Promise.resolve(),
      getFollowState: () => Promise.resolve({ follows: true }),
      checkLatestFromLink: () => Promise.resolve({ kind: "up_to_date" }),
      replaceWithLatest: () => Promise.reject(new Error("nothing to replace")),
    });
    render(<App client={client} />);

    const link = "https://docs.google.com/spreadsheets/d/abc/edit";
    fireEvent.change(await screen.findByLabelText("Paste a Google Sheets profile link"), { target: { value: link } });
    fireEvent.click(screen.getAllByRole("button", { name: "Import the profile from the pasted Google Sheets link" }).at(-1)!);
    const dialog = await screen.findByRole("dialog", { name: "Import review" });
    expect(links).toEqual([link]);
    fireEvent.click(within(dialog).getByRole("button", { name: /^Import/u }));
    await screen.findByRole("heading", { level: 1, name: "halo.csv" });

    // Get the latest lives in the Share menu, as in Avalonia.
    fireEvent.click(screen.getByRole("button", { name: "Share this profile as a Google Sheet" }));
    fireEvent.click(await screen.findByRole("button", { name: /Download this profile again/u }));
    await screen.findByText("Already up to date. The sheet matches your copy.");
  });
});

describe("Drive backup and restore", () => {
  it("backs this profile up from the Share menu, then restores it from Home", async () => {
    const client = new MockQcmClient();
    const calls = { backups: 0, restored: [] as string[] };
    Object.assign(client, {
      getGoogleAuthStatus: () => Promise.resolve({ supported: true, configured: true, connected: true }),
      connectGoogle: () => Promise.resolve({ supported: true, configured: true, connected: true }),
      backupProfileToDrive: () => {
        calls.backups += 1;
        return Promise.resolve({ kind: "pushed" });
      },
      resolveDriveConflict: () => Promise.reject(new Error("no conflict")),
      listDriveBackups: () => Promise.resolve([{ cloudRef: "ref-1", name: "racing", modifiedTime: "" }]),
      restoreDriveBackups: (refs: readonly string[]) => {
        calls.restored.push(...refs);
        return Promise.resolve({ imported: ["racing"], skipped: [], failed: [] });
      },
    });
    render(<App client={client} />);

    client.willOpen("racing.csv");
    fireEvent.click((await screen.findAllByRole("button", { name: "Open a profile CSV file from your computer" }))[0]!);
    await screen.findByRole("heading", { level: 1, name: "racing.csv" });
    fireEvent.click(screen.getByRole("button", { name: "Share this profile as a Google Sheet" }));
    // The item says what it does: it backs up now, it is not a progress line.
    fireEvent.click(await screen.findByRole("button", { name: "Back up this profile to Google Drive" }));
    await screen.findByText("Backed up to Google Drive.");
    expect(calls.backups).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: "Home" }));
    fireEvent.click(await screen.findByRole("button", { name: /Click to import your other profiles/u }));
    const racing = await screen.findByRole("checkbox", { name: /racing/u });
    fireEvent.click(racing);
    const picker = screen.getByRole("dialog", { name: "Import from Google Drive" });
    fireEvent.click(within(picker).getAllByRole("button", { name: /^Import/u })[0]!);
    await waitFor(() => expect(calls.restored).toEqual(["ref-1"]));
  });
});

describe("device settings", () => {
  it("changes a slider the way a screen reader does, then saves it to the stick", async () => {
    const client = new MockQcmClient();
    client.plugDevice("QUADSTICK", {
      "prefs.csv": "Preferences\nprefs.csv\nPreference,Value,Units,Description\nmouse_speed,100,,\n",
    });
    Object.assign(client, {
      getPreferenceCatalog: () =>
        Promise.resolve({
          categories: ["Mouse"],
          definitions: [{
            name: "mouse_speed", label: "Mouse speed", category: "Mouse", editor: "integer", default: "100",
            minimum: 0, maximum: 200, unit: "", description: "", options: [], modeOverride: false, risk: "",
            source: "", firmwareMayAddMore: false, alsoCalled: "", advanced: false,
          }],
        }),
    });
    render(<App client={client} />);
    fireEvent.click(screen.getByRole("button", { name: "Manage files on your QuadStick" }));
    const slider = await screen.findByRole("slider", { name: "Mouse speed" });

    // VoiceOver's increment fires input and change, never a key or pointer up.
    fireEvent.change(slider, { target: { value: "120" } });
    fireEvent.blur(slider);
    expect(await screen.findByText(/1 setting changed/u)).toBeInTheDocument();
    const save = screen.getByText("Save to your QuadStick").closest("button")!;
    expect(save).toBeEnabled();
    fireEvent.click(save);
    const confirm = await screen.findByRole("dialog", { name: "Device settings" });
    fireEvent.click(within(confirm).getByRole("button", { name: "Save" }));
    // After the write the line says it was saved, not that it is ready to save.
    await screen.findByText("Saved to prefs.csv.");
    expect(screen.queryByText("No problems. Ready to save or install.")).toBeNull();
    await waitFor(() => expect(screen.getByText("Save to your QuadStick").closest("button")).toBeDisabled());
  });
});
