//! Native QuadStick HID input.
//!
//! The adapter owns every OS handle and path. Above this file the device is an
//! opaque `LiveDeviceId`, a safe product label and descriptor-derived motion.
//! No report is decoded by a fixed byte offset: the HID report descriptor is
//! parsed first and only fields inside an Application collection whose Generic
//! Desktop usage is Joystick, Game Pad or Multi-axis Controller are considered.

use hidapi::{HidApi, HidDevice, MAX_REPORT_DESCRIPTOR_SIZE};
use qcm_core::error::DeviceError;
use qcm_core::ports::live_input::{
    CandidateKind, LiveCandidate, LiveDeviceId, LiveInputPort, LiveInputSession, Reading,
};
use std::collections::{BTreeMap, HashMap};
use std::ffi::CString;
use std::sync::{Mutex, MutexGuard, PoisonError};

/// Every HID identity emitted by firmware 2373's joystick descriptors.
/// Xbox 360 native (emulation mode 3) is intentionally absent: it is XInput,
/// interface class 0xFF, not HID.
pub const KNOWN_IDENTITIES: &[(u16, u16)] = &[
    (0x16D0, 0x092B), // mode 0, QuadStick / Afterglow PS3 descriptor
    (0x054C, 0x0268), // mode 1, Dual Shock 3
    (0x16D0, 0x092C), // mode 2, X360CE
    (0x16D0, 0x092D), // modes 4/7 once the device decides it is on a PC
    (0x054C, 0x05C5), // mode 4, wired DualShock 4
    (0x0F0D, 0x0066), // mode 4, HORI before the console answers
    (0x057E, 0x2009), // mode 5, Nintendo Switch Pro Controller
    (0x16D0, 0x092E), // mode 6, PS4 without flash drive
    (0x054C, 0x05C4), // mode 7, wireless DualShock 4 V1
];

const GENERIC_DESKTOP_PAGE: u16 = 0x01;
const BUTTON_PAGE: u16 = 0x09;
const USAGE_JOYSTICK: u16 = 0x04;
const USAGE_GAMEPAD: u16 = 0x05;
const USAGE_MULTIAXIS: u16 = 0x08;
const USAGE_X: u16 = 0x30;
const USAGE_Y: u16 = 0x31;
// The live worker owns the blocking read. Keep its timeout short enough that
// disposing the last subscription closes the handle promptly, while still long
// enough to avoid a busy loop when the stick is idle.
const READ_TIMEOUT_MS: i32 = 100;

#[derive(Debug, Default)]
struct DeviceMap {
    next_id: u64,
    by_path: HashMap<Vec<u8>, LiveDeviceId>,
    paths: BTreeMap<LiveDeviceId, CString>,
}

/// Real HID port. `HidApi` itself is short-lived; only opaque ids and private
/// C-string paths survive enumeration so an OS path can never cross IPC.
#[derive(Debug, Default)]
pub struct HidLiveInput {
    devices: Mutex<DeviceMap>,
}

impl HidLiveInput {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    fn devices(&self) -> MutexGuard<'_, DeviceMap> {
        self.devices.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn mint_id(state: &mut DeviceMap, path: &CString) -> LiveDeviceId {
        let key = path.as_bytes().to_vec();
        if let Some(id) = state.by_path.get(&key) {
            return *id;
        }
        state.next_id = state.next_id.saturating_add(1).max(1);
        let id = LiveDeviceId::from_raw(state.next_id);
        state.by_path.insert(key, id);
        state.paths.insert(id, path.clone());
        id
    }
}

impl LiveInputPort for HidLiveInput {
    type Session = HidLiveSession;

    fn candidates(&self) -> Result<Vec<LiveCandidate>, DeviceError> {
        let api = HidApi::new().map_err(|_| DeviceError::NotFound)?;
        let mut found = Vec::new();

        for info in api.device_list() {
            if !known_identity(info.vendor_id(), info.product_id()) {
                continue;
            }

            // Opening each matching interface is deliberate. Most QuadStick
            // modes expose gamepad + mouse + keyboard under one VID/PID and
            // enumeration order is not stable. The descriptor is the authority.
            let Ok(device) = info.open_device(&api) else {
                continue;
            };
            let Some(layout) = descriptor_layout(&device) else {
                continue;
            };
            if !layout.has_stick_fields() {
                continue;
            }

            let path = info.path().to_owned();
            let id = {
                let mut state = self.devices();
                Self::mint_id(&mut state, &path)
            };
            found.push(LiveCandidate {
                id,
                kind: CandidateKind::Readable,
                product: safe_product(info.product_string()),
            });
        }

        Ok(found)
    }

