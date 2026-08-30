use qcm_config::vocab::{
    LEGACY_INPUTS, LEGACY_OUTPUTS, NONE_INPUT, default_template, function_arity,
    functions_in_firmware_order, known_outputs, preference_overrides,
};
use qcm_config::{
    Issue, IssueKind, ProfileFile, Severity, SheetType, load_preferences, load_validation,
};
use serde_json::{Map, Value, json};
use std::collections::BTreeMap;
use std::env;
use std::fs;
use std::io::{self, Read};
use std::path::{Path, PathBuf};

const FIRST_INPUT_COL: usize = 2;
const MAX_INPUTS: usize = 8;

fn main() {
    let args = env::args().skip(1).collect::<Vec<_>>();
    let result = dispatch(&args);
    match result {
        Ok(code) => std::process::exit(code),
        Err(error) => {
            emit(&json!({ "ok": false, "error": error }));
            std::process::exit(2);
        }
    }
}

fn dispatch(args: &[String]) -> Result<i32, String> {
    let Some(command) = args.first().map(String::as_str) else {
        usage();
        return Ok(2);
    };
    match command {
        "inspect" => inspect(&args[1..]),
        "vocab" => vocab(),
        "validate" => validate_cmd(&args[1..]),
        "apply" => apply(&args[1..]),
        "diff" => diff(&args[1..]),
        _ => {
            usage();
            Ok(2)
        }
    }
}

fn usage() {
    eprintln!(
        "qsf inspect <file.csv>...\n\
         qsf vocab\n\
         qsf validate <file.csv>\n\
         qsf apply --from <file.csv>|--template <name.csv> --ops <file.json>|- --out <file.csv>\n\
         qsf diff <a.csv> <b.csv>"
    );
}

fn emit(value: &Value) {
    let mut value = value.clone();
    strip_null_properties(&mut value);
    println!(
        "{}",
        serde_json::to_string_pretty(&value).expect("qsf JSON values are serializable")
    );
}

fn strip_null_properties(value: &mut Value) {
    match value {
        Value::Object(map) => {
            map.retain(|_, value| !value.is_null());
            for value in map.values_mut() {
                strip_null_properties(value);
            }
        }
        Value::Array(items) => {
            for item in items {
                strip_null_properties(item);
            }
        }
        _ => {}
    }
}

fn inspect(paths: &[String]) -> Result<i32, String> {
    if paths.is_empty() {
        usage();
        return Ok(2);
    }
    let profiles = paths
        .iter()
        .map(|path| {
            let text = read_profile_text(path)?;
            let (profile, inserted) = open(&text);
            Ok(describe(path, &profile, inserted))
        })
        .collect::<Result<Vec<_>, String>>()?;
    emit(&json!({ "profiles": profiles }));
    Ok(0)
}

fn read_profile_text(path: &str) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|error| format!("{path}: {error}"))?;
    if bytes.starts_with(b"PK\x03\x04") {
        return Err(format!(
            "{path}: XLSX import is deferred until the Rust workbook importer exists"
        ));
    }
    String::from_utf8(bytes).map_err(|error| format!("{path}: file is not valid UTF-8: {error}"))
}

fn open(text: &str) -> (ProfileFile, usize) {
    let mut profile = ProfileFile::load(text);
    let before = profile.grid.len();
    let _ = profile.normalize_for_device_csv();
    let inserted = profile.grid.len().saturating_sub(before);
    (profile, inserted)
}

