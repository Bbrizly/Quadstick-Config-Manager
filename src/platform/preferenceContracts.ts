export type PreferenceEditorKind = "integer" | "toggle" | "choice" | "text";

export interface PreferenceOption {
  readonly value: string;
  readonly label: string;
}

export interface PreferenceDefinition {
  readonly name: string;
  readonly label: string;
  readonly category: string;
  readonly editor: PreferenceEditorKind;
  readonly default: string | null;
  readonly minimum: number | null;
  readonly maximum: number | null;
  readonly unit: string;
  readonly description: string;
  readonly options: readonly PreferenceOption[];
  readonly modeOverride: boolean;
  readonly risk: string;
  readonly source: string;
  readonly firmwareMayAddMore: boolean;
  readonly alsoCalled: string;
}

export interface PreferenceCatalog {
  readonly categories: readonly string[];
  readonly definitions: readonly PreferenceDefinition[];
}
