import type { MessageKey } from "../../i18n";

/**
 * `FunctionParameters.cs` (firmware 2373): what each number after a function
 * does and what the device uses without it. `what` is a catalog key; the
 * default is a key or, where C# prints it untranslated, the literal text.
 */
interface Parameter {
  readonly what: MessageKey;
  readonly fallback: MessageKey | { readonly literal: string };
}

const ms = (literal: string) => ({ literal });

const TABLE: Readonly<Record<string, readonly Parameter[]>> = {
  repeat: [
    { what: "Fn_HowFastItTapsWhile", fallback: "Fn_10ASecond" },
    { what: "Fn_HoldsTheFirstTapThis", fallback: "Fn_NoExtraHold" },
  ],
  pulse: [
    { what: "Fn_HowLongEachPressLasts", fallback: ms("100 ms") },
    { what: "Fn_HowManyPressesOneActivation", fallback: "Fn_OnePress" },
  ],
  duty: [{ what: "Fn_TheOnAndOffCycle", fallback: ms("100 ms") }],
  greater_than: [
    { what: "Fn_FiresOnceYourInputIs", fallback: ms("100 percent") },
    { what: "Fn_StopsFiringAgainOnceYour", fallback: "Fn_NoUpperLimit" },
  ],
  less_than: [{ what: "Fn_FiresWhileYourInputStays", fallback: ms("50 percent") }],
  force_off: [{ what: "Fn_WaitsThisLongBeforeReleasing", fallback: "Fn_ReleasedAtOnce" }],
  delayed_latch: [{ what: "Fn_HoldTheInputLongerThan", fallback: ms("1000 ms") }],
  delay_off: [{ what: "Fn_KeepsTheOutputPressedThis", fallback: ms("100 ms") }],
  delay_on: [
    { what: "Fn_WaitsThisLongAfterYou", fallback: ms("1000 ms") },
    { what: "Fn_1LatchesTheOutputOn", fallback: "Fn_HoldsWhileYouHold" },
  ],
  tap: [
    { what: "Fn_LetGoInsideThisWindow", fallback: ms("500 ms") },
    { what: "Fn_HowLongTheTapPresses", fallback: ms("100 ms") },
  ],
  increment_value: [
    { what: "Fn_HowFarUpTheAnalog", fallback: ms("10 percent") },
    { what: "Fn_KeepsSteppingThisOftenWhile", fallback: "Fn_OneStepPerActivation" },
  ],
  decrement_value: [
    { what: "Fn_HowFarDownTheAnalog", fallback: ms("10 percent") },
    { what: "Fn_KeepsSteppingThisOftenWhile", fallback: "Fn_OneStepPerActivation" },
  ],
};

type T = (key: MessageKey, values?: readonly unknown[]) => string;

/** `ParameterHint`: one line per number, keyed on the function's first word. */
export function parameterHint(functionCell: string, t: T): readonly string[] {
  const token = functionCell.trim().split(/\s+/u)[0] ?? "";
  const spec = Object.hasOwn(TABLE, token) ? TABLE[token] ?? [] : [];
  return spec.map((parameter, index) => {
    const fallback =
      typeof parameter.fallback === "string" ? t(parameter.fallback) : parameter.fallback.literal;
    const sentence = t("Fn_Sentence", [t(parameter.what), fallback]).trim();
    if (spec.length < 2) return sentence;
    return t(index === 0 ? "Fn_FirstNumber" : "Fn_SecondNumber", [sentence]);
  });
}

/** `OutputCatalog.OtherNames`: the pad's word when the firmware's is not printed on it. */
export function padName(token: string, t: T): string {
  switch (token.trim()) {
    case "start":
      return t("Outputs_AlsoStart");
    case "select":
      return t("Outputs_AlsoSelect");
    case "back":
      return t("Outputs_AlsoBack");
    case "ps3":
      return t("Outputs_AlsoPsButton");
    case "guide":
      return t("Outputs_AlsoGuide");
    case "touch":
      return t("Outputs_AlsoTouch");
    case "capture":
      return t("Outputs_AlsoCapture");
    case "kb_left_gui":
    case "kb_right_gui":
      return t("Outputs_GuiKeyOtherNames");
    default:
      return PRINTED[token.trim()] ?? "";
  }
}

// Printed on the pad, so nothing to translate.
const PRINTED: Readonly<Record<string, string>> = {
  left_1: "L1", left_2: "L2", left_3: "L3",
  right_1: "R1", right_2: "R2", right_3: "R3",
  left_bumper: "LB", left_trigger: "LT", left_stick: "LS",
  right_bumper: "RB", right_trigger: "RT", right_stick: "RS",
};