fn describe(path: &str, profile: &ProfileFile, rows_inserted: usize) -> Value {
    let document = &profile.document;
    let modes = document
        .sheets
        .iter()
        .enumerate()
        .map(|(index, sheet)| {
            let number = document.sheets[..=index]
                .iter()
                .filter(|candidate| candidate.sheet_type == SheetType::ProfileName)
                .count();
            let bindings = sheet
                .bindings
                .iter()
                .map(|binding| {
                    json!({
                        "row": binding.row,
                        "output": binding.output,
                        "function": binding.function,
                        "inputs": binding.inputs,
                        "action": (!binding.action_name.is_empty()).then_some(binding.action_name.as_str()),
                    })
                })
                .collect::<Vec<_>>();
            json!({
                "index": index,
                "type": sheet_type_name(sheet.sheet_type),
                "number": number,
                "name": sheet.mode_name,
                "channel": sheet.channel,
                "label": sheet.header_label,
                "startRow": sheet.start_row,
                "bindings": bindings,
            })
        })
        .collect::<Vec<_>>();
    json!({
        "path": path,
        "skippedTabs": [],
        "limitation": Value::Null,
        "rowsInsertedForDevice": rows_inserted,
        "title": document.title(),
        "csvFileName": document.csv_file_name(),
        "headerVersion": document.has_version_header.then_some(document.header_version.as_str()),
        "modes": modes,
        "issues": profile.issues.iter().map(issue_out).collect::<Vec<_>>(),
    })
}

fn issue_out(issue: &Issue) -> Value {
    json!({
        "severity": match issue.severity { Severity::Error => "error", Severity::Warning => "warning" },
        "cell": issue.cell,
        "message": issue.message,
        "fix": issue.fix,
        "kind": match issue.kind { IssueKind::None => "None", IssueKind::UnknownInput => "UnknownInput" },
    })
}

fn vocab() -> Result<i32, String> {
    let validation = load_validation()?;
    let preferences = load_preferences()?;
    let mut inputs = validation.inputs.clone();
    inputs.sort();
    let mut ps3 = validation.outputs_ps3.clone();
    ps3.sort();
    let mut xbox = validation.outputs_xbox.clone();
    xbox.sort();
    let functions = functions_in_firmware_order(&validation)
        .into_iter()
        .map(|name| {
            let (min, max) = function_arity(&name).unwrap_or((0, 0));
            json!({ "name": name, "minParams": min, "maxParams": max })
        })
        .collect::<Vec<_>>();
    emit(&json!({
        "inputs": inputs,
        "outputs": { "ps3": ps3, "xbox": xbox },
        "preferenceOverrides": preference_overrides(&preferences).into_iter().collect::<Vec<_>>(),
        "functions": functions,
        "legacyInputs": LEGACY_INPUTS,
    }));
    Ok(0)
}

fn validate_cmd(paths: &[String]) -> Result<i32, String> {
    let Some(path) = paths.first() else {
        usage();
        return Ok(2);
    };
    let text = read_profile_text(path)?;
    let (profile, inserted) = open(&text);
    let errors = profile
        .issues
        .iter()
        .filter(|issue| issue.severity == Severity::Error)
        .count();
    let warnings = profile
        .issues
        .iter()
        .filter(|issue| issue.severity == Severity::Warning)
        .count();
    emit(&json!({
        "path": path,
        "rowsInsertedForDevice": inserted,
        "ok": errors == 0,
        "errors": errors,
        "warnings": warnings,
        "issues": profile.issues.iter().map(issue_out).collect::<Vec<_>>(),
    }));
    Ok(if errors == 0 { 0 } else { 1 })
}