    fn open(&self, device: LiveDeviceId) -> Result<Self::Session, DeviceError> {
        let path = self
            .devices()
            .paths
            .get(&device)
            .cloned()
            .ok_or(DeviceError::NotFound)?;
        let api = HidApi::new().map_err(|_| DeviceError::NotFound)?;
        let handle = api.open_path(path.as_c_str()).map_err(|_| DeviceError::NotFound)?;
        let layout = descriptor_layout(&handle).ok_or(DeviceError::NotFound)?;
        let product = handle
            .get_product_string()
            .ok()
            .flatten()
            .map(|value| safe_product(Some(value.as_str())))
            .unwrap_or_else(|| "QuadStick".to_owned());
        let buffer = vec![0_u8; layout.wire_bytes().max(64)];
        Ok(HidLiveSession {
            device: handle,
            layout,
            product,
            buffer,
        })
    }
}

pub struct HidLiveSession {
    device: HidDevice,
    layout: HidLayout,
    product: String,
    buffer: Vec<u8>,
}

impl LiveInputSession for HidLiveSession {
    fn product(&self) -> &str {
        &self.product
    }

    fn read(&mut self) -> Result<Option<Reading>, DeviceError> {
        let read = self
            .device
            .read_timeout(&mut self.buffer, READ_TIMEOUT_MS)
            .map_err(|_| DeviceError::NotFound)?;
        if read == 0 {
            return Ok(None);
        }
        Ok(self.layout.read(&self.buffer[..read]))
    }
}

fn known_identity(vendor: u16, product: u16) -> bool {
    KNOWN_IDENTITIES
        .iter()
        .any(|&(known_vendor, known_product)| known_vendor == vendor && known_product == product)
}

fn safe_product(raw: Option<&str>) -> String {
    raw.map(str::trim)
        .filter(|value| !value.is_empty() && !looks_like_path(value))
        .unwrap_or("QuadStick")
        .chars()
        .filter(|character| !character.is_control())
        .take(128)
        .collect::<String>()
        .trim()
        .to_owned()
}

fn looks_like_path(value: &str) -> bool {
    value.starts_with('/')
        || value.starts_with("\\\\")
        || value
            .as_bytes()
            .get(1)
            .is_some_and(|second| *second == b':')
        || value.contains("/dev/")
}

fn descriptor_layout(device: &HidDevice) -> Option<HidLayout> {
    let mut bytes = vec![0_u8; MAX_REPORT_DESCRIPTOR_SIZE];
    let used = device.get_report_descriptor(&mut bytes).ok()?;
    HidLayout::parse(&bytes[..used])
}

#[derive(Debug, Clone, Copy, Default)]
struct GlobalState {
    usage_page: u16,
    logical_min: i32,
    logical_max: i32,
    report_size: usize,
    report_count: usize,
    report_id: Option<u8>,
}

#[derive(Debug, Clone, Default)]
struct LocalState {
    usages: Vec<u32>,
    usage_min: Option<u32>,
    usage_max: Option<u32>,
}

impl LocalState {
    fn clear(&mut self) {
        self.usages.clear();
        self.usage_min = None;
        self.usage_max = None;
    }

    fn usage_at(&self, index: usize) -> Option<u32> {
        if let Some(value) = self.usages.get(index) {
            return Some(*value);
        }
        if let (Some(min), Some(max)) = (self.usage_min, self.usage_max) {
            let value = min.saturating_add(index as u32);
            if value <= max {
                return Some(value);
            }
        }
        self.usages.last().copied()
    }

    fn first_usage(&self) -> Option<u32> {
        self.usages.first().copied().or(self.usage_min)
    }
}

#[derive(Debug, Clone, Copy)]
struct CollectionState {
    kind: u8,
    usage: Option<u32>,
}

