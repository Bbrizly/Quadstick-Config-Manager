//! The local profile store port.
//!
//! Paths never cross this port into the core or WebView. The optional
//! persistent key is specifically for native services such as Drive backup: a
//! filesystem adapter may use its private path as the key in native-only state,
//! while tests can retain the default opaque process identity.

use crate::error::StorageError;
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LocalProfileId(u64);

impl LocalProfileId {
    #[must_use]
    pub const fn from_raw(raw: u64) -> Self { Self(raw) }
    #[must_use]
    pub const fn raw(self) -> u64 { self.0 }
}
impl fmt::Display for LocalProfileId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { write!(f, "local-{}", self.0) }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ProfileDisplayName(String);
impl ProfileDisplayName {
    const MAX: usize = 128;
    const FALLBACK: &'static str = "Untitled profile";
    #[must_use]
    pub fn new(name: &str) -> Self {
        let last = name.rsplit(['/', '\\']).find(|part| !part.trim().is_empty()).unwrap_or_default();
        let cleaned: String = last.chars().filter(|c| !c.is_control() && *c != ':').take(Self::MAX).collect();
        let trimmed = cleaned.trim();
        if trimmed.is_empty() { Self(Self::FALLBACK.to_owned()) } else { Self(trimmed.to_owned()) }
    }
    #[must_use]
    pub fn as_str(&self) -> &str { &self.0 }
}
impl fmt::Display for ProfileDisplayName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { f.write_str(&self.0) }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalProfileRef {
    id: LocalProfileId,
    display_name: ProfileDisplayName,
}
impl LocalProfileRef {
    #[must_use]
    pub const fn new(id: LocalProfileId, display_name: ProfileDisplayName) -> Self { Self { id, display_name } }
    #[must_use]
    pub const fn id(&self) -> LocalProfileId { self.id }
    #[must_use]
    pub const fn display_name(&self) -> &ProfileDisplayName { &self.display_name }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalWriteReceipt { pub bytes: usize }

pub trait LocalProfileStore {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError>;
    fn write(&self, target: &LocalProfileRef, text: &str) -> Result<LocalWriteReceipt, StorageError>;
    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError>;

    /// Stable native-only identity for auxiliary persisted state. It is never a
    /// display value and must never cross IPC. Real filesystem adapters override
    /// this with their private persistent location; fakes can use the process id.
    fn persistent_key(&self, target: &LocalProfileRef) -> Result<String, StorageError> {
        Ok(target.id().to_string())
    }
}

impl<T: LocalProfileStore + ?Sized> LocalProfileStore for &T {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError> { (**self).read(target) }
    fn write(&self, target: &LocalProfileRef, text: &str) -> Result<LocalWriteReceipt, StorageError> { (**self).write(target, text) }
    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError> { (**self).is_on_quadstick(target) }
    fn persistent_key(&self, target: &LocalProfileRef) -> Result<String, StorageError> { (**self).persistent_key(target) }
}

impl<T: LocalProfileStore + ?Sized> LocalProfileStore for std::sync::Arc<T> {
    fn read(&self, target: &LocalProfileRef) -> Result<String, StorageError> { (**self).read(target) }
    fn write(&self, target: &LocalProfileRef, text: &str) -> Result<LocalWriteReceipt, StorageError> { (**self).write(target, text) }
    fn is_on_quadstick(&self, target: &LocalProfileRef) -> Result<bool, StorageError> { (**self).is_on_quadstick(target) }
    fn persistent_key(&self, target: &LocalProfileRef) -> Result<String, StorageError> { (**self).persistent_key(target) }
}
