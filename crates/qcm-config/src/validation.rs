use crate::issue::{Issue, IssueKind, Severity};
use crate::model::{Binding, ModeSheet, ProfileDocument, SheetType};
use crate::preferences::{PreferenceDefinition, PreferenceEditor, load_preferences};
use crate::vocab::{
    CHANNELS, FIRMWARE_FUNCTION_ORDER, LEGACY_INPUTS, LEGACY_OUTPUTS, NONE_INPUT, ValidationCatalog,
    function_arity, known_outputs, load_validation, preference_overrides,
};
use std::collections::{BTreeSet, HashMap};

const MAX_DEVICE_FILE_NAME_LENGTH: usize = 31;
const MAX_BINDINGS: usize = 128;
const MAX_PROFILES: usize = 16;
const FUNCTION_PARAMETER_CEILING: i64 = 16_383;

struct Resources {
    validation: ValidationCatalog,
    preferences: Vec<PreferenceDefinition>,
    outputs: BTreeSet<String>,
    overrides: BTreeSet<String>,
}

impl Resources {
    fn load() -> Self {
        let validation = load_validation().expect("embedded validation.json must stay valid");
        let preferences = load_preferences().expect("embedded preferences.json must stay valid");
        let outputs = known_outputs(&validation);
        let overrides = preference_overrides(&preferences);
        Self {
            validation,
            preferences,
            outputs,
            overrides,
        }
    }

    fn preference(&self, name: &str) -> Option<&PreferenceDefinition> {
        self.preferences.iter().find(|definition| definition.name == name)
    }

    fn is_preference_override(&self, binding: &Binding) -> bool {
        self.overrides.contains(binding.output.as_str())
    }
}

/// Apply the frozen C# validator to an already parsed document.
#[must_use]
pub fn validate(document: &ProfileDocument) -> Vec<Issue> {
    let resources = Resources::load();
    let mut issues = Vec::new();
    validate_file_name(document, &mut issues);

    let decides_boot_mode = document.is_default_config() || document.is_device_preferences();
    let mut profile_sheets = 0usize;

    for sheet in &document.sheets {
        if sheet.sheet_type == SheetType::Preferences {
            validate_preferences_sheet(
                sheet,
                decides_boot_mode,
                &resources,
                &mut issues,
            );
            continue;
        }
        if sheet.sheet_type != SheetType::ProfileName {
            continue;
        }

        profile_sheets += 1;
        if profile_sheets == MAX_PROFILES + 1 {
            issues.push(warning(
                format!("A{}", sheet.start_row),
                "The device supports only sixteen profile modes.",
            ));
        }

        let counted = sheet
            .bindings
            .iter()
            .filter(|binding| {
                resources.outputs.contains(binding.output.as_str())
                    || resources.overrides.contains(binding.output.as_str())
            })
            .collect::<Vec<_>>();
        if counted.len() > MAX_BINDINGS {
            issues.push(warning(
                format!("A{}", counted[MAX_BINDINGS].row),
                "The device ignores binding rows after the first 128.",
            ));
        }

        validate_channel(sheet, &resources, &mut issues);

        let mut mode_numbers: HashMap<String, (i32, usize)> = HashMap::new();
        for binding in &sheet.bindings {
            if resources.is_preference_override(binding) {
                validate_preference_override(
                    binding,
                    decides_boot_mode,
                    &resources,
                    &mut issues,
                );
                if binding.input_cols.first() == Some(&2)
                    && let Some(value) = binding.inputs.first()
                    && let Ok(number) = value.trim().parse::<i32>()
                {
                    mode_numbers.insert(binding.output.clone(), (number, binding.row));
                }
                continue;
            }

            validate_output(binding, &resources, &mut issues);
            warn_about_reset(binding, &mut issues);
            validate_function(binding, &mut issues);
            validate_inputs(binding, &resources, &mut issues);
        }

        validate_preference_order(&mode_numbers, "C", &mut issues);
    }

    issues
}

