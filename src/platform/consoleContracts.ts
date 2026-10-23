/** The QuadStick's own console over Bluetooth SPP or a serial cable. Ports are opaque ids. */
export interface ConsolePort {
  readonly id: string;
  readonly label: string;
}

export type ConsoleConnectOutcome =
  | { readonly kind: "connected"; readonly build: number }
  | { readonly kind: "notAQuadStick" }
  | { readonly kind: "couldNotOpen"; readonly port: string; readonly detail: string };

export interface ConsoleProfilePick {
  readonly pickId: string;
  readonly name: string | null;
  readonly isDefault: boolean;
  readonly isPreferences: boolean;
}

export interface ConsoleSent {
  readonly name: string;
  /** Where the device's old copy went, as a location a user can find; null when it had none. */
  readonly backup: string | null;
}

export interface StrengthSetup {
  /** The hard threshold the person plays with, percent. */
  readonly threshold: number;
}

/** Console failure codes. The payload's `subject` names the file or setting. */
export const CONSOLE_ERROR_CODES = {
  noAnswer: "QCM_CONSOLE_NO_ANSWER",
  invalidText: "QCM_CONSOLE_INVALID_TEXT",
  settingRefused: "QCM_CONSOLE_SETTING_REFUSED",
  couldNotOpen: "QCM_CONSOLE_COULD_NOT_OPEN",
  writeRefused: "QCM_CONSOLE_WRITE_REFUSED",
  couldNotLoad: "QCM_CONSOLE_COULD_NOT_LOAD",
  readBackDiffers: "QCM_CONSOLE_READ_BACK_DIFFERS",
  port: "QCM_CONSOLE_PORT",
} as const;

export interface ConsoleClient {
  listConsolePorts(): Promise<readonly ConsolePort[]>;
  connectConsole(portId: string): Promise<ConsoleConnectOutcome>;
  closeConsole(): Promise<void>;
  /** With `consoleSendProfile`: absent, the window has no Send profile. */
  chooseConsoleProfile?(): Promise<ConsoleProfilePick | null>;
  consoleSendProfile?(pickId: string, confirmDefaultCsv: boolean, confirmPreferencesCsv: boolean): Promise<ConsoleSent>;
  consoleStrengthBegin(): Promise<StrengthSetup>;
  consoleCalibrate(): Promise<void>;
  consoleSetThreshold(puff: boolean, value: number): Promise<void>;
  /** Delete the test file and restart the QuadStick. Never fails; the port is let go. */
  consoleStrengthEnd(): Promise<void>;
  /** The strength test finished: record it for usage statistics, if allowed. */
  recordStrengthMeasured?(puff: number, sip: number): Promise<void>;
}
