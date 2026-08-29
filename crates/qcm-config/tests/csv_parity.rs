use std::{env, fs, path::PathBuf};

use qcm_config::csv::{Grid, parse, write};

fn repo(path: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .join(path)
}

#[test]
fn checked_in_csv_edge_fixtures_have_expected_grids_and_roundtrip() {
    let cases: [(&str, Grid); 3] = [
        ("csv-empty.csv", vec![]),
        (
            "csv-quoted.csv",
            vec![vec!["a,b".into(), "quote \"inside\"".into(), "tail".into()]],
        ),
        (
            "csv-crlf.csv",
            vec![vec!["a".into(), "b".into()], vec!["c".into(), "d".into()]],
        ),
    ];

    for (name, expected) in cases {
        let bytes = fs::read(repo(&format!("fixtures/profiles/{name}"))).expect("fixture must exist");
        let text = String::from_utf8(bytes.clone()).expect("CSV fixtures are UTF-8");
        let grid = parse(&text);
        assert_eq!(grid, expected, "parse mismatch for {name}");
        assert_eq!(write(&grid).as_bytes(), bytes, "writer mismatch for {name}");
    }
}

#[test]
fn csharp_csv_oracle_matches_every_edge_fixture() {
    let required = env::var_os("QCM_REQUIRE_CSV_ORACLE").is_some();
    for id in ["csv-empty", "csv-quoted", "csv-crlf"] {
        let artifact = repo(&format!("fixtures/oracle/{id}.csv-parity.txt"));
        if !artifact.exists() {
            assert!(!required, "required C# CSV oracle artifact is missing: {}", artifact.display());
            continue;
        }
        let expected = parse_oracle(&fs::read_to_string(&artifact).expect("read C# oracle artifact"));
        let fixture = repo(&format!("fixtures/profiles/{id}.csv"));
        let text = fs::read_to_string(&fixture).expect("read CSV fixture");
        let actual = parse(&text);
        assert_eq!(actual, expected.rows, "C# parse parity failed for {id}");
        assert_eq!(write(&actual).into_bytes(), expected.written, "C# write parity failed for {id}");
    }
}

struct CsvOracle {
    rows: Grid,
    written: Vec<u8>,
}

fn parse_oracle(text: &str) -> CsvOracle {
    let mut lines = text.lines();
    assert_eq!(lines.next(), Some("qcm-csv-parity-1"));
    let expected_rows: usize = lines
        .next()
        .and_then(|line| line.strip_prefix("rows="))
        .expect("rows header")
        .parse()
        .expect("numeric row count");
    let mut rows = Vec::new();
    let mut current: Option<Vec<String>> = None;
    let mut written = None;

    for line in lines {
        if line == "row" {
            assert!(current.is_none(), "nested row in CSV oracle");
            current = Some(Vec::new());
        } else if line == "endrow" {
            rows.push(current.take().expect("endrow without row"));
        } else if let Some(hex) = line.strip_prefix("cell=") {
            let bytes = decode_hex(hex);
            current
                .as_mut()
                .expect("cell outside row")
                .push(String::from_utf8(bytes).expect("oracle cell must be UTF-8"));
        } else if let Some(hex) = line.strip_prefix("write=") {
            written = Some(decode_hex(hex));
        } else {
            panic!("unknown CSV oracle line: {line}");
        }
    }

    assert!(current.is_none(), "unterminated row in CSV oracle");
    assert_eq!(rows.len(), expected_rows, "CSV oracle row count mismatch");
    CsvOracle {
        rows,
        written: written.expect("CSV oracle write bytes missing"),
    }
}

fn decode_hex(hex: &str) -> Vec<u8> {
    assert!(hex.len().is_multiple_of(2), "odd hex length");
    hex.as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let high = nibble(pair[0]);
            let low = nibble(pair[1]);
            (high << 4) | low
        })
        .collect()
}

fn nibble(byte: u8) -> u8 {
    match byte {
        b'0'..=b'9' => byte - b'0',
        b'a'..=b'f' => byte - b'a' + 10,
        _ => panic!("invalid lowercase hex byte"),
    }
}
