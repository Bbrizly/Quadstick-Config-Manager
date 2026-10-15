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