fn apply(args: &[String]) -> Result<i32, String> {
    let mut from = None;
    let mut template = None;
    let mut ops_path = None;
    let mut out_path = None;
    let mut index = 0;
    while index + 1 < args.len() {
        match args[index].as_str() {
            "--from" => from = Some(args[index + 1].clone()),
            "--template" => template = Some(args[index + 1].clone()),
            "--ops" => ops_path = Some(args[index + 1].clone()),
            "--out" => out_path = Some(args[index + 1].clone()),
            _ => {
                index += 1;
                continue;
            }
        }
        index += 2;
    }
    let Some(ops_path) = ops_path else {
        usage();
        return Ok(2);
    };
    let Some(out_path) = out_path else {
        usage();
        return Ok(2);
    };
    if from.is_none() && template.is_none() {
        usage();
        return Ok(2);
    }

    let source = if let Some(path) = from {
        read_profile_text(&path)?
    } else {
        let mut profile = ProfileFile::load(default_template());
        let filename = template.expect("checked above");
        let row = profile.document.file_name_cell_row();
        let _ = profile.set_cell(row, 0, filename);
        profile.to_csv_text()
    };
    let (mut profile, _) = open(&source);

    let ops_text = if ops_path == "-" {
        let mut input = String::new();
        io::stdin()
            .read_to_string(&mut input)
            .map_err(|error| format!("read stdin ops: {error}"))?;
        input
    } else {
        fs::read_to_string(&ops_path).map_err(|error| format!("{ops_path}: {error}"))?
    };
    let operations = serde_json::from_str::<Value>(&ops_text)
        .map_err(|error| format!("ops JSON is invalid: {error}"))?;
    let operations = operations
        .as_array()
        .ok_or_else(|| "ops must be a JSON array".to_owned())?;

    let mut applied = Vec::new();
    let mut rejected = Vec::new();
    for (op_index, operation) in operations.iter().enumerate() {
        let object = operation
            .as_object()
            .ok_or_else(|| format!("op {op_index} is not an object"))?;
        apply_one(&mut profile, op_index, object, &mut applied, &mut rejected)?;
    }

    let errors = profile
        .issues
        .iter()
        .filter(|issue| issue.severity == Severity::Error)
        .count();
    let warnings = profile
        .issues
        .iter()
        .filter(|issue| issue.severity == Severity::Warning)
        .count();
    let ok = rejected.is_empty() && errors == 0;
    let mut shifted = 0;
    if ok {
        let before = profile.grid.len();
        let _ = profile.normalize_for_device_csv();
        shifted = profile.grid.len().saturating_sub(before);
        write_atomic(Path::new(&out_path), &profile.to_csv_text())?;
    }
    emit(&json!({
        "ok": ok,
        "wrote": ok.then_some(out_path),
        "rowsInsertedAtWrite": shifted,
        "applied": applied,
        "rejected": rejected,
        "errors": errors,
        "warnings": warnings,
        "issues": profile.issues.iter().map(issue_out).collect::<Vec<_>>(),
    }));
    Ok(if ok { 0 } else { 1 })
}

fn apply_one(
    profile: &mut ProfileFile,
    index: usize,
    op: &Map<String, Value>,
    applied: &mut Vec<Value>,
    rejected: &mut Vec<Value>,
) -> Result<(), String> {
    let kind = string(op, "op")?;
    let why = op.get("why").and_then(Value::as_str);
    let mut accept = |detail: Value| {
        applied.push(json!({ "index": index, "op": kind, "why": why, "detail": detail }));
    };
    let mut reject = |reason: String| {
        rejected.push(json!({ "index": index, "op": kind, "why": why, "reason": reason }));
    };

    match kind {
        "set_filename" => {
            let row = profile.document.file_name_cell_row();
            let _ = profile.set_cell(row, 0, string(op, "name")?.to_owned());
            accept(Value::Null);
        }
        "add_mode" => {
            let Some(mode) = profile.add_mode_sheet(string(op, "name")?) else {
                reject("could not add mode".to_owned());
                return Ok(());
            };
            let _ = profile.normalize_for_device_csv();
            accept(json!({ "mode": mode }));
        }
        "rename_mode" => {
            let mode = integer(op, "mode")?;
            if profile.rename_mode(mode, string(op, "name")?) {
                accept(Value::Null);
            } else {
                reject("mode index out of range, or the name is unchanged".to_owned());
            }
        }
        "add_row" => {
            let mode = integer(op, "mode")?;
            if let Some(row) = profile.add_binding_row(mode) {
                accept(json!({ "row": row }));
            } else {
                reject(format!("no mode at index {mode}"));
            }
        }
        "delete_row" => {
            let _ = profile.delete_row(integer(op, "row")?);
            accept(Value::Null);
        }
        "set_action" => {
            let row = integer(op, "row")?;
            let name = string(op, "name")?;
            let token = profile.get_cell(row, 0).trim().to_owned();
            if token.is_empty() {
                reject(format!("row {row} has no output to name"));
            } else if profile.set_output(row, &token, name) {
                accept(json!({ "cell": a1(row, 11) }));
            } else {
                reject(format!(
                    "'{name}' cannot name row {row}: 1 to {} characters, not an output name, and not already used here",
                    qcm_config::MAX_ACTION_NAME
                ));
            }
        }
        "set_cell" => {
            let _ = profile.set_cell(
                integer(op, "row")?,
                integer(op, "col")?,
                string(op, "value")?.to_owned(),
            );
            accept(Value::Null);
        }
        "set_binding" => {
            let row = integer(op, "row")?;
            let output = string(op, "output")?;
            let function = op
                .get("function")
                .and_then(Value::as_str)
                .unwrap_or("normal");
            let inputs = op
                .get("inputs")
                .and_then(Value::as_array)
                .map(|items| {
                    items
                        .iter()
                        .map(|value| {
                            value
                                .as_str()
                                .map(str::to_owned)
                                .ok_or_else(|| "binding input is not a string".to_owned())
                        })
                        .collect::<Result<Vec<_>, String>>()
                })
                .transpose()?
                .unwrap_or_default();
            let action = op.get("action").and_then(Value::as_str).unwrap_or("");
            if let Some(reason) = reject_binding(profile, row, output, function, &inputs, action)? {
                reject(reason);
                return Ok(());
            }
            let _ = profile.set_output(row, output, action);
            let _ = profile.set_cell(row, 1, function.to_owned());
            for col in 0..MAX_INPUTS {
                let value = inputs.get(col).cloned().unwrap_or_default();
                let _ = profile.set_cell(row, FIRST_INPUT_COL + col, value);
            }
            if let Some(note) = op.get("note").and_then(Value::as_str) {
                let _ = profile.set_cell(row, qcm_config::NOTE_COLUMN, note.to_owned());
            }
            accept(json!({ "cell": a1(row, 0) }));
        }
        _ => reject(format!("unknown op '{kind}'")),
    }
    Ok(())
}