#[derive(Debug, Clone)]
struct HidField {
    report_id: Option<u8>,
    bit_offset: usize,
    bit_size: usize,
    usage: u32,
    logical_min: i32,
    logical_max: i32,
}

#[derive(Debug, Clone, Default)]
struct HidLayout {
    fields: Vec<HidField>,
    report_bits: BTreeMap<Option<u8>, usize>,
}

impl HidLayout {
    fn parse(bytes: &[u8]) -> Option<Self> {
        let mut layout = Self::default();
        let mut global = GlobalState::default();
        let mut globals = Vec::new();
        let mut local = LocalState::default();
        let mut collections = Vec::<CollectionState>::new();
        let mut index = 0;

        while index < bytes.len() {
            let prefix = bytes[index];
            index += 1;
            if prefix == 0xFE {
                let length = *bytes.get(index)? as usize;
                index = index.checked_add(2 + length)?;
                if index > bytes.len() {
                    return None;
                }
                continue;
            }

            let size = match prefix & 0x03 {
                0 => 0,
                1 => 1,
                2 => 2,
                3 => 4,
                _ => unreachable!(),
            };
            let end = index.checked_add(size)?;
            let data = bytes.get(index..end)?;
            index = end;
            let unsigned = unsigned_value(data);
            let signed = signed_value(data);
            let item_type = (prefix >> 2) & 0x03;
            let tag = (prefix >> 4) & 0x0F;

            match (item_type, tag) {
                // Global: Usage Page, Logical Min/Max, Report Size/ID/Count,
                // Push/Pop. Unknown globals are intentionally ignored.
                (1, 0x0) => global.usage_page = unsigned as u16,
                (1, 0x1) => global.logical_min = signed,
                (1, 0x2) => {
                    global.logical_max = if global.logical_min < 0 {
                        signed
                    } else {
                        unsigned.min(i32::MAX as u32) as i32
                    };
                }
                (1, 0x7) => global.report_size = unsigned as usize,
                (1, 0x8) => global.report_id = u8::try_from(unsigned).ok().filter(|id| *id != 0),
                (1, 0x9) => global.report_count = unsigned as usize,
                (1, 0xA) => globals.push(global),
                (1, 0xB) => global = globals.pop()?,

                // Local: Usage, Usage Minimum, Usage Maximum.
                (2, 0x0) => local.usages.push(resolve_usage(global.usage_page, unsigned, size)),
                (2, 0x1) => local.usage_min = Some(resolve_usage(global.usage_page, unsigned, size)),
                (2, 0x2) => local.usage_max = Some(resolve_usage(global.usage_page, unsigned, size)),

                // Main Collection / End Collection.
                (0, 0xA) => {
                    collections.push(CollectionState {
                        kind: unsigned as u8,
                        usage: local.first_usage(),
                    });
                    local.clear();
                }
                (0, 0xC) => {
                    collections.pop()?;
                    local.clear();
                }

                // Main Input. Every input advances its report's bit cursor, but
                // only variable, non-constant fields inside a stick Application
                // collection are retained for decoding.
                (0, 0x8) => {
                    let offset = *layout.report_bits.get(&global.report_id).unwrap_or(&0);
                    let constant = unsigned & 0x01 != 0;
                    let variable = unsigned & 0x02 != 0;
                    if !constant
                        && variable
                        && global.report_size > 0
                        && global.report_size <= 32
                        && in_stick_application(&collections)
                    {
                        for field_index in 0..global.report_count {
                            if let Some(usage) = local.usage_at(field_index) {
                                layout.fields.push(HidField {
                                    report_id: global.report_id,
                                    bit_offset: offset + field_index * global.report_size,
                                    bit_size: global.report_size,
                                    usage,
                                    logical_min: global.logical_min,
                                    logical_max: global.logical_max,
                                });
                            }
                        }
                    }
                    let bits = global.report_count.checked_mul(global.report_size)?;
                    layout
                        .report_bits
                        .insert(global.report_id, offset.checked_add(bits)?);
                    local.clear();
                }

                // Output/Feature and any other main item consume local state but
                // are not part of the live input contract.
                (0, _) => local.clear(),
                _ => {}
            }
        }

        layout.has_stick_fields().then_some(layout)
    }