fn validate_file_name(document: &ProfileDocument, issues: &mut Vec<Issue>) {
    let cell = format!("A{}", document.file_name_cell_row());
    let Some(name) = document.csv_file_name() else {
        issues.push(error(cell, "The first sheet is missing its CSV filename."));
        return;
    };
    if name.trim().is_empty() {
        issues.push(error(cell, "The first sheet is missing its CSV filename."));
        return;
    }

    if !name.to_ascii_lowercase().ends_with(".csv")
        || utf16_len(name) <= 4
        || name.chars().any(is_invalid_filename_char)
    {
        issues.push(error(
            cell.clone(),
            "The profile filename is not safe on supported systems.",
        ));
    }
    if is_reserved_windows_name(name) {
        issues.push(error(
            cell.clone(),
            "This filename resolves to a Windows device name.",
        ));
    }
    if utf16_len(name) > MAX_DEVICE_FILE_NAME_LENGTH {
        issues.push(error(
            cell.clone(),
            "The device filename slot holds at most 31 characters.",
        ));
    }
    if name.eq_ignore_ascii_case("prefs.csv") {
        issues.push(warning(
            cell.clone(),
            "prefs.csv is the device-wide preferences file.",
        ));
    }
    if document.is_default_config() {
        issues.push(warning(
            cell,
            "default.csv is the device fallback configuration.",
        ));
    }
}

fn is_invalid_filename_char(c: char) -> bool {
    matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' | ' ') || c.is_control()
}

fn is_reserved_windows_name(name: &str) -> bool {
    let first = name.split('.').next().unwrap_or(name);
    matches!(
        first.to_ascii_uppercase().as_str(),
        "CON"
            | "PRN"
            | "AUX"
            | "NUL"
            | "COM1"
            | "COM2"
            | "COM3"
            | "COM4"
            | "COM5"
            | "COM6"
            | "COM7"
            | "COM8"
            | "COM9"
            | "LPT1"
            | "LPT2"
            | "LPT3"
            | "LPT4"
            | "LPT5"
            | "LPT6"
            | "LPT7"
            | "LPT8"
            | "LPT9"
    )
}

fn validate_channel(sheet: &ModeSheet, resources: &Resources, issues: &mut Vec<Issue>) {
    if !sheet.channel.is_empty() && !CHANNELS.contains(&sheet.channel.as_str()) {
        issues.push(warning(
            format!("C{}", sheet.start_row + 2),
            "The device does not recognize this channel exactly.",
        ));
    }
    if sheet.channel == "both" {
        issues.push(warning(
            format!("C{}", sheet.start_row + 2),
            "The both channel requires newer firmware.",
        ));
    }

    if matches!(sheet.channel.as_str(), "bluetooth" | "none") {
        let affected = sheet
            .bindings
            .iter()
            .filter(|binding| {
                !resources.is_preference_override(binding)
                    && (binding.output.starts_with("mouse_") || binding.output.starts_with("kb_"))
            })
            .count();
        if affected > 0 {
            issues.push(warning(
                format!("C{}", sheet.start_row + 2),
                "Mouse and keyboard reports do not run on a non-USB channel in current firmware.",
            ));
        }
    }
}

fn validate_output(binding: &Binding, resources: &Resources, issues: &mut Vec<Issue>) {
    let cell = format!("A{}", binding.row);
    if binding.output.is_empty() {
        issues.push(warning(cell, "This binding row has no output."));
        return;
    }
    if resources.outputs.contains(binding.output.as_str()) {
        return;
    }
    if LEGACY_OUTPUTS.contains(&binding.output.as_str()) {
        issues.push(warning(cell, "This is a legacy firmware output alias."));
    } else {
        issues.push(warning(cell, "This output is not in the documented output vocabulary."));
    }
}

fn warn_about_reset(binding: &Binding, issues: &mut Vec<Issue>) {
    if binding.output == "reset_quadstick" {
        issues.push(warning(
            format!("A{}", binding.row),
            "reset_quadstick restarts the device and can enter the bootloader with push held.",
        ));
    }
}

