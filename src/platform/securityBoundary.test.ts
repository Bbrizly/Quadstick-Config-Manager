import { describe, expect, it } from "vitest";

import capabilityText from "../../src-tauri/capabilities/main.json?raw";
import tauriConfigText from "../../src-tauri/tauri.conf.json?raw";
import cargoManifest from "../../src-tauri/Cargo.toml?raw";
import packageManifest from "../../package.json?raw";
import { TAURI_COMMANDS } from "./tauriQcmClient";

interface CapabilityFile {
  readonly local: boolean;
  readonly windows: readonly string[];
  readonly permissions: readonly string[];
}

interface TauriConfig {
  readonly app: {
    readonly withGlobalTauri: boolean;
    readonly security: {
      readonly csp: string;
      readonly devCsp: string;
      readonly freezePrototype: boolean;
      readonly assetProtocol: {
        readonly enable: boolean;
        readonly scope: readonly string[];
      };
    };
  };
}

const capability = JSON.parse(capabilityText) as CapabilityFile;
const config = JSON.parse(tauriConfigText) as TauriConfig;
const commandNames: readonly string[] = TAURI_COMMANDS;

const FORBIDDEN_PLUGIN_PACKAGES = [
  "tauri-plugin-fs",
  "tauri-plugin-shell",
  "tauri-plugin-http",
  "tauri-plugin-opener",
  "tauri-plugin-process",
  "tauri-plugin-store",
  "tauri-plugin-stronghold",
  "tauri-plugin-updater",
  "@tauri-apps/plugin-fs",
  "@tauri-apps/plugin-shell",
  "@tauri-apps/plugin-http",
  "@tauri-apps/plugin-opener",
  "@tauri-apps/plugin-process",
  "@tauri-apps/plugin-store",
  "@tauri-apps/plugin-stronghold",
  "@tauri-apps/plugin-updater",
] as const;

const FORBIDDEN_PLUGIN_COMMANDS = [
  "plugin:fs|read_file",
  "plugin:fs|write_file",
  "plugin:shell|execute",
  "plugin:http|fetch",
  "plugin:opener|open_url",
  "plugin:process|exit",
  "plugin:store|set",
  "plugin:stronghold|save",
  "plugin:updater|download_and_install",
] as const;

function directives(csp: string): Map<string, readonly string[]> {
  return new Map(
    csp
      .split(";")
      .map((directive) => directive.trim())
      .filter(Boolean)
      .map((directive) => {
        const [name = "", ...sources] = directive.split(/\s+/u);
        return [name, sources] as const;
      }),
  );
}

describe("TASK-035 WebView security boundary", () => {
  it("gives the main window no direct plugin capability", () => {
    expect(capability.local).toBe(true);
    expect(capability.windows).toEqual(["main"]);
    expect(capability.permissions).toEqual([]);
  });

  it("keeps the Tauri global and asset protocol disabled", () => {
    expect(config.app.withGlobalTauri).toBe(false);
    expect(config.app.security.freezePrototype).toBe(true);
    expect(config.app.security.assetProtocol).toEqual({ enable: false, scope: [] });
  });

  it("keeps production CSP local and IPC-only", () => {
    const csp = directives(config.app.security.csp);
    expect(csp.get("default-src")).toEqual(["'self'"]);
    expect(csp.get("script-src")).toEqual(["'self'"]);
    expect(csp.get("connect-src")).toEqual(["'self'", "ipc:", "http://ipc.localhost"]);
    expect(csp.get("object-src")).toEqual(["'none'"]);
    expect(csp.get("frame-src")).toEqual(["'none'"]);
    expect(csp.get("frame-ancestors")).toEqual(["'none'"]);
    expect(csp.get("form-action")).toEqual(["'none'"]);

    const everySource = [...csp.values()].flat();
    for (const forbidden of ["*", "https:", "ws:", "wss:"]) {
      expect(everySource).not.toContain(forbidden);
    }
  });

  it("does not install broad native plugins behind the empty capability", () => {
    for (const dependency of FORBIDDEN_PLUGIN_PACKAGES) {
      expect(cargoManifest).not.toContain(dependency);
      expect(packageManifest).not.toContain(dependency);
    }
  });

  it("keeps the command ledger domain-only with no plugin escape hatch", () => {
    // TASK-031 separately proves the adapter is the only frontend source that
    // may name a domain command. This test intentionally does not duplicate
    // those command literals, otherwise the security test would violate the
    // boundary it is meant to defend.
    expect(commandNames.length).toBeGreaterThan(0);
    expect(new Set(commandNames).size).toBe(commandNames.length);
    expect(commandNames.every((command) => !command.startsWith("plugin:"))).toBe(true);

    for (const forbidden of FORBIDDEN_PLUGIN_COMMANDS) {
      expect(commandNames).not.toContain(forbidden);
    }
  });

  it("limits development network access to the pinned Vite origin", () => {
    const dev = directives(config.app.security.devCsp);
    const connect = dev.get("connect-src") ?? [];
    expect(connect).toContain("http://localhost:1420");
    expect(connect).toContain("ws://localhost:1420");
    expect(connect).not.toContain("*");
    expect(connect).not.toContain("https:");
  });
});