fn reject_binding(
    profile: &ProfileFile,
    row: usize,
    output: &str,
    function: &str,
    inputs: &[String],
    action: &str,
) -> Result<Option<String>, String> {
    if row < 1 {
        return Ok(Some(format!("row {row} is not a row")));
    }
    let Some(sheet) = profile
        .document
        .sheets
        .iter()
        .rev()
        .find(|sheet| sheet.start_row <= row)
    else {
        return Ok(Some(format!(
            "row {row} is above the first mode, so it is not part of any sheet"
        )));
    };
    if row <= sheet.start_row + 2 {
        return Ok(Some(format!(
            "row {row} is one of the '{}' sheet's header rows; add_row first, then bind the row it hands back",
            sheet.mode_name
        )));
    }
    if sheet.sheet_type != SheetType::ProfileName {
        return Ok(Some(format!(
            "row {row} is in a {} sheet, which holds settings rather than bindings",
            sheet_type_name(sheet.sheet_type)
        )));
    }

    let validation = load_validation()?;
    let preferences = load_preferences()?;
    let outputs = known_outputs(&validation);
    let overrides = preference_overrides(&preferences);
    if !outputs.contains(output) && !overrides.contains(output) && !LEGACY_OUTPUTS.contains(&output)
    {
        return Ok(Some(format!(
            "'{output}' is not an output the device knows (case sensitive). See qsf vocab."
        )));
    }

    let parts = function.split_whitespace().collect::<Vec<_>>();
    let Some(name) = parts.first() else {
        return Ok(Some(
            "function is empty; 'normal' is the plain one".to_owned(),
        ));
    };
    let Some((_, max)) = function_arity(name) else {
        return Ok(Some(format!(
            "'{name}' is not a function the device knows. See qsf vocab."
        )));
    };
    if parts.len().saturating_sub(1) > max {
        return Ok(Some(format!(
            "'{name}' takes at most {max} parameter(s), got {}",
            parts.len() - 1
        )));
    }
    for parameter in &parts[1..] {
        if parameter.is_empty()
            || !parameter.bytes().all(|byte| byte.is_ascii_digit())
            || parameter.parse::<i64>().is_err()
        {
            return Ok(Some(format!(
                "'{parameter}' is not a whole number, and the device reads '{name}' parameters as whole numbers, so it would run as something other than what this says"
            )));
        }
    }
    if inputs.len() > MAX_INPUTS {
        return Ok(Some(format!(
            "a row holds at most {MAX_INPUTS} inputs, got {}",
            inputs.len()
        )));
    }
    if !overrides.contains(output) {
        for input in inputs {
            if input != NONE_INPUT
                && !validation.inputs.iter().any(|known| known == input)
                && !LEGACY_INPUTS.contains(&input.as_str())
            {
                return Ok(Some(format!(
                    "'{input}' is not an input the device knows (case sensitive). See qsf vocab."
                )));
            }
        }
    }
    if action.is_empty() {
        return Ok(None);
    }
    if !ProfileFile::is_legal_action_name(action) {
        return Ok(Some(format!(
            "'{action}' cannot be an action name: 1 to {} characters, and not the name of an output",
            qcm_config::MAX_ACTION_NAME
        )));
    }
    let wanted = action.to_lowercase();
    let mut tokens = BTreeMap::<String, String>::new();
    for binding in profile
        .document
        .sheets
        .iter()
        .filter(|sheet| sheet.sheet_type == SheetType::ProfileName)
        .flat_map(|sheet| &sheet.bindings)
    {
        if !binding.action_name.is_empty() {
            tokens
                .entry(binding.action_name.to_lowercase())
                .or_insert_with(|| binding.output.clone());
        }
    }
    if let Some(existing) = tokens.get(&wanted)
        && existing != output
    {
        return Ok(Some(format!(
            "'{action}' already means '{existing}' in this profile; one name, one output"
        )));
    }
    Ok(None)
}