fn validate_function(binding: &Binding, issues: &mut Vec<Issue>) {
    let parts = binding
        .function
        .split(' ')
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>();
    let cell = format!("B{}", binding.row);

    let Some(function) = parts.first().copied() else {
        issues.push(warning(
            cell,
            "An empty function cell falls back to normal on the device.",
        ));
        return;
    };

    let Some((_, maximum_args)) = function_arity(function) else {
        let _firmware_prefix = FIRMWARE_FUNCTION_ORDER
            .iter()
            .find(|candidate| binding.function.starts_with(*candidate));
        issues.push(warning(
            cell,
            "This function text does not exactly match the documented function name.",
        ));
        return;
    };

    let args = &parts[1..];
    if args.len() > maximum_args {
        issues.push(warning(
            cell.clone(),
            "The function has more parameters than firmware uses.",
        ));
    }

    for (index, argument) in args.iter().enumerate() {
        let Ok(number) = argument.parse::<i64>() else {
            issues.push(warning(
                cell.clone(),
                "Firmware converts this function parameter with atoi.",
            ));
            continue;
        };
        if number < 0 {
            issues.push(warning(
                cell.clone(),
                "Negative function parameters are not preserved by firmware semantics.",
            ));
            continue;
        }
        if index == 0 && number > FUNCTION_PARAMETER_CEILING {
            issues.push(warning(
                cell.clone(),
                "The first function parameter exceeds its 14-bit storage.",
            ));
            continue;
        }
        if let Some((minimum, maximum)) = function_parameter_bounds(function, index)
            && (number < minimum || number > maximum)
        {
            issues.push(warning(
                cell.clone(),
                "The function parameter is outside the proven firmware range.",
            ));
        }
    }
}

fn function_parameter_bounds(function: &str, index: usize) -> Option<(i64, i64)> {
    let ceiling = FUNCTION_PARAMETER_CEILING;
    match (function, index) {
        ("repeat", 0) => Some((1, 1_000)),
        ("repeat", 1) => Some((0, ceiling)),
        ("pulse", 0 | 1) => Some((1, ceiling)),
        ("duty", 0) => Some((1, ceiling)),
        ("greater_than", 0 | 1) => Some((1, 100)),
        ("less_than", 0) => Some((1, 100)),
        ("force_off" | "delayed_latch" | "delay_off", 0) => Some((1, ceiling)),
        ("delay_on" | "tap", 0 | 1) => Some((1, ceiling)),
        ("increment_value" | "decrement_value", 0) => Some((1, 100)),
        ("increment_value" | "decrement_value", 1) => Some((1, ceiling)),
        _ => None,
    }
}

fn validate_inputs(binding: &Binding, resources: &Resources, issues: &mut Vec<Issue>) {
    for (index, input) in binding.inputs.iter().enumerate() {
        if resources.validation.inputs.iter().any(|known| known == input) || input == NONE_INPUT {
            continue;
        }
        let column = binding.input_cols.get(index).copied().unwrap_or(2);
        let cell = cell_ref(column, binding.row);
        if LEGACY_INPUTS.contains(&input.as_str()) {
            issues.push(warning(cell, "This is a legacy firmware input alias."));
        } else {
            issues.push(Issue::with_kind(
                Severity::Warning,
                cell,
                "This input is not in the documented input vocabulary.",
                "Choose a known input or leave it blank.",
                IssueKind::UnknownInput,
            ));
        }
    }
}

fn validate_preference_override(
    binding: &Binding,
    decides_boot_mode: bool,
    resources: &Resources,
    issues: &mut Vec<Issue>,
) {
    let value_in_c = if binding.input_cols.first() == Some(&2) {
        binding.inputs.first().map(String::as_str)
    } else {
        None
    };

    let Some(value) = value_in_c else {
        if !binding.function.is_empty() {
            issues.push(warning(
                format!("B{}", binding.row),
                "A mode preference keeps its value in column C, not B.",
            ));
        } else {
            issues.push(warning(
                format!("C{}", binding.row),
                "This mode preference has no value in column C.",
            ));
        }
        return;
    };

    let mut rejected = false;
    match value.parse::<i64>() {
        Ok(number) if i32::try_from(number).is_err() => {
            issues.push(error(
                format!("C{}", binding.row),
                "The device reads this preference through a 32-bit integer.",
            ));
            rejected = true;
        }
        Ok(_) => {}
        Err(_) => {
            issues.push(error(
                format!("C{}", binding.row),
                "A mode preference value is read with atoi and must be a whole number.",
            ));
            rejected = true;
        }
    }

    if let Some(definition) = resources.preference(&binding.output) {
        validate_against_catalog(
            definition,
            value,
            &format!("C{}", binding.row),
            rejected,
            issues,
        );
    }
    special_preference_warnings(
        &binding.output,
        value,
        &format!("C{}", binding.row),
        decides_boot_mode,
        issues,
    );
}

