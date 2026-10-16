/** One card under "Your profiles" (`library_shell::LibraryCardDto`). */
export interface LibraryCard {
  /** Opaque, valid until the next listing. Never a path. */
  readonly id: string;
  readonly name: string;
  readonly title: string;
  readonly modes: number;
  readonly bindings: number;
  readonly modeNames: readonly string[];
  readonly editedMs: number | null;
  readonly follows: boolean;
  readonly readable: boolean;
}

export interface TemplateEntry {
  readonly id: string;
  readonly name: string;
}

export interface TemplateReceipt {
  readonly name: string;
}

/** `library_shell::TemplateRenameDto`: a name another template has is refused. */
export type TemplateRename =
  | { readonly kind: "renamed"; readonly name: string }
  | { readonly kind: "taken"; readonly name: string };

/** `names_shell::CustomNameDraftsDto`: `[name, output]` pairs no row uses yet. */
export interface CustomNameDrafts {
  readonly drafts: readonly (readonly [string, string])[];
  /** False while the profile has no file to keep them under. */
  readonly kept: boolean;
}
