import { placedInputLabel, outputLabel } from "../features/editor/labels";
import { translatorFor, type LocaleTag } from "../i18n";

/** The editor's words for a profile's tokens, in one language. */
export interface ProfileLabels {
  /** "mp_left_sip" as "Left · sip"; a combo names its pairing. */
  input(token: string): string;
  /** "mouse_left" as "Mouse left". */
  output(token: string): string;
}

/**
 * For a host that lists a profile's lines outside the editor (a comparison,
 * a printout), so they read as the editor reads them.
 */
export async function profileLabels(locale: LocaleTag): Promise<ProfileLabels> {
  const t = await translatorFor(locale);
  return {
    input: (token) => placedInputLabel(token, t),
    output: (token) => outputLabel(token),
  };
}
