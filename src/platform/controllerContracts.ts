/** The Linux Xbox controller bridge and the Mac SDL gamepad switch. */
import type { Subscription } from "./contracts";

export type BridgeState =
  | "waiting"
  | "bridging"
  | "no_access"
  | "no_virtual_pad"
  | "unsupported"
  | "stopped";

export interface ControllerOptions {
  /** `bridge` on Linux with the binary, `mac` on macOS, else `none`. */
  readonly kind: "bridge" | "mac" | "none";
  readonly bridgeOn: boolean;
  readonly bridgeState: BridgeState | null;
  readonly macOn: boolean;
  readonly macSandboxed: boolean;
  /** Only for the sandboxed Mac build, which cannot run them itself. */
  readonly macCommand: string | null;
  readonly macUndoCommand: string | null;
}

export interface MacGamepadResult {
  readonly on: boolean;
  readonly took: boolean;
}

export interface ControllerClient {
  getControllerOptions(): Promise<ControllerOptions>;
  setControllerBridge(on: boolean): Promise<boolean>;
  setMacGamepad(on: boolean): Promise<MacGamepadResult>;
  subscribeControllerBridge(onState: (state: BridgeState | null) => void): Promise<Subscription>;
}