fn validate_preferences_sheet(
    sheet: &ModeSheet,
    decides_boot_mode: bool,
    resources: &Resources,
    issues: &mut Vec<Issue>,
) {
    let mut numbers: HashMap<String, (i32, usize)> = HashMap::new();

    for binding in &sheet.bindings {
        if binding.output.is_empty() {
            continue;
        }
        let value = binding.function.as_str();
        let value_in_c = if binding.input_cols.first() == Some(&2) {
            binding.inputs.first().map(String::as_str)
        } else {
            None
        };
        let definition = resources.preference(&binding.output);

        if definition.is_none() {
            issues.push(warning(
                format!("A{}", binding.row),
                "The current QuadStick preference catalog does not contain this name.",
            ));
        }

        if value.is_empty() {
            let message = if value_in_c.is_some() {
                "Preferences sheets keep the value in column B, but it is in C."
            } else {
                "This preference has no value in column B."
            };
            issues.push(warning(format!("B{}", binding.row), message));
            continue;
        }

        let is_word_valued = is_word_valued_preference(&binding.output);
        let parsed_i64 = value.parse::<i64>();
        let mut rejected = false;
        if parsed_i64.is_err() && !is_word_valued {
            issues.push(error(
                format!("B{}", binding.row),
                "This preference is read as a whole number by firmware.",
            ));
            rejected = true;
        } else if !is_word_valued
            && parsed_i64
                .ok()
                .is_some_and(|number| i32::try_from(number).is_err())
        {
            issues.push(error(
                format!("B{}", binding.row),
                "The device reads this preference through a 32-bit integer.",
            ));
            rejected = true;
        }

        if let Ok(number) = value.parse::<i32>() {
            numbers.insert(binding.output.clone(), (number, binding.row));
        }

        if let Some(definition) = definition {
            validate_against_catalog(
                definition,
                value,
                &format!("B{}", binding.row),
                rejected,
                issues,
            );
        }
        special_preference_warnings(
            &binding.output,
            value,
            &format!("B{}", binding.row),
            decides_boot_mode,
            issues,
        );
    }

    validate_preference_order(&numbers, "B", issues);
}

fn validate_against_catalog(
    definition: &PreferenceDefinition,
    value: &str,
    cell: &str,
    already_rejected: bool,
    issues: &mut Vec<Issue>,
) {
    match definition.editor {
        PreferenceEditor::Integer => {
            let Ok(number) = value.parse::<i64>() else {
                return;
            };
            if definition.minimum.is_some_and(|minimum| number < i64::from(minimum))
                || definition.maximum.is_some_and(|maximum| number > i64::from(maximum))
            {
                issues.push(warning(
                    cell.to_owned(),
                    "The value is outside the range proven by the official manager.",
                ));
            }
        }
        PreferenceEditor::Toggle => {
            if !already_rejected && value != "0" && value != "1" {
                issues.push(warning(
                    cell.to_owned(),
                    "A toggle value should be 0 or 1.",
                ));
            }
        }
        PreferenceEditor::Choice => {
            if already_rejected || definition.options.iter().any(|option| option == value) {
                return;
            }
            if definition.firmware_may_add_more {
                issues.push(warning(
                    cell.to_owned(),
                    "This choice is newer than the values known to this app.",
                ));
            } else {
                issues.push(error(
                    cell.to_owned(),
                    "This choice is not one of the firmware keyword values.",
                ));
            }
        }
        PreferenceEditor::Text => {}
    }
}