fn diff(args: &[String]) -> Result<i32, String> {
    if args.len() < 2 {
        usage();
        return Ok(2);
    }
    let (left, _) = open(&read_profile_text(&args[0])?);
    let (right, _) = open(&read_profile_text(&args[1])?);
    let mut changes = Vec::new();
    let rows = left.grid.len().max(right.grid.len());
    for row in 0..rows {
        let a = left.grid.get(row).map(Vec::as_slice).unwrap_or(&[]);
        let b = right.grid.get(row).map(Vec::as_slice).unwrap_or(&[]);
        let cols = a.len().max(b.len());
        for col in 0..cols {
            let from = a.get(col).map_or("", |value| value.trim());
            let to = b.get(col).map_or("", |value| value.trim());
            if from != to {
                changes.push(json!({
                    "cell": a1(row + 1, col),
                    "row": row + 1,
                    "col": col,
                    "from": from,
                    "to": to,
                }));
            }
        }
    }
    emit(&json!({ "a": args[0], "b": args[1], "changes": changes }));
    Ok(0)
}

fn write_atomic(path: &Path, text: &str) -> Result<(), String> {
    let mut tmp = PathBuf::from(path);
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| format!("invalid output path: {}", path.display()))?;
    tmp.set_file_name(format!("{file_name}.qscm-tmp"));
    fs::write(&tmp, text).map_err(|error| format!("{}: {error}", tmp.display()))?;
    let result = if path.exists() {
        fs::remove_file(path)
            .and_then(|()| fs::rename(&tmp, path))
            .map_err(|error| format!("{}: {error}", path.display()))
    } else {
        fs::rename(&tmp, path).map_err(|error| format!("{}: {error}", path.display()))
    };
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

fn string<'a>(object: &'a Map<String, Value>, key: &str) -> Result<&'a str, String> {
    object
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("op is missing '{key}'"))
}

fn integer(object: &Map<String, Value>, key: &str) -> Result<usize, String> {
    object
        .get(key)
        .and_then(Value::as_u64)
        .and_then(|value| usize::try_from(value).ok())
        .ok_or_else(|| format!("op is missing '{key}'"))
}

fn sheet_type_name(sheet_type: SheetType) -> &'static str {
    match sheet_type {
        SheetType::ProfileName => "ProfileName",
        SheetType::Preferences => "Preferences",
        SheetType::Infrared => "Infrared",
    }
}

fn a1(row: usize, col: usize) -> String {
    let mut name = String::new();
    let mut n = col;
    loop {
        name.insert(
            0,
            char::from(b'A' + u8::try_from(n % 26).expect("column remainder")),
        );
        if n < 26 {
            break;
        }
        n = n / 26 - 1;
    }
    format!("{name}{row}")
}
