//! Exact port of the legacy `QuadStick.Format.Csv` reader/writer.
//!
//! This is intentionally a tiny CSV implementation rather than a standards
//! library. Compatibility means preserving the legacy parser's behavior,
//! including ignoring carriage returns outside quotes and only ending rows on
//! line feeds.

/// Lossless row/cell grid used by the QuadStick profile layer.
pub type Grid = Vec<Vec<String>>;

/// Parse text using the exact state machine used by the frozen C# `Csv.Parse`.
#[must_use]
pub fn parse(text: &str) -> Grid {
    let chars: Vec<char> = text.chars().collect();
    let mut rows = Vec::new();
    let mut row = Vec::new();
    let mut field = String::new();
    let mut in_quotes = false;
    let mut i = 0;

    while i < chars.len() {
        let c = chars[i];
        if in_quotes {
            if c == '"' {
                if i + 1 < chars.len() && chars[i + 1] == '"' {
                    field.push('"');
                    i += 1;
                } else {
                    in_quotes = false;
                }
            } else {
                field.push(c);
            }
        } else {
            match c {
                '"' => in_quotes = true,
                ',' => {
                    row.push(std::mem::take(&mut field));
                }
                '\r' => {}
                '\n' => {
                    row.push(std::mem::take(&mut field));
                    rows.push(std::mem::take(&mut row));
                }
                _ => field.push(c),
            }
        }
        i += 1;
    }

    if !field.is_empty() || !row.is_empty() {
        row.push(field);
        rows.push(row);
    }
    rows
}

/// Write a grid using the exact quoting and CRLF rules of legacy `Csv.Write`.
#[must_use]
pub fn write(rows: &[Vec<String>]) -> String {
    let mut output = String::new();
    for row in rows {
        for (index, field) in row.iter().enumerate() {
            if index > 0 {
                output.push(',');
            }
            if needs_quotes(field) {
                output.push('"');
                for c in field.chars() {
                    if c == '"' {
                        output.push('"');
                    }
                    output.push(c);
                }
                output.push('"');
            } else {
                output.push_str(field);
            }
        }
        output.push_str("\r\n");
    }
    output
}

fn needs_quotes(field: &str) -> bool {
    field.chars().any(|c| matches!(c, ',' | '"' | '\n' | '\r'))
}

#[cfg(test)]
mod tests {
    use super::{parse, write};

    #[test]
    fn edge_semantics_match_legacy_state_machine() {
        assert!(parse("").is_empty());
        assert_eq!(parse("\n"), vec![vec![""]]);
        assert_eq!(parse(","), vec![vec!["", ""]]);
        assert_eq!(parse("a,b"), vec![vec!["a", "b"]]);
        assert_eq!(parse("a,b\n"), vec![vec!["a", "b"]]);
        assert_eq!(parse("a\rb\r\n"), vec![vec!["ab"]]);
        assert_eq!(parse("\"a\r\nb\",c\n"), vec![vec!["a\r\nb", "c"]]);
        assert_eq!(parse("\"a\"\"b\",c"), vec![vec!["a\"b", "c"]]);
        assert_eq!(parse("héllo,世界"), vec![vec!["héllo", "世界"]]);
    }

    #[test]
    fn writer_matches_legacy_quoting_and_crlf() {
        let rows = vec![
            vec!["plain".to_owned(), "a,b".to_owned(), "a\"b".to_owned()],
            vec!["a\nb".to_owned(), "a\rb".to_owned(), String::new()],
        ];
        assert_eq!(
            write(&rows),
            "plain,\"a,b\",\"a\"\"b\"\r\n\"a\nb\",\"a\rb\",\r\n"
        );
    }

    #[test]
    fn deterministic_fuzzish_inputs_never_panic() {
        let alphabet = ['a', 'Z', '0', ',', '"', '\r', '\n', 'é', '中', '\0'];
        let mut state = 0x9e37_79b9_u32;
        for _ in 0..2_000 {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            let len = (state % 96) as usize;
            let mut sample = String::new();
            for _ in 0..len {
                state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                sample.push(alphabet[(state as usize) % alphabet.len()]);
            }
            let parsed = parse(&sample);
            let _ = write(&parsed);
        }
    }
}
