//! Where the app looks for a mounted QuadStick, per platform.
//!
//! The parity baseline is the shipped `Device.FindCandidates`: every ready
//! drive, keep removable, keep fixed only on macOS under `/Volumes`, then the
//! `default.csv` marker decides. The marker check is not here; it belongs to
//! the storage adapter, and this only produces roots worth asking about.

use std::path::PathBuf;

/// Where candidate roots come from.
///
/// A trait so the adapter above it can be driven by a fixed list in a test. The
/// platform implementations cannot be: they answer differently on every machine
/// they run on, which is exactly why nothing above them may depend on their
/// answer being right.
pub trait VolumeSource {
    fn candidate_roots(&self) -> Vec<PathBuf>;
}

/// The real thing.
#[derive(Debug, Clone, Copy, Default)]
pub struct PlatformVolumes;

impl VolumeSource for PlatformVolumes {
    fn candidate_roots(&self) -> Vec<PathBuf> {
        platform_roots()
    }
}

/// A fixed list, for tests and for the folder the user picked by hand.
#[derive(Debug, Clone, Default)]
pub struct FixedVolumes {
    roots: Vec<PathBuf>,
}

impl FixedVolumes {
    #[must_use]
    pub fn new(roots: Vec<PathBuf>) -> Self {
        Self { roots }
    }
}

impl VolumeSource for FixedVolumes {
    fn candidate_roots(&self) -> Vec<PathBuf> {
        self.roots.clone()
    }
}

/// macOS mounts removable volumes under `/Volumes`, and `DriveInfo` reports them
/// as fixed, which is why the shipped code accepts fixed drives only there. The
/// boot volume is `/`, never a child of `/Volumes`, so it is out of reach here
/// for the same reason it was out of reach before.
#[cfg(target_os = "macos")]
fn platform_roots() -> Vec<PathBuf> {
    read_children("/Volumes")
}

/// Linux has no drive list. `/proc/mounts` is the closest thing, and the
/// filesystem type is what `DriveInfo` reads to decide a mount is removable. A
/// QuadStick is FAT formatted, so the FAT family is what is kept; a mount the
/// app cannot even read the type of is skipped rather than guessed at.
#[cfg(target_os = "linux")]
fn platform_roots() -> Vec<PathBuf> {
    const REMOVABLE_FILESYSTEMS: [&str; 4] = ["vfat", "msdos", "exfat", "fat"];
    let Ok(mounts) = std::fs::read_to_string("/proc/mounts") else {
        return Vec::new();
    };
    let mut roots = Vec::new();
    for line in mounts.lines() {
        let mut fields = line.split_whitespace();
        let (Some(_device), Some(mount), Some(kind)) =
            (fields.next(), fields.next(), fields.next())
        else {
            continue;
        };
        if REMOVABLE_FILESYSTEMS.contains(&kind) {
            roots.push(PathBuf::from(unescape_mount(mount)));
        }
    }
    roots
}

/// `/proc/mounts` writes a space in a mount point as `\040`. Only the four
/// escapes the kernel emits are handled; anything else is left alone rather than
/// half decoded into a path that names somewhere else.
#[cfg(target_os = "linux")]
fn unescape_mount(mount: &str) -> String {
    mount
        .replace("\\040", " ")
        .replace("\\011", "\t")
        .replace("\\012", "\n")
        .replace("\\134", "\\")
}

/// Windows has no way to ask which drives are removable without calling
/// `GetDriveTypeW`, and this workspace forbids unsafe code, so the drive letters
/// are probed and the marker decides.
///
/// That is broader than the shipped rule, which accepts removable drives only.
/// The system drive is excluded because it is the one fixed disk that is certain
/// to be there; a second fixed disk with a `default.csv` in its root would still
/// be offered, where the shipped app would not offer it. It is recorded as an
/// open item rather than papered over: closing it needs a decision about taking
/// on a Windows API dependency.
#[cfg(target_os = "windows")]
fn platform_roots() -> Vec<PathBuf> {
    let system = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
    let system = system.trim_end_matches(['\\', '/']).to_ascii_uppercase();
    (b'A'..=b'Z')
        .map(|letter| format!("{}:", char::from(letter)))
        .filter(|drive| drive != &system)
        .map(|drive| PathBuf::from(format!("{drive}\\")))
        .filter(|root| root.join(super::MARKER).is_file())
        .collect()
}

#[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
fn platform_roots() -> Vec<PathBuf> {
    Vec::new()
}

/// Every directory entry under a mount root, skipping what cannot be read.
///
/// An unreadable or permission-denied volume is skipped, not reported: the
/// shipped enumeration swallowed both for the same reason, and one locked drive
/// must not stop the app from finding a QuadStick on another.
#[cfg(target_os = "macos")]
fn read_children(parent: &str) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(parent) else {
        return Vec::new();
    };
    entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{FixedVolumes, PlatformVolumes, VolumeSource};
    use std::path::PathBuf;

    #[test]
    fn a_fixed_source_hands_back_exactly_what_it_was_given() {
        let source = FixedVolumes::new(vec![PathBuf::from("/one"), PathBuf::from("/two")]);
        assert_eq!(
            source.candidate_roots(),
            vec![PathBuf::from("/one"), PathBuf::from("/two")]
        );
    }

    // Enumeration answers differently on every machine, so the only thing worth
    // asserting is that it answers at all and never names the boot volume.
    #[test]
    fn the_platform_source_answers_without_naming_the_boot_volume() {
        let roots = PlatformVolumes.candidate_roots();
        assert!(!roots.contains(&PathBuf::from("/")));
    }
}
