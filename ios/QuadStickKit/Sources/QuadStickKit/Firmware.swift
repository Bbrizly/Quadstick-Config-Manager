import Foundation

/// Keyword maps between this app's catalog and the firmware's vocabulary.
/// Source of truth: Joystick/*.h of FW 2373, via the desktop repo's
/// tests/QuadStick.Format.Tests/corpus/firmware-2373.json. If a keyword here
/// disagrees with the firmware, this file is what changes.
public enum Firmware {

    /// Catalog action id to firmware input keyword.
    public static let inputKeyword: [String: String] = [
        "left-tube-normal-sip": "mp_left_sip",
        "left-tube-normal-puff": "mp_left_puff",
        "left-tube-soft-sip": "mp_left_sip_soft",
        "left-tube-soft-puff": "mp_left_puff_soft",
        "center-tube-normal-sip": "mp_center_sip",
        "center-tube-normal-puff": "mp_center_puff",
        "center-tube-soft-sip": "mp_center_sip_soft",
        "center-tube-soft-puff": "mp_center_puff_soft",
        "right-tube-normal-sip": "mp_right_sip",
        "right-tube-normal-puff": "mp_right_puff",
        "right-tube-soft-sip": "mp_right_sip_soft",
        "right-tube-soft-puff": "mp_right_puff_soft",
        "side-tube-normal-sip": "right_sip",
        "side-tube-normal-puff": "right_puff",
        "side-tube-soft-sip": "right_sip_soft",
        "side-tube-soft-puff": "right_puff_soft",
        "side-tube-long-sip": "right_sip_long",
        "side-tube-long-puff": "right_puff_long",
        "joystick-up": "up",
        "joystick-down": "down",
        "joystick-left": "left",
        "joystick-right": "right",
        "lip-press": "lip",
        "lip-soft-press": "lip_soft",
        "jack-top-a": "digital_in_7",
        "jack-top-b": "digital_in_8",
        "jack-middle-a": "digital_in_5",
        "jack-middle-b": "digital_in_6",
        "jack-bottom-a": "digital_in_1",
        "jack-bottom-b": "digital_in_2",
        "usb-up": "usb_1_up",
        "usb-down": "usb_1_down",
        "usb-left": "usb_1_left",
        "usb-right": "usb_1_right",
        "usb-button-1": "usb_1_button_1",
        "usb-button-2": "usb_1_button_2",
        "usb-button-3": "usb_1_button_3",
        "usb-button-4": "usb_1_button_4",
        "usb-button-5": "usb_1_button_5",
        "usb-button-6": "usb_1_button_6",
        "usb-button-7": "usb_1_button_7",
        "usb-button-8": "usb_1_button_8",
    ]

    /// Firmware input keyword back to catalog action id.
    public static let actionID: [String: String] =
        Dictionary(uniqueKeysWithValues: inputKeyword.map { ($1, $0) })

    /// Catalog output id to firmware output keyword. Exports use the generic
    /// controller keywords 2373 added (A, left_bumper, ...) so one file works
    /// for every controller type.
    public static let outputKeyword: [String: String] = [
        "controller-a": "A",
        "controller-b": "B",
        "controller-x": "X",
        "controller-y": "Y",
        "controller-left-trigger": "left_trigger",
        "controller-right-trigger": "right_trigger",
        "controller-left-bumper": "left_bumper",
        "controller-right-bumper": "right_bumper",
        "controller-d-pad-up": "dpad_N",
        "controller-d-pad-down": "dpad_S",
        "controller-d-pad-left": "dpad_W",
        "controller-d-pad-right": "dpad_E",
        "controller-left-stick-up": "left_joy_up",
        "controller-left-stick-down": "left_joy_down",
        "controller-left-stick-left": "left_joy_left",
        "controller-left-stick-right": "left_joy_right",
        "controller-right-stick-up": "right_joy_up",
        "controller-right-stick-down": "right_joy_down",
        "controller-right-stick-left": "right_joy_left",
        "controller-right-stick-right": "right_joy_right",
        "controller-left-stick-click": "left_stick",
        "controller-right-stick-click": "right_stick",
        "controller-start": "start",
        "controller-select": "select",
        "keyboard-space": "kb_space",
        "keyboard-enter": "kb_enter",
        "keyboard-escape": "kb_escape",
        "keyboard-tab": "kb_tab",
        "keyboard-shift": "kb_left_shift",
        "keyboard-w": "kb_w",
        "keyboard-a": "kb_a",
        "keyboard-s": "kb_s",
        "keyboard-d": "kb_d",
        "keyboard-e": "kb_e",
        "keyboard-r": "kb_r",
        "keyboard-1": "kb_1",
        "keyboard-2": "kb_2",
        "keyboard-3": "kb_3",
        "mouse-left-click": "mouse_left_button",
        "mouse-right-click": "mouse_right_button",
        "mouse-middle-click": "mouse_middle_button",
        "mouse-scroll-up": "mouse_wheel_up",
        "mouse-scroll-down": "mouse_wheel_down",
        "quadstick-volume-up": "volume_up",
        "quadstick-volume-down": "volume_down",
        "quadstick-brightness-up": "brightness_up",
        "quadstick-brightness-down": "brightness_down",
        "quadstick-restart-quadstick": "reset_quadstick",
        "mode & profile-next-mode": "increment_mode",
        "mode & profile-previous-mode": "decrement_mode",
        "mode & profile-load-next-profile": "load_file",
    ]

    /// Firmware output keyword back to catalog output id. Includes the
    /// PlayStation spellings community files use (x, circle, left_1, ...),
    /// mapped onto the equivalent generic button position.
    public static let outputID: [String: String] = {
        var map = Dictionary(uniqueKeysWithValues: outputKeyword.map { ($1, $0) })
        map["x"] = "controller-a"            // cross, bottom button
        map["circle"] = "controller-b"
        map["square"] = "controller-x"
        map["triangle"] = "controller-y"
        map["left_1"] = "controller-left-bumper"
        map["left_2"] = "controller-left-trigger"
        map["left_3"] = "controller-left-stick-click"
        map["right_1"] = "controller-right-bumper"
        map["right_2"] = "controller-right-trigger"
        map["right_3"] = "controller-right-stick-click"
        return map
    }()

    /// The word to write for a catalog output. Curated ids are translated;
    /// every other id is already a firmware word, so it stands for itself.
    public static func keyword(forOutput id: String) -> String? {
        if let mapped = outputKeyword[id] { return mapped }
        return Vocabulary.accepts(output: id) ? id : nil
    }

    /// The catalog id for a word read out of a file, mirroring the above.
    public static func outputID(forKeyword keyword: String) -> String? {
        if let mapped = outputID[keyword] { return mapped }
        return Vocabulary.accepts(output: keyword) ? keyword : nil
    }
}
