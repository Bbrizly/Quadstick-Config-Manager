//! A QuadStick console in memory, answering the way `Joystick/Console.c` does
//! on firmware 2373. Ported from `tests/QuadStick.Format.Tests/FakeQuadStick.cs`.
//! Each answer names the line it copies, so a test that passes here passes
//! against the parser the device runs, not a guess at it.

use std::collections::{BTreeMap, VecDeque};
use std::io::{self, Read, Write};

#[derive(Debug)]
pub struct FakeConsole {
    pub files: BTreeMap<String, String>,
    /// Insertion-ordered like the C# dictionary, so `pref` prints the same.
    pub prefs: Vec<(String, i32)>,
    pub commands: Vec<String>,
    pub longest_line: usize,
    /// Nothing on the other end of the port.
    pub silent: bool,
    /// Flash that drops the last byte of a chunk.
    pub corrupt_writes: bool,
    /// printf turning `\n` into `\r\n`.
    pub crlf_on_read: bool,
    line: Vec<u8>,
    out: VecDeque<u8>,
    writing: Option<String>,
    written: String,
}

impl Default for FakeConsole {
    fn default() -> Self {
        Self::new()
    }
}

impl FakeConsole {
    #[must_use]
    pub fn new() -> Self {
        Self {
            files: BTreeMap::new(),
            prefs: [
                ("sip_puff_threshold", 60),
                ("puff_threshold", 0),
                ("sip_threshold", 0),
                ("puff_maximum", 0),
                ("sip_maximum", 0),
            ]
            .into_iter()
            .map(|(k, v)| (k.to_owned(), v))
            .collect(),
            commands: Vec::new(),
            longest_line: 0,
            silent: false,
            corrupt_writes: false,
            crlf_on_read: true,
            line: Vec::new(),
            out: VecDeque::new(),
            writing: None,
            written: String::new(),
        }
    }

    #[must_use]
    pub fn pref(&self, name: &str) -> Option<i32> {
        self.prefs.iter().find(|(k, _)| k == name).map(|(_, v)| *v)
    }

    fn say(&mut self, text: &str) {
        self.out.extend(text.as_bytes());
    }

    fn answer(&mut self, line: String) {
        self.commands.push(line.clone());
        let (word, rest) = line.split_once(',').unwrap_or((&line, ""));
        match word {
            "build" => self.say("2373\n"), // :342
            "calibrate" => self.say("\nCalibrated.  Hope you weren't touching it.\n"),
            "pref" => {
                // :336-339
                self.say("\nCurrent Preference values\n\n");
                let prefs = self.prefs.clone();
                for (k, v) in prefs {
                    self.say(&format!(" {k}: {v}\n"));
                }
            }
            "load" => {
                // :259-266
                self.say(&format!("Console load command: {rest}\n"));
                if self.files.contains_key(rest) {
                    self.say(&format!("\nLoaded configuration file {rest} OK"));
                } else {
                    self.say("\n*** File ErrorCode: 4 ***");
                }
            }
            "read_file" => {
                // :381-400
                if let Some(text) = self.files.get(rest).cloned() {
                    let text = if self.crlf_on_read {
                        text.replace('\n', "\r\n")
                    } else {
                        text
                    };
                    self.say(&text);
                    self.say("**END OF FILE**\n");
                } else {
                    self.say("Open file ErrorCode: 4\n");
                }
            }
            "write_file" => {
                // :401-438
                let (mode, arg) = rest.split_once(',').unwrap_or((rest, ""));
                match mode {
                    "0" => {
                        self.writing = Some(arg.to_owned());
                        self.written.clear();
                    }
                    "1" => {
                        if self.corrupt_writes {
                            let mut cut = arg.to_owned();
                            cut.pop();
                            self.written.push_str(&cut);
                        } else {
                            self.written.push_str(arg);
                        }
                        self.say(&format!("{}\n>", arg.len()));
                        return; // :425, no blip
                    }
                    _ => {
                        if let Some(name) = self.writing.clone() {
                            self.files.insert(name, self.written.clone());
                        }
                        let n = self.written.chars().count();
                        self.say(&format!("{n} bytes written\n"));
                    }
                }
            }
            "delete_file" => {
                self.files.remove(rest);
                self.say(&format!("Deleted: {rest}\n"));
            }
            "boot" => return, // resets, never answers
            _ => {
                // A preference name: :568-580. Anything else is ignored.
                if let Some(slot) = self.prefs.iter_mut().find(|(k, _)| k == word) {
                    slot.1 = rest.parse().unwrap_or(slot.1);
                    self.say(&format!("\n{word:>25}: {rest}\n>"));
                }
                return;
            }
        }
        self.say("\n>"); // :540
    }
}

impl Read for FakeConsole {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let mut n = 0;
        while n < buf.len() {
            let Some(b) = self.out.pop_front() else { break };
            buf[n] = b;
            n += 1;
        }
        Ok(n)
    }
}

impl Write for FakeConsole {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        for &b in buf {
            if (b == b'\r' || b == b'\n') && self.line.is_empty() {
                continue; // :601
            }
            if b == 0x08 {
                self.line.clear(); // :606
                continue;
            }
            if b != b'\r' {
                self.line.push(b); // :607
                continue;
            }
            if self.line.len() > 529 {
                self.line.clear(); // :598, dropped
                continue;
            }
            self.longest_line = self.longest_line.max(self.line.len());
            let line = String::from_utf8_lossy(&self.line).into_owned();
            self.line.clear();
            if !self.silent {
                self.answer(line);
            }
        }
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