fn special_preference_warnings(
    name: &str,
    value: &str,
    cell: &str,
    decides_boot_mode: bool,
    issues: &mut Vec<Issue>,
) {
    if matches!(
        name,
        "enable_auto_zero"
            | "usb_2_dead_zone"
            | "joystick_warning"
            | "joystick_alarm"
            | "watchdog_disable"
    ) && value.parse::<i32>().is_ok_and(|number| number != 0)
    {
        issues.push(warning(
            cell.to_owned(),
            "Current firmware stores this setting but does not act on it.",
        ));
    }

    if matches!(
        (name, value.parse::<i32>().ok()),
        ("enable_DS3_emulation", Some(5)) | ("joystick_deflection_minimum", Some(0))
    ) {
        issues.push(warning(
            cell.to_owned(),
            "This value changed meaning between legacy and current firmware.",
        ));
    }

    if name == "enable_DS3_emulation"
        && value
            .parse::<i32>()
            .is_ok_and(|mode| matches!(mode, 1 | 5 | 6 | 7))
    {
        issues.push(if decides_boot_mode {
            error(
                cell.to_owned(),
                "This boot USB emulation removes the mass-storage interface.",
            )
        } else {
            warning(
                cell.to_owned(),
                "This USB emulation removes the mass-storage interface while the mode is active.",
            )
        });
    }
}

fn is_word_valued_preference(name: &str) -> bool {
    matches!(
        name,
        "bluetooth_device_mode" | "bluetooth_connection_mode" | "bluetooth_remote_address"
    )
}

const ORDERED_PAIRS: [(&str, &str, i32); 4] = [
    ("sip_puff_threshold_soft", "sip_puff_threshold", 2),
    ("sip_puff_threshold", "sip_puff_maximum", 2),
    ("lip_position_minimum", "lip_position_maximum", 5),
    ("joystick_D_Pad_inner", "joystick_D_Pad_outer", 2),
];

const SIP_PUFF_TRIOS: [(&str, &str, &str); 2] = [
    ("sip_threshold_soft", "sip_threshold", "sip_maximum"),
    ("puff_threshold_soft", "puff_threshold", "puff_maximum"),
];

fn validate_preference_order(
    numbers: &HashMap<String, (i32, usize)>,
    value_column: &str,
    issues: &mut Vec<Issue>,
) {
    for (lower, upper, gap) in ORDERED_PAIRS {
        let (Some(&(lower_value, _)), Some(&(upper_value, upper_row))) =
            (numbers.get(lower), numbers.get(upper))
        else {
            continue;
        };
        if i64::from(lower_value) + i64::from(gap) > i64::from(upper_value) {
            issues.push(warning(
                format!("{value_column}{upper_row}"),
                "These preference values are too close or reversed.",
            ));
        }
    }

    for (soft, hard, maximum) in SIP_PUFF_TRIOS {
        check_sip_puff_pair(numbers, value_column, soft, hard, issues);
        check_sip_puff_pair(numbers, value_column, hard, maximum, issues);
    }
}

fn check_sip_puff_pair(
    numbers: &HashMap<String, (i32, usize)>,
    value_column: &str,
    lower_name: &str,
    upper_name: &str,
    issues: &mut Vec<Issue>,
) {
    let Some((_, lower_value, _)) = effective_preference(numbers, lower_name) else {
        return;
    };
    let Some((_, upper_value, upper_row)) = effective_preference(numbers, upper_name) else {
        return;
    };
    if i64::from(lower_value) + 2 > i64::from(upper_value) {
        issues.push(warning(
            format!("{value_column}{upper_row}"),
            "The effective sip/puff thresholds need at least two points between them.",
        ));
    }
}

fn effective_preference(
    numbers: &HashMap<String, (i32, usize)>,
    own: &str,
) -> Option<(String, i32, usize)> {
    if let Some(&(value, row)) = numbers.get(own)
        && value != 0
    {
        return Some((own.to_owned(), value, row));
    }

    let shared = if own.ends_with("maximum") {
        "sip_puff_maximum"
    } else if own.ends_with("_soft") {
        "sip_puff_threshold_soft"
    } else {
        "sip_puff_threshold"
    };
    numbers
        .get(shared)
        .map(|&(value, row)| (shared.to_owned(), value, row))
}

