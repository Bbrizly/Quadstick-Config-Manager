//! The traits the core talks to the outside world through.
//!
//! Every port here is scoped to the operations the core actually needs. None of
//! them takes a path, so no caller, including a compromised window, can name a
//! place on the machine to read or write.

pub mod storage;