    fn has_stick_fields(&self) -> bool {
        self.fields.iter().any(|field| {
            let page = usage_page(field.usage);
            let id = usage_id(field.usage);
            (page == GENERIC_DESKTOP_PAGE && matches!(id, USAGE_X | USAGE_Y))
                || page == BUTTON_PAGE
        })
    }

    fn wire_bytes(&self) -> usize {
        self.report_bits
            .iter()
            .map(|(report_id, bits)| bits.div_ceil(8) + usize::from(report_id.is_some()))
            .max()
            .unwrap_or(64)
    }

    fn read(&self, bytes: &[u8]) -> Option<Reading> {
        let numbered = self.report_bits.keys().any(Option::is_some);
        let report_id = if numbered { bytes.first().copied() } else { None };
        let prefix_bits = usize::from(numbered) * 8;
        let mut x = 0.0;
        let mut y = 0.0;
        let mut buttons = Vec::new();
        let mut matched = false;

        for field in &self.fields {
            if field.report_id != report_id {
                continue;
            }
            let raw = extract_bits(bytes, prefix_bits + field.bit_offset, field.bit_size)?;
            let value = if field.logical_min < 0 {
                sign_extend(raw, field.bit_size)
            } else {
                raw.min(i32::MAX as u32) as i32
            };
            let page = usage_page(field.usage);
            let id = usage_id(field.usage);
            if page == GENERIC_DESKTOP_PAGE && id == USAGE_X {
                x = normalize(value, field.logical_min, field.logical_max);
                matched = true;
            } else if page == GENERIC_DESKTOP_PAGE && id == USAGE_Y {
                y = normalize(value, field.logical_min, field.logical_max);
                matched = true;
            } else if page == BUTTON_PAGE {
                matched = true;
                if value != 0 {
                    buttons.push(id);
                }
            }
        }

        matched.then_some(Reading { x, y, buttons })
    }
}

fn in_stick_application(collections: &[CollectionState]) -> bool {
    collections.iter().any(|collection| {
        collection.kind == 0x01 && collection.usage.is_some_and(is_stick_usage)
    })
}

fn is_stick_usage(usage: u32) -> bool {
    usage_page(usage) == GENERIC_DESKTOP_PAGE
        && matches!(usage_id(usage), USAGE_JOYSTICK | USAGE_GAMEPAD | USAGE_MULTIAXIS)
}

fn resolve_usage(page: u16, value: u32, size: usize) -> u32 {
    if size > 2 {
        value
    } else {
        (u32::from(page) << 16) | (value & 0xFFFF)
    }
}

fn usage_page(usage: u32) -> u16 {
    (usage >> 16) as u16
}

fn usage_id(usage: u32) -> u16 {
    (usage & 0xFFFF) as u16
}

fn unsigned_value(bytes: &[u8]) -> u32 {
    bytes
        .iter()
        .enumerate()
        .fold(0_u32, |value, (shift, byte)| value | (u32::from(*byte) << (shift * 8)))
}

fn signed_value(bytes: &[u8]) -> i32 {
    if bytes.is_empty() {
        return 0;
    }
    sign_extend(unsigned_value(bytes), bytes.len() * 8)
}

fn extract_bits(bytes: &[u8], bit_offset: usize, bit_size: usize) -> Option<u32> {
    if bit_size == 0 || bit_size > 32 {
        return None;
    }
    let mut value = 0_u32;
    for bit in 0..bit_size {
        let absolute = bit_offset.checked_add(bit)?;
        let byte = *bytes.get(absolute / 8)?;
        let set = (byte >> (absolute % 8)) & 1;
        value |= u32::from(set) << bit;
    }
    Some(value)
}

fn sign_extend(value: u32, bits: usize) -> i32 {
    if bits == 0 || bits >= 32 {
        return value as i32;
    }
    let shift = 32 - bits;
    ((value << shift) as i32) >> shift
}

