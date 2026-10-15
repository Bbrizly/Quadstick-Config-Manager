/**
 * The names the device knows, from the same `validation.json` the Rust and C#
 * validators embed (`Vocab.cs`). Used for the Unused list and the mock's
 * stand-in validation; the real problems list always comes from Rust.
 */
import validation from "../QuadStick.Format/Data/validation.json";

/** `Vocab.LegacyInputs`: still read by firmware 2373, no longer listed. */
export const LEGACY_INPUTS: readonly string[] = ["push", "lip_soft", "right_sip_long", "right_puff_long", "bluetooth_status"];
/** `Vocab.LegacyOutputs`. */
export const LEGACY_OUTPUTS: readonly string[] = ["gyroscope_cw", "gyroscope_ccw"];
/** `SwitchJacks.RearJoystick`: accepted by firmware, absent from validation.json. */
export const REAR_JOYSTICK: readonly string[] = ["usb_1_up", "usb_1_down", "usb_1_left", "usb_1_right"];

export const INPUTS: ReadonlySet<string> = new Set(validation.inputs);
/** `Vocab.AllInputs` plus the rear joystick, the Unused picker's vocabulary. */
export const PICKER_INPUTS: readonly string[] = [...new Set([...validation.inputs, ...LEGACY_INPUTS, ...REAR_JOYSTICK])];
export const KNOWN_OUTPUTS: ReadonlySet<string> = new Set([
  ...validation.outputs_ps3,
  ...validation.outputs_xbox,
  ...LEGACY_OUTPUTS,
]);
/** The function names validation.json lists, the Function picker's choices. */
export const FUNCTIONS: readonly string[] = validation.functions;