fn cell_ref(column: usize, row: usize) -> String {
    let letter = char::from_u32(u32::from(b'A') + u32::try_from(column).expect("small grid column"))
        .expect("ASCII cell column");
    format!("{letter}{row}")
}

fn utf16_len(value: &str) -> usize {
    value.encode_utf16().count()
}

fn warning(cell: impl Into<String>, message: impl Into<String>) -> Issue {
    Issue::new(Severity::Warning, cell, message, "Review the value.")
}

fn error(cell: impl Into<String>, message: impl Into<String>) -> Issue {
    Issue::new(Severity::Error, cell, message, "Correct the value before installing.")
}

#[cfg(test)]
mod tests {
    use super::{MAX_BINDINGS, MAX_PROFILES, validate};
    use crate::issue::{IssueKind, Severity};
    use crate::model::{Binding, ModeSheet, ProfileDocument, SheetType};

    fn profile(filename: &str) -> ProfileDocument {
        let mut sheet = ModeSheet::new(SheetType::ProfileName);
        sheet.start_row = 1;
        sheet.csv_file_name = Some(filename.to_owned());
        sheet.channel = "usb".to_owned();
        ProfileDocument {
            sheets: vec![sheet],
            ..ProfileDocument::default()
        }
    }

    #[test]
    fn device_filename_limit_is_31_utf16_units() {
        let safe = format!("{}.csv", "a".repeat(27));
        let unsafe_name = format!("{}.csv", "a".repeat(28));
        assert!(!validate(&profile(&safe))
            .iter()
            .any(|issue| issue.severity == Severity::Error));
        assert!(validate(&profile(&unsafe_name))
            .iter()
            .any(|issue| issue.severity == Severity::Error && issue.cell == "A2"));
    }

    #[test]
    fn unknown_input_is_a_warning_with_machine_kind() {
        let mut document = profile("input.csv");
        document.sheets[0].bindings.push(Binding::new(
            4,
            "x",
            "normal",
            vec!["not_an_input".into()],
            vec![5],
        ));
        let issues = validate(&document);
        assert!(issues.iter().any(|issue| {
            issue.cell == "F4"
                && issue.severity == Severity::Warning
                && issue.kind == IssueKind::UnknownInput
        }));
    }

    #[test]
    fn mode_and_binding_limits_warn_at_first_ignored_item() {
        let mut document = profile("limits.csv");
        for index in 0..MAX_BINDINGS + 1 {
            document.sheets[0].bindings.push(Binding::new(
                index + 4,
                "x",
                "normal",
                Vec::new(),
                Vec::new(),
            ));
        }
        for index in 1..=MAX_PROFILES {
            let mut sheet = ModeSheet::new(SheetType::ProfileName);
            sheet.start_row = index + 200;
            sheet.channel = "usb".to_owned();
            document.sheets.push(sheet);
        }

        let issues = validate(&document);
        assert!(issues.iter().any(|issue| {
            issue.cell == format!("A{}", MAX_BINDINGS + 4)
                && issue.severity == Severity::Warning
        }));
        assert!(issues.iter().any(|issue| {
            issue.cell == "A216" && issue.severity == Severity::Warning
        }));
    }

    #[test]
    fn boot_emulation_that_removes_the_drive_is_an_error() {
        let mut document = profile("default.csv");
        document.sheets[0].bindings.push(Binding::new(
            4,
            "enable_DS3_emulation",
            "",
            vec!["7".into()],
            vec![2],
        ));
        assert!(validate(&document)
            .iter()
            .any(|issue| issue.cell == "C4" && issue.severity == Severity::Error));
    }

    #[test]
    fn first_function_parameter_is_14_bit_bounded() {
        let mut document = profile("function.csv");
        document.sheets[0].bindings.push(Binding::new(
            4,
            "x",
            "pulse 16384",
            vec!["lip".into()],
            vec![2],
        ));
        assert!(validate(&document)
            .iter()
            .any(|issue| issue.cell == "B4" && issue.severity == Severity::Warning));
    }
}
