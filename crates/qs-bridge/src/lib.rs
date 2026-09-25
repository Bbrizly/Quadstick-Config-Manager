//! Decode a QuadStick x360ce report (emulation mode 2) into one pad state.
//!
//! The layout is `USB_X360CE_Report_Data_t` in FW 2373 `Joystick/Descriptors.h:573`,
//! filled at `DataFlow.c:2713`. The report has no report ID, so byte 0 is left X.

#[cfg(target_os = "linux")]
pub mod uinput;

pub const VENDOR_ID: u16 = 0x16D0;
/// Mode 2 only. `Descriptors.c:173`.
pub const PRODUCT_ID_X360CE: u16 = 0x092C;

/// Sticks follow the Linux gamepad convention: centre 0, right and down positive.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Pad {
    pub left_x: i16,
    pub left_y: i16,
    pub right_x: i16,
    pub right_y: i16,
    pub left_trigger: u8,
    pub right_trigger: u8,
    pub buttons: Buttons,
    /// -1 left, 1 right.
    pub hat_x: i8,
    /// -1 up, 1 down.
    pub hat_y: i8,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Buttons {
    pub a: bool,
    pub b: bool,
    pub x: bool,
    pub y: bool,
    pub lb: bool,
    pub rb: bool,
    pub back: bool,
    pub start: bool,
    pub guide: bool,
    pub left_stick: bool,
    pub right_stick: bool,
}

/// The shortest report that holds every field the pad uses.
pub const REPORT_LEN: usize = 14;

pub fn decode(report: &[u8]) -> Option<Pad> {
    if report.len() < REPORT_LEN {
        return None;
    }
    let axis = |i: usize| u16::from_le_bytes([report[i], report[i + 1]]);
    // The firmware sends 0 to 0xFFFF with 0x8000 at rest, down and right high.
    let stick = |i: usize| (i32::from(axis(i)) - 0x8000) as i16;
    let trigger = |i: usize| (axis(i) >> 8) as u8;
    let face = report[12];
    let rest = report[13];
    let bit = |byte: u8, n: u8| byte & (1 << n) != 0;
    // Firmware names are PlayStation ones: square, X, O, triangle. Mode 3 at
    // DataFlow.c:2764 maps them to Xbox X, A, B, Y, and so does this.
    let buttons = Buttons {
        x: bit(face, 0),
        a: bit(face, 1),
        b: bit(face, 2),
        y: bit(face, 3),
        lb: bit(face, 4),
        rb: bit(face, 5),
        // QMP (ViGEmBus.py:365) reads bit 6 as Start. The firmware puts select there.
        back: bit(face, 6),
        start: bit(face, 7),
        left_stick: bit(rest, 0),
        right_stick: bit(rest, 1),
        guide: bit(rest, 2),
    };
    // Hat 0 is N, clockwise to 7 NW. 15 is idle (ps3.h:19); the descriptor
    // declares a null state, so anything past 7 is released.
    let (hat_x, hat_y) = match (rest >> 3) & 0x0F {
        0 => (0, -1),
        1 => (1, -1),
        2 => (1, 0),
        3 => (1, 1),
        4 => (0, 1),
        5 => (-1, 1),
        6 => (-1, 0),
        7 => (-1, -1),
        _ => (0, 0),
    };
    Some(Pad {
        left_x: stick(0),
        left_y: stick(2),
        right_x: stick(4),
        right_y: stick(6),
        left_trigger: trigger(8),
        right_trigger: trigger(10),
        buttons,
        hat_x,
        hat_y,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A report at rest: sticks centred, triggers out, hat idle.
    fn rest() -> [u8; 16] {
        let mut r = [0u8; 16];
        for i in [0, 2, 4, 6] {
            r[i..i + 2].copy_from_slice(&0x8000u16.to_le_bytes());
        }
        r[13] = 15 << 3;
        r
    }

    #[test]
    fn rest_is_neutral() {
        assert_eq!(decode(&rest()), Some(Pad::default()));
    }

    #[test]
    fn short_report_is_rejected() {
        assert_eq!(decode(&rest()[..REPORT_LEN - 1]), None);
        assert!(decode(&rest()[..REPORT_LEN]).is_some());
    }

    #[test]
    fn stick_extremes() {
        // DataFlow.c:2715: byte << 8, with 0xFF00 raised to 0xFFFF.
        for (raw, want) in [
            (0x0000u16, i16::MIN),
            (0x8000, 0),
            (0xFFFF, i16::MAX),
            (0x7F00, -256),
        ] {
            for offset in [0, 2, 4, 6] {
                let mut r = rest();
                r[offset..offset + 2].copy_from_slice(&raw.to_le_bytes());
                let p = decode(&r).unwrap();
                let got = [p.left_x, p.left_y, p.right_x, p.right_y][offset / 2];
                assert_eq!(got, want, "raw {raw:#06x} at byte {offset}");
            }
        }
    }

    #[test]
    fn up_on_the_stick_is_negative() {
        let mut r = rest();
        r[2..4].copy_from_slice(&0u16.to_le_bytes());
        assert_eq!(decode(&r).unwrap().left_y, i16::MIN);
    }

    #[test]
    fn triggers() {
        for (raw, want) in [(0u16, 0u8), (0x8000, 0x80), (0xFFFF, 0xFF)] {
            let mut r = rest();
            r[8..10].copy_from_slice(&raw.to_le_bytes());
            r[10..12].copy_from_slice(&raw.to_le_bytes());
            let p = decode(&r).unwrap();
            assert_eq!((p.left_trigger, p.right_trigger), (want, want));
        }
    }

    #[test]
    fn each_button_bit() {
        let set = |byte: usize, bit: u8| {
            let mut r = rest();
            r[byte] |= 1 << bit;
            decode(&r).unwrap().buttons
        };
        let one = |f: fn(&mut Buttons)| {
            let mut b = Buttons::default();
            f(&mut b);
            b
        };
        assert_eq!(set(12, 0), one(|b| b.x = true));
        assert_eq!(set(12, 1), one(|b| b.a = true));
        assert_eq!(set(12, 2), one(|b| b.b = true));
        assert_eq!(set(12, 3), one(|b| b.y = true));
        assert_eq!(set(12, 4), one(|b| b.lb = true));
        assert_eq!(set(12, 5), one(|b| b.rb = true));
        assert_eq!(set(12, 6), one(|b| b.back = true));
        assert_eq!(set(12, 7), one(|b| b.start = true));
        assert_eq!(set(13, 0), one(|b| b.left_stick = true));
        assert_eq!(set(13, 1), one(|b| b.right_stick = true));
        assert_eq!(set(13, 2), one(|b| b.guide = true));
    }

    #[test]
    fn every_hat_value() {
        let want = [
            (0, -1),
            (1, -1),
            (1, 0),
            (1, 1),
            (0, 1),
            (-1, 1),
            (-1, 0),
            (-1, -1),
        ];
        for v in 0u8..16 {
            let mut r = rest();
            r[13] = v << 3;
            let p = decode(&r).unwrap();
            let expect = want.get(usize::from(v)).copied().unwrap_or((0, 0));
            assert_eq!((p.hat_x, p.hat_y), expect, "hat {v}");
        }
    }

    #[test]
    fn hat_ignores_the_buttons_beside_it() {
        let mut r = rest();
        r[13] = (2 << 3) | 0b111;
        let p = decode(&r).unwrap();
        assert_eq!((p.hat_x, p.hat_y), (1, 0));
        assert!(p.buttons.guide && p.buttons.left_stick && p.buttons.right_stick);
    }
}