fn normalize(value: i32, minimum: i32, maximum: i32) -> f64 {
    if maximum <= minimum {
        return 0.0;
    }
    let fraction = f64::from(value - minimum) / f64::from(maximum - minimum);
    (fraction * 2.0 - 1.0).clamp(-1.0, 1.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Representative unnumbered gamepad descriptor: X/Y are 8-bit absolute
    // axes and buttons 1-4 are a bitmap. The parser intentionally receives raw
    // descriptor bytes, the same boundary the OS adapter receives.
    const GAMEPAD: &[u8] = &[
        0x05, 0x01, // Usage Page (Generic Desktop)
        0x09, 0x05, // Usage (Game Pad)
        0xA1, 0x01, // Collection (Application)
        0x15, 0x00, // Logical Min 0
        0x26, 0xFF, 0x00, // Logical Max 255
        0x75, 0x08, // Report Size 8
        0x95, 0x02, // Report Count 2
        0x09, 0x30, // X
        0x09, 0x31, // Y
        0x81, 0x02, // Input (Data, Variable, Absolute)
        0x05, 0x09, // Usage Page (Button)
        0x19, 0x01, // Usage Min 1
        0x29, 0x04, // Usage Max 4
        0x15, 0x00, // Logical Min 0
        0x25, 0x01, // Logical Max 1
        0x75, 0x01, // Report Size 1
        0x95, 0x04, // Report Count 4
        0x81, 0x02, // Input variable
        0x75, 0x04, // pad to byte
        0x95, 0x01,
        0x81, 0x03, // Input constant
        0xC0, // End Collection
    ];

    const MOUSE: &[u8] = &[
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08,
        0x95, 0x02, 0x09, 0x30, 0x09, 0x31, 0x81, 0x02, 0xC0,
    ];

    #[test]
    fn every_shipped_identity_is_present_and_xinput_is_not() {
        assert_eq!(KNOWN_IDENTITIES.len(), 9);
        assert!(known_identity(0x054C, 0x0268));
        assert!(known_identity(0x057E, 0x2009));
        assert!(!known_identity(0x045E, 0x028E));
    }

    #[test]
    fn descriptor_selects_gamepad_and_rejects_mouse_interface() {
        assert!(HidLayout::parse(GAMEPAD).is_some());
        assert!(HidLayout::parse(MOUSE).is_none());
    }

    #[test]
    fn axes_are_descriptor_normalized_and_buttons_use_usage_numbers() {
        let layout = HidLayout::parse(GAMEPAD).expect("gamepad descriptor");
        let reading = layout.read(&[0, 255, 0b0000_0101]).expect("report");
        assert_eq!(reading.x, -1.0);
        assert_eq!(reading.y, 1.0);
        assert_eq!(reading.buttons, vec![1, 3]);
    }

    #[test]
    fn missing_axis_defaults_to_centre_instead_of_reusing_an_offset() {
        let descriptor = [
            0x05, 0x01, 0x09, 0x04, 0xA1, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75,
            0x08, 0x95, 0x01, 0x09, 0x30, 0x81, 0x02, 0xC0,
        ];
        let layout = HidLayout::parse(&descriptor).expect("joystick descriptor");
        let reading = layout.read(&[255]).expect("report");
        assert_eq!(reading.x, 1.0);
        assert_eq!(reading.y, 0.0);
    }

    #[test]
    fn numbered_report_uses_id_prefix_without_treating_it_as_axis_data() {
        let descriptor = [
            0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x85, 0x07, 0x15, 0x00, 0x26,
            0xFF, 0x00, 0x75, 0x08, 0x95, 0x02, 0x09, 0x30, 0x09, 0x31, 0x81, 0x02,
            0xC0,
        ];
        let layout = HidLayout::parse(&descriptor).expect("gamepad descriptor");
        let reading = layout.read(&[7, 128, 255]).expect("report seven");
        assert!(reading.x.abs() < 0.01);
        assert_eq!(reading.y, 1.0);
        assert!(layout.read(&[8, 128, 255]).is_none());
    }

    #[test]
    fn safe_product_never_falls_back_to_an_os_path() {
        assert_eq!(safe_product(Some("/dev/hidraw4")), "QuadStick");
        assert_eq!(safe_product(Some("C:\\secret\\hid")), "QuadStick");
        assert_eq!(safe_product(Some("  QuadStick FPS  ")), "QuadStick FPS");
    }
}
