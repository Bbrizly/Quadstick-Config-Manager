//! A virtual Xbox 360 pad on Linux through uinput, shaped like the one the
//! kernel's xpad driver makes, so SDL, Steam and Proton take it as a real pad.

use std::io;

use evdev::uinput::VirtualDevice;
use evdev::{
    AbsInfo, AbsoluteAxisCode as Abs, AttributeSet, BusType, Device, InputEvent, InputId, KeyCode,
    UinputAbsSetup,
};

use crate::{PRODUCT_ID_X360CE, Pad, VENDOR_ID};

const KEYS: [KeyCode; 11] = [
    KeyCode::BTN_SOUTH,
    KeyCode::BTN_EAST,
    KeyCode::BTN_WEST,
    KeyCode::BTN_NORTH,
    KeyCode::BTN_TL,
    KeyCode::BTN_TR,
    KeyCode::BTN_SELECT,
    KeyCode::BTN_START,
    KeyCode::BTN_MODE,
    KeyCode::BTN_THUMBL,
    KeyCode::BTN_THUMBR,
];

pub struct VirtualPad {
    device: VirtualDevice,
    /// The QuadStick's own gamepad node, grabbed so games do not see two pads.
    /// Held only to keep the grab; the kernel lets go when it drops.
    _grabbed: Option<Device>,
}

impl VirtualPad {
    pub fn create() -> io::Result<Self> {
        let stick = AbsInfo::new(0, -32768, 32767, 16, 128, 0);
        let trigger = AbsInfo::new(0, 0, 255, 0, 0, 0);
        let hat = AbsInfo::new(0, -1, 1, 0, 0, 0);
        let mut builder = VirtualDevice::builder()?
            .name("Microsoft X-Box 360 pad")
            // bcdDevice of a wired 360 pad, which xpad reports as the version.
            .input_id(InputId::new(BusType::BUS_USB, 0x045E, 0x028E, 0x0114))
            .with_keys(&KEYS.iter().collect::<AttributeSet<_>>())?;
        for (code, info) in [
            (Abs::ABS_X, stick),
            (Abs::ABS_Y, stick),
            (Abs::ABS_RX, stick),
            (Abs::ABS_RY, stick),
            (Abs::ABS_Z, trigger),
            (Abs::ABS_RZ, trigger),
            (Abs::ABS_HAT0X, hat),
            (Abs::ABS_HAT0Y, hat),
        ] {
            builder = builder.with_absolute_axis(&UinputAbsSetup::new(code, info))?;
        }
        let device = builder.build()?;
        Ok(Self {
            device,
            _grabbed: grab_real_stick(),
        })
    }

    pub fn send(&mut self, pad: &Pad) -> io::Result<()> {
        self.device.emit(&events(pad))
    }
}

impl Drop for VirtualPad {
    fn drop(&mut self) {
        // The kernel releases held keys when the device goes, but say it anyway.
        let _ = self.send(&Pad::default());
    }
}

pub fn events(pad: &Pad) -> Vec<InputEvent> {
    let b = &pad.buttons;
    let pressed = [
        b.a,
        b.b,
        b.x,
        b.y,
        b.lb,
        b.rb,
        b.back,
        b.start,
        b.guide,
        b.left_stick,
        b.right_stick,
    ];
    let axes = [
        (Abs::ABS_X, i32::from(pad.left_x)),
        (Abs::ABS_Y, i32::from(pad.left_y)),
        (Abs::ABS_RX, i32::from(pad.right_x)),
        (Abs::ABS_RY, i32::from(pad.right_y)),
        (Abs::ABS_Z, i32::from(pad.left_trigger)),
        (Abs::ABS_RZ, i32::from(pad.right_trigger)),
        (Abs::ABS_HAT0X, i32::from(pad.hat_x)),
        (Abs::ABS_HAT0Y, i32::from(pad.hat_y)),
    ];
    let keys = KEYS
        .iter()
        .zip(pressed)
        .map(|(k, down)| *evdev::KeyEvent::new(*k, i32::from(down)));
    let abs = axes
        .iter()
        .map(|(code, v)| *evdev::AbsoluteAxisEvent::new(*code, *v));
    keys.chain(abs).collect()
}

/// Grabs the gamepad node only; the stick's keyboard and mouse stay live.
/// `grab()` hides evdev, not hidraw, so Steam's HIDAPI driver may still list it.
fn grab_real_stick() -> Option<Device> {
    let (path, mut device) = evdev::enumerate().find(|(_, d)| {
        let id = d.input_id();
        id.vendor() == VENDOR_ID
            && id.product() == PRODUCT_ID_X360CE
            && d.supported_absolute_axes()
                .is_some_and(|a| a.contains(Abs::ABS_X))
    })?;
    match device.grab() {
        Ok(()) => Some(device),
        Err(e) => {
            eprintln!(
                "qs-bridge: could not hide {}: {e}. Games may see the QuadStick twice.",
                path.display()
            );
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Buttons;

    #[test]
    fn each_button_reaches_its_xbox_key() {
        type Press = fn(&mut Buttons);
        let cases: [(Press, KeyCode); 11] = [
            (|b| b.a = true, KeyCode::BTN_SOUTH),
            (|b| b.b = true, KeyCode::BTN_EAST),
            (|b| b.x = true, KeyCode::BTN_WEST),
            (|b| b.y = true, KeyCode::BTN_NORTH),
            (|b| b.lb = true, KeyCode::BTN_TL),
            (|b| b.rb = true, KeyCode::BTN_TR),
            (|b| b.back = true, KeyCode::BTN_SELECT),
            (|b| b.start = true, KeyCode::BTN_START),
            (|b| b.guide = true, KeyCode::BTN_MODE),
            (|b| b.left_stick = true, KeyCode::BTN_THUMBL),
            (|b| b.right_stick = true, KeyCode::BTN_THUMBR),
        ];
        for (press, key) in cases {
            let mut pad = Pad::default();
            press(&mut pad.buttons);
            let down: Vec<u16> = events(&pad)
                .iter()
                .filter(|e| e.event_type() == evdev::EventType::KEY && e.value() == 1)
                .map(|e| e.code())
                .collect();
            assert_eq!(down, [key.code()]);
        }
    }

    #[test]
    fn neutral_releases_everything() {
        assert!(events(&Pad::default()).iter().all(|e| e.value() == 0));
    }
}
