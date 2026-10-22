//! Usage data, ported from `src/QuadStick.App/Telemetry.cs`.
//!
//! The same closed event set, the same per-event property allowlist, the same
//! consent rule (`QSCM_TELEMETRY=0` wins, then notice version 2, then the
//! toggle) and the same empty-token rule: without a token baked in at build
//! time no sender is ever started, so a fork, a source build and a test run
//! cannot report.
//!
//! Cost when off is a flag read. Cost when on and idle is one parked thread
//! blocked on a channel: no timer, no wakeup. An event starts a five second
//! batch window, like the .NET SDK's flush timer; the batch goes out on one
//! HTTPS request off the UI thread.

use serde_json::{Map, Value, json};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Bump to re-prompt when what gets sent materially changes.
/// 2: setup time and the library game were added (2026-10-14).
pub const NOTICE_VERSION: u32 = 2;
pub const HOST: &str = "https://us.i.posthog.com";
pub const MAX_FEEDBACK_CHARS: usize = 1000;
const BATCH_WINDOW: Duration = Duration::from_secs(5);
const BATCH_MAX: usize = 100;
const SEND_FLUSH: Duration = Duration::from_secs(5);
const SHUTDOWN_FLUSH: Duration = Duration::from_secs(2);
/// Stripped before the wire. Marks the one event that needs no standing
/// consent: a crash report the person pressed Send on.
const CRASH_CONSENT_MARKER: &str = "__crash_consent";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TelemetryEvent {
    AppLaunched,
    ProfileOpened,
    ProfileSaved,
    InstallAttempted,
    InstallSucceeded,
    InstallFailed,
    FeatureUsed,
    FeedbackSubmitted,
    StrengthMeasured,
}

impl TelemetryEvent {
    #[must_use]
    pub const fn wire(self) -> &'static str {
        match self {
            Self::AppLaunched => "app_launched",
            Self::ProfileOpened => "profile_opened",
            Self::ProfileSaved => "profile_saved",
            Self::InstallAttempted => "install_attempted",
            Self::InstallSucceeded => "install_succeeded",
            Self::InstallFailed => "install_failed",
            Self::FeatureUsed => "feature_used",
            Self::FeedbackSubmitted => "feedback_submitted",
            Self::StrengthMeasured => "strength_measured",
        }
    }

    #[must_use]
    pub fn from_wire(name: &str) -> Option<Self> {
        [
            Self::AppLaunched,
            Self::ProfileOpened,
            Self::ProfileSaved,
            Self::InstallAttempted,
            Self::InstallSucceeded,
            Self::InstallFailed,
            Self::FeatureUsed,
            Self::FeedbackSubmitted,
            Self::StrengthMeasured,
        ]
        .into_iter()
        .find(|event| event.wire() == name)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileSource {
    New,
    File,
    Library,
    Device,
    Sheets,
    Drive,
    Rescue,
}

impl ProfileSource {
    #[must_use]
    pub const fn wire(self) -> &'static str {
        match self {
            Self::New => "new",
            Self::File => "file",
            Self::Library => "library",
            Self::Device => "device",
            Self::Sheets => "sheets",
            Self::Drive => "drive",
            Self::Rescue => "rescue",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallFailure {
    HasErrors,
    NoProfile,
    NotAQuadstick,
    CancelledDevice,
    CancelledFolder,
    CancelledDefault,
    CancelledPreferences,
    IoError,
}

impl InstallFailure {
    #[must_use]
    pub const fn wire(self) -> &'static str {
        match self {
            Self::HasErrors => "has_errors",
            Self::NoProfile => "no_profile",
            Self::NotAQuadstick => "not_a_quadstick",
            Self::CancelledDevice => "cancelled_device",
            Self::CancelledFolder => "cancelled_folder",
            Self::CancelledDefault => "cancelled_default",
            Self::CancelledPreferences => "cancelled_preferences",
            Self::IoError => "io_error",
        }
    }

    /// The four reasons a window reports itself: the person backed out.
    #[must_use]
    pub fn cancelled_from_wire(name: &str) -> Option<Self> {
        [
            Self::CancelledDevice,
            Self::CancelledFolder,
            Self::CancelledDefault,
            Self::CancelledPreferences,
        ]
        .into_iter()
        .find(|reason| reason.wire() == name)
    }

    /// Device and folder choices come before `prepare_install`, which is
    /// where the attempt is counted. Avalonia counts it first.
    #[must_use]
    pub const fn before_prepare(self) -> bool {
        matches!(self, Self::CancelledDevice | Self::CancelledFolder)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AppFeature {
    SheetsImport,
    DriveBackup,
    DriveRestore,
    ShareLink,
}

impl AppFeature {
    #[must_use]
    pub const fn wire(self) -> &'static str {
        match self {
            Self::SheetsImport => "sheets_import",
            Self::DriveBackup => "drive_backup",
            Self::DriveRestore => "drive_restore",
            Self::ShareLink => "share_link",
        }
    }
}

/// The token CI bakes in from `POSTHOG_PROJECT_TOKEN`, as `TelemetryToken.Local.cs`
/// does for the shipped app. Empty in every other build.
#[must_use]
pub fn build_token() -> &'static str {
    option_env!("QSCM_TELEMETRY_TOKEN").unwrap_or("")
}

#[must_use]
pub fn kill_switch_from_env() -> bool {
    std::env::var("QSCM_TELEMETRY").ok().as_deref() == Some("0")
}

/// Allowed per event, not once globally, so one wrong call site cannot put
/// feedback text on `app_launched`. An allowlist fails closed.
#[must_use]
pub fn allowed(event: &str, key: &str) -> bool {
    matches!(key, "os" | "os_version" | "app_version" | "is_debug")
        || matches!(
            (event, key),
            ("profile_opened", "source" | "game")
                | ("profile_saved" | "install_succeeded", "game")
                | ("install_succeeded", "setup_seconds")
                | ("strength_measured", "puff" | "sip")
                | ("install_failed", "reason")
                | ("feature_used", "feature")
                | ("feedback_submitted", "text")
                | ("$exception", "$exception_list" | "$exception_type")
        )
}

/// Where a batch goes. The real one is PostHog's `/batch/` endpoint.
pub trait Sink: Send + Sync {
    /// True only when the server took it.
    fn post(&self, body: &Value) -> bool;
}

/// Blocking HTTPS, built on the sender thread the first time it is needed.
pub struct PostHogSink {
    http: OnceLock<Option<reqwest::blocking::Client>>,
}

impl Default for PostHogSink {
    fn default() -> Self {
        Self {
            http: OnceLock::new(),
        }
    }
}

impl Sink for PostHogSink {
    fn post(&self, body: &Value) -> bool {
        let client = self.http.get_or_init(|| {
            reqwest::blocking::Client::builder()
                .timeout(Duration::from_secs(10))
                .user_agent("QuadStickConfigManager")
                .build()
                .ok()
        });
        let Some(client) = client else { return false };
        client
            .post(format!("{HOST}/batch/"))
            .json(body)
            .send()
            .is_ok_and(|response| response.status().is_success())
    }
}

#[derive(Debug, Clone)]
struct Captured {
    event: String,
    distinct_id: String,
    properties: Map<String, Value>,
    timestamp: String,
}

enum Msg {
    Event(Captured),
    Flush(Sender<bool>),
    Stop,
}

struct Worker {
    tx: Sender<Msg>,
    handle: Option<JoinHandle<()>>,
}

struct State {
    distinct_id: String,
    game: Option<String>,
    opened_at: Option<Instant>,
    launch_tracked: bool,
}

pub struct Telemetry {
    token: String,
    kill_switch: bool,
    usage: Arc<AtomicBool>,
    sink: Arc<dyn Sink>,
    window: Duration,
    state: Mutex<State>,
    worker: Mutex<Option<Worker>>,
}

static GLOBAL: OnceLock<Telemetry> = OnceLock::new();

/// The app's one instance. Built on first use, with no thread until consent.
pub fn global() -> &'static Telemetry {
    GLOBAL.get_or_init(|| {
        Telemetry::new(
            build_token(),
            kill_switch_from_env() || !crate::host::network(),
            Arc::new(PostHogSink::default()),
        )
    })
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

impl Telemetry {
    pub fn new(token: &str, kill_switch: bool, sink: Arc<dyn Sink>) -> Self {
        Self {
            token: token.to_owned(),
            kill_switch,
            usage: Arc::new(AtomicBool::new(false)),
            sink,
            window: BATCH_WINDOW,
            state: Mutex::new(State {
                distinct_id: String::new(),
                game: None,
                opened_at: None,
                launch_tracked: false,
            }),
            worker: Mutex::new(None),
        }
    }

    #[must_use]
    pub fn with_window(mut self, window: Duration) -> Self {
        self.window = window;
        self
    }

    /// True when `QSCM_TELEMETRY=0`: nothing to consent to, so do not ask.
    #[must_use]
    pub const fn disabled_by_environment(&self) -> bool {
        self.kill_switch
    }

    #[must_use]
    pub fn is_live(&self) -> bool {
        lock(&self.worker).is_some()
    }

    /// The only way standing consent reaches the sender. `install_id` is the
    /// durable id when usage is on, empty otherwise. Tracks `app_launched`
    /// once per process the first time usage comes on.
    pub fn apply_consent(&self, notice_version: u32, usage: bool, install_id: &str) {
        let on = !self.kill_switch && notice_version >= NOTICE_VERSION && usage;
        self.usage.store(on, Ordering::SeqCst);
        {
            let mut state = lock(&self.state);
            state.distinct_id = if on {
                install_id.to_owned()
            } else {
                String::new()
            };
        }
        if on {
            self.start();
            let first = {
                let mut state = lock(&self.state);
                !std::mem::replace(&mut state.launch_tracked, true)
            };
            if first {
                self.track(TelemetryEvent::AppLaunched);
            }
        } else {
            self.stop();
        }
    }

    fn start(&self) {
        if self.kill_switch || self.token.is_empty() {
            return;
        }
        let mut worker = lock(&self.worker);
        if worker.is_some() {
            return;
        }
        let (tx, rx) = mpsc::channel::<Msg>();
        let sink = Arc::clone(&self.sink);
        let usage = Arc::clone(&self.usage);
        let token = self.token.clone();
        let window = self.window;
        let handle = std::thread::Builder::new()
            .name("qcm-telemetry".into())
            .spawn(move || run_sender(&rx, &*sink, &usage, &token, window))
            .ok();
        if handle.is_some() {
            *worker = Some(Worker { tx, handle });
        }
    }

    /// Drop the sender. Queued usage events are dropped too, because the
    /// usage flag is already down when the drain runs.
    fn stop(&self) {
        let taken = lock(&self.worker).take();
        if let Some(worker) = taken {
            let _ = worker.tx.send(Msg::Stop);
            // Not joined: a consent change must not block on the network.
            drop(worker.handle);
        }
    }

    /// Push the queue on exit so a short session is not a silent one, then
    /// let go.
    pub fn shutdown(&self) {
        // Bounded: two seconds is the most a close ever waits. The sender is
        // then detached, not joined, so a request already in flight cannot
        // hold the exit for its full HTTP timeout.
        let _ = self.flush(SHUTDOWN_FLUSH);
        self.usage.store(false, Ordering::SeqCst);
        self.stop();
    }

    fn flush(&self, wait: Duration) -> bool {
        let tx = match lock(&self.worker).as_ref() {
            Some(worker) => worker.tx.clone(),
            None => return false,
        };
        let (reply, answer) = mpsc::channel();
        if tx.send(Msg::Flush(reply)).is_err() {
            return false;
        }
        answer.recv_timeout(wait).unwrap_or(false)
    }

    fn envelope() -> Map<String, Value> {
        let os = if cfg!(target_os = "windows") {
            "windows"
        } else if cfg!(target_os = "macos") {
            "macos"
        } else if cfg!(target_os = "linux") {
            "linux"
        } else {
            "other"
        };
        let mut props = Map::new();
        props.insert("os".into(), json!(os));
        props.insert("os_version".into(), json!(truncate(&os_description(), 80)));
        props.insert("app_version".into(), json!(app_version()));
        props.insert("is_debug".into(), json!(cfg!(debug_assertions)));
        props
    }

    fn capture(&self, event: TelemetryEvent, extra: &[(&str, Value)]) -> bool {
        if self.kill_switch || !self.usage.load(Ordering::SeqCst) {
            return false;
        }
        let (distinct_id, game) = {
            let state = lock(&self.state);
            (state.distinct_id.clone(), state.game.clone())
        };
        if distinct_id.is_empty() {
            return false;
        }
        let tx = match lock(&self.worker).as_ref() {
            Some(worker) => worker.tx.clone(),
            None => return false,
        };
        let mut properties = Self::envelope();
        for (key, value) in extra {
            properties.insert((*key).to_owned(), value.clone());
        }
        if let Some(game) = game
            && matches!(
                event,
                TelemetryEvent::ProfileOpened
                    | TelemetryEvent::ProfileSaved
                    | TelemetryEvent::InstallSucceeded
            )
        {
            properties.insert("game".into(), json!(game));
        }
        tx.send(Msg::Event(Captured {
            event: event.wire().to_owned(),
            distinct_id,
            properties,
            timestamp: chrono::Utc::now().to_rfc3339(),
        }))
        .is_ok()
    }

    pub fn track(&self, event: TelemetryEvent) -> bool {
        self.capture(event, &[])
    }

    /// A profile was opened. Starts the setup clock. Only a registry slug ever
    /// lands in `game`, never a file name or a profile's own text.
    pub fn opened(&self, source: ProfileSource, game: Option<&str>) -> bool {
        {
            let mut state = lock(&self.state);
            state.game = game.map(str::to_owned);
            state.opened_at = Some(Instant::now());
        }
        self.capture(
            TelemetryEvent::ProfileOpened,
            &[("source", json!(source.wire()))],
        )
    }

    pub fn saved(&self) -> bool {
        self.track(TelemetryEvent::ProfileSaved)
    }

    pub fn install_attempted(&self) -> bool {
        self.track(TelemetryEvent::InstallAttempted)
    }

    /// Install worked. Carries the seconds since the profile was opened.
    pub fn installed(&self) -> bool {
        let seconds = lock(&self.state)
            .opened_at
            .map_or(0, |at| at.elapsed().as_secs());
        self.capture(
            TelemetryEvent::InstallSucceeded,
            &[("setup_seconds", json!(seconds))],
        )
    }

    pub fn install_failed(&self, reason: InstallFailure) -> bool {
        self.capture(
            TelemetryEvent::InstallFailed,
            &[("reason", json!(reason.wire()))],
        )
    }

    pub fn feature_used(&self, feature: AppFeature) -> bool {
        self.capture(
            TelemetryEvent::FeatureUsed,
            &[("feature", json!(feature.wire()))],
        )
    }

    /// Percent of the sensor's range; 4 means below the bottom of the sweep.
    pub fn strength_measured(&self, puff: i32, sip: i32) -> bool {
        self.capture(
            TelemetryEvent::StrengthMeasured,
            &[("puff", json!(puff)), ("sip", json!(sip))],
        )
    }

    /// False when nothing was sent, so the window never claims otherwise. Waits
    /// for the batch, because the caller clears the box on true.
    pub fn send_feedback(&self, text: &str) -> bool {
        let text = text.trim();
        if text.is_empty() {
            return false;
        }
        let capped: String = text.chars().take(MAX_FEEDBACK_CHARS).collect();
        if !self.capture(
            TelemetryEvent::FeedbackSubmitted,
            &[("text", json!(capped))],
        ) {
            return false;
        }
        self.flush(SEND_FLUSH)
    }

    /// Send one stored crash report. Pressing Send is the consent, so no
    /// standing flag is needed; the install id must already be durable.
    pub fn send_crash_report(&self, report: &Value, install_id: &str) -> bool {
        let Some(properties) = exception_properties(report) else {
            return false;
        };
        if install_id.is_empty() || self.kill_switch {
            return false;
        }
        self.start();
        let tx = match lock(&self.worker).as_ref() {
            Some(worker) => worker.tx.clone(),
            None => return false,
        };
        let mut properties = properties;
        properties.insert(CRASH_CONSENT_MARKER.into(), json!(true));
        let sent = tx
            .send(Msg::Event(Captured {
                event: "$exception".into(),
                distinct_id: install_id.to_owned(),
                properties,
                timestamp: chrono::Utc::now().to_rfc3339(),
            }))
            .is_ok();
        let flushed = sent && self.flush(SEND_FLUSH);
        // A sender started only for this report goes away again.
        if !self.usage.load(Ordering::SeqCst) {
            self.stop();
        }
        flushed
    }
}

/// Rebuild what crosses the wire from typed fields: allowed keys only, the
/// crash marker removed, nothing sent at all without consent or the marker.
fn scrub(event: &Captured, usage: bool) -> Option<Value> {
    let crash_consent = event.properties.contains_key(CRASH_CONSENT_MARKER);
    if !usage && !crash_consent {
        return None;
    }
    let mut kept = Map::new();
    for (key, value) in &event.properties {
        if allowed(&event.event, key) {
            kept.insert(key.clone(), value.clone());
        }
    }
    // What the .NET SDK added on its own, disclosed in PRIVACY.md.
    kept.insert("$lib".into(), json!("quadstick-config-manager"));
    kept.insert("$lib_version".into(), json!(app_version()));
    kept.insert("$geoip_disable".into(), json!(true));
    Some(json!({
        "event": event.event,
        "distinct_id": event.distinct_id,
        "properties": kept,
        "timestamp": event.timestamp,
        "type": "capture",
    }))
}

fn send_batch(queue: &mut Vec<Captured>, sink: &dyn Sink, usage: &AtomicBool, token: &str) -> bool {
    let on = usage.load(Ordering::SeqCst);
    let batch: Vec<Value> = queue.drain(..).filter_map(|e| scrub(&e, on)).collect();
    if batch.is_empty() {
        return true;
    }
    sink.post(&json!({ "api_key": token, "batch": batch }))
}

fn run_sender(
    rx: &mpsc::Receiver<Msg>,
    sink: &dyn Sink,
    usage: &AtomicBool,
    token: &str,
    window: Duration,
) {
    let mut queue: Vec<Captured> = Vec::new();
    let mut deadline = Instant::now();
    loop {
        // Parked with nothing queued: no timer runs while idle.
        let msg = if queue.is_empty() {
            rx.recv().map_err(|_| RecvTimeoutError::Disconnected)
        } else {
            rx.recv_timeout(deadline.saturating_duration_since(Instant::now()))
        };
        match msg {
            Ok(Msg::Event(event)) => {
                if queue.is_empty() {
                    deadline = Instant::now() + window;
                }
                queue.push(event);
                if queue.len() >= BATCH_MAX {
                    send_batch(&mut queue, sink, usage, token);
                }
            }
            Ok(Msg::Flush(reply)) => {
                let ok = send_batch(&mut queue, sink, usage, token);
                let _ = reply.send(ok);
            }
            Err(RecvTimeoutError::Timeout) => {
                send_batch(&mut queue, sink, usage, token);
            }
            Ok(Msg::Stop) | Err(RecvTimeoutError::Disconnected) => {
                send_batch(&mut queue, sink, usage, token);
                return;
            }
        }
    }
}

/// Caps for anything read back off disk (`CrashReport.FromJson`).
pub const MAX_CHAIN: usize = 20;
pub const MAX_FRAMES: usize = 200;
pub const MAX_NAME_CHARS: usize = 300;

/// `CrashReport.Identifier`: the characters a qualified function name uses,
/// everything else `_`. Rust's `::` is written as `.` first, so no colon,
/// slash, space or `@` survives and a path or URL cannot reach the wire.
#[must_use]
pub fn identifier(text: &str) -> String {
    text.replace("::", ".")
        .chars()
        .take(MAX_NAME_CHARS)
        .map(|c| {
            if c.is_alphanumeric()
                || matches!(
                    c,
                    '.' | '_' | '+' | '-' | '<' | '>' | '`' | '[' | ']' | ',' | '|'
                )
            {
                c
            } else {
                '_'
            }
        })
        .collect()
}

/// `$exception` from a stored report's `chain`: type and frame function names
/// only, never a message, file name or line. `None` when there is nothing to
/// group on, so the caller keeps the file. Every string is re-filtered: the
/// file is in the user's own folder and anything could have edited it.
#[must_use]
pub fn exception_properties(report: &Value) -> Option<Map<String, Value>> {
    let chain = report.get("chain")?.as_array()?;
    if chain.is_empty() {
        return None;
    }
    let mut items = Vec::new();
    for link in chain.iter().take(MAX_CHAIN) {
        let kind = identifier(
            link.get("type")
                .and_then(Value::as_str)
                .unwrap_or("Unknown"),
        );
        let frames: Vec<Value> = link
            .get("frames")
            .and_then(Value::as_array)
            .map(|frames| {
                frames
                    .iter()
                    .take(MAX_FRAMES)
                    .map(|frame| {
                        json!({
                            "platform": "custom",
                            "lang": "rust",
                            "function": identifier(frame.get("function").and_then(Value::as_str).unwrap_or("")),
                            "in_app": frame.get("inApp").and_then(Value::as_bool).unwrap_or(false),
                        })
                    })
                    .collect()
            })
            .unwrap_or_default();
        items.push(json!({
            "type": kind,
            "value": "",
            "mechanism": { "type": "generic", "handled": false, "synthetic": false },
            "stacktrace": { "type": "raw", "frames": frames },
        }));
    }
    let first = identifier(
        chain[0]
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("Unknown"),
    );
    let mut props = Telemetry::envelope();
    props.insert("$exception_list".into(), Value::Array(items));
    props.insert("$exception_type".into(), json!(first));
    for (key, field) in [("os", "os"), ("app_version", "app")] {
        if let Some(value) = report.get(field).and_then(Value::as_str) {
            props.insert(key.into(), json!(identifier(value)));
        }
    }
    if let Some(value) = report.get("osVersion").and_then(Value::as_str) {
        let printable: String = value.chars().filter(|c| !c.is_control()).collect();
        props.insert("os_version".into(), json!(truncate(&printable, 80)));
    }
    if let Some(value) = report.get("isDebug").and_then(Value::as_bool) {
        props.insert("is_debug".into(), json!(value));
    }
    Some(props)
}

/// The report a panic leaves on disk, shaped like `CrashPayload`: where, the
/// app and OS, and a one-link chain of function names from the backtrace.
/// The panic message and every file/line are dropped here, before the file
/// is written, because a message can quote the user's own data.
#[must_use]
pub fn panic_report(backtrace: &str, utc: &str) -> Value {
    let os = if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else {
        "other"
    };
    json!({
        "schema": 1,
        "where": "panic",
        "app": app_version(),
        "os": os,
        "osVersion": truncate(&os_description(), 80),
        "isDebug": cfg!(debug_assertions),
        "utc": utc,
        "chain": [{ "type": "panic", "frames": backtrace_frames(backtrace) }],
    })
}

/// Function names from `std::backtrace::Backtrace`'s text: the `N: name`
/// lines, hash suffix removed. The `at file:line` lines are skipped.
#[must_use]
pub fn backtrace_frames(backtrace: &str) -> Vec<Value> {
    backtrace
        .lines()
        .filter_map(|line| {
            let (index, name) = line.trim_start().split_once(": ")?;
            if index.is_empty() || !index.chars().all(|c| c.is_ascii_digit()) {
                return None;
            }
            let name = name.trim();
            let name = match name.rsplit_once("::h") {
                Some((head, hash))
                    if hash.len() == 16 && hash.chars().all(|c| c.is_ascii_hexdigit()) =>
                {
                    head
                }
                _ => name,
            };
            let function = identifier(name);
            let in_app = ["qcm_", "qs_bridge"].iter().any(|prefix| {
                function.starts_with(prefix) || function.starts_with(&format!("<{prefix}"))
            });
            Some(json!({ "function": function, "inApp": in_app }))
        })
        .take(MAX_FRAMES)
        .collect()
}

fn app_version() -> String {
    let version = env!("CARGO_PKG_VERSION");
    version.split('.').take(3).collect::<Vec<_>>().join(".")
}

fn os_description() -> String {
    format!("{} {}", std::env::consts::OS, std::env::consts::ARCH)
}

fn truncate(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

/// A random install id, made once. Never derived from the machine or user.
#[must_use]
pub fn new_install_id() -> Option<String> {
    let mut bytes = [0_u8; 16];
    getrandom::fill(&mut bytes).ok()?;
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    let hex: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
    Some(format!(
        "{}-{}-{}-{}-{}",
        &hex[0..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..32]
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Recorder {
        posts: Mutex<Vec<Value>>,
    }

    impl Sink for Recorder {
        fn post(&self, body: &Value) -> bool {
            lock(&self.posts).push(body.clone());
            true
        }
    }

    fn rig(token: &str, kill: bool) -> (Telemetry, Arc<Recorder>) {
        let sink = Arc::new(Recorder::default());
        let telemetry = Telemetry::new(token, kill, Arc::clone(&sink) as Arc<dyn Sink>)
            .with_window(Duration::from_millis(20));
        (telemetry, sink)
    }

    fn events(sink: &Recorder) -> Vec<Value> {
        lock(&sink.posts)
            .iter()
            .flat_map(|post| post["batch"].as_array().cloned().unwrap_or_default())
            .collect()
    }

    fn settle() {
        std::thread::sleep(Duration::from_millis(120));
    }

    // While a game runs nothing is queued, and the sender must sit in a
    // blocking receive: no timer, no periodic flush. One event is one post
    // after the window, and then it parks again.
    #[test]
    fn the_sender_parks_with_nothing_queued() {
        let sink = Arc::new(Recorder::default());
        let usage = Arc::new(AtomicBool::new(true));
        let (tx, rx) = mpsc::channel::<Msg>();
        let worker = {
            let sink = Arc::clone(&sink);
            let usage = Arc::clone(&usage);
            std::thread::spawn(move || {
                run_sender(&rx, &*sink, &usage, "t", Duration::from_millis(10));
            })
        };
        std::thread::sleep(Duration::from_millis(150));
        assert!(!worker.is_finished());
        assert!(lock(&sink.posts).is_empty());

        tx.send(Msg::Event(Captured {
            event: TelemetryEvent::AppLaunched.wire().to_owned(),
            distinct_id: "d".into(),
            properties: Map::new(),
            timestamp: "2026-10-15T00:00:00Z".into(),
        }))
        .unwrap();
        std::thread::sleep(Duration::from_millis(300));
        assert_eq!(lock(&sink.posts).len(), 1);

        // Hanging up wakes a parked receive at once.
        drop(tx);
        let deadline = std::time::Instant::now() + Duration::from_secs(1);
        while !worker.is_finished() && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(worker.is_finished());
        assert_eq!(lock(&sink.posts).len(), 1);
    }

    #[test]
    fn every_event_has_its_avalonia_wire_name() {
        let names: Vec<_> = [
            TelemetryEvent::AppLaunched,
            TelemetryEvent::ProfileOpened,
            TelemetryEvent::ProfileSaved,
            TelemetryEvent::InstallAttempted,
            TelemetryEvent::InstallSucceeded,
            TelemetryEvent::InstallFailed,
            TelemetryEvent::FeatureUsed,
            TelemetryEvent::FeedbackSubmitted,
            TelemetryEvent::StrengthMeasured,
        ]
        .iter()
        .map(|e| e.wire())
        .collect();
        assert_eq!(
            names,
            [
                "app_launched",
                "profile_opened",
                "profile_saved",
                "install_attempted",
                "install_succeeded",
                "install_failed",
                "feature_used",
                "feedback_submitted",
                "strength_measured"
            ]
        );
        assert_eq!(
            TelemetryEvent::from_wire("strength_measured"),
            Some(TelemetryEvent::StrengthMeasured)
        );
        assert_eq!(TelemetryEvent::from_wire("nope"), None);
    }

    #[test]
    fn the_allowlist_pairs_keys_with_events() {
        assert!(allowed("strength_measured", "puff"));
        assert!(allowed("install_succeeded", "setup_seconds"));
        assert!(allowed("app_launched", "os"));
        assert!(!allowed("app_launched", "text"));
        assert!(!allowed("profile_opened", "setup_seconds"));
        assert!(!allowed("feedback_submitted", "game"));
    }

    #[test]
    fn nothing_starts_or_sends_without_a_token() {
        let (telemetry, sink) = rig("", false);
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        assert!(!telemetry.is_live());
        assert!(!telemetry.track(TelemetryEvent::ProfileSaved));
        settle();
        assert!(events(&sink).is_empty());
    }

    #[test]
    fn the_kill_switch_beats_consent() {
        let (telemetry, sink) = rig("phc_test", true);
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        assert!(!telemetry.is_live());
        assert!(telemetry.disabled_by_environment());
        assert!(!telemetry.strength_measured(50, 40));
        settle();
        assert!(events(&sink).is_empty());
    }

    #[test]
    fn an_old_notice_is_not_consent() {
        let (telemetry, _) = rig("phc_test", false);
        telemetry.apply_consent(1, true, "id-1");
        assert!(!telemetry.is_live());
        assert!(!telemetry.track(TelemetryEvent::ProfileSaved));
    }

    #[test]
    fn consent_sends_a_launch_once_and_batched_events_with_the_envelope() {
        let (telemetry, sink) = rig("phc_test", false);
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        assert!(telemetry.is_live());
        assert!(telemetry.opened(ProfileSource::Library, Some("minecraft")));
        assert!(telemetry.saved());
        assert!(telemetry.installed());
        assert!(telemetry.strength_measured(62, 4));
        settle();
        let sent = events(&sink);
        let names: Vec<_> = sent
            .iter()
            .map(|e| e["event"].as_str().unwrap_or(""))
            .collect();
        assert_eq!(
            names,
            [
                "app_launched",
                "profile_opened",
                "profile_saved",
                "install_succeeded",
                "strength_measured"
            ]
        );
        assert_eq!(lock(&sink.posts)[0]["api_key"], "phc_test");
        for event in &sent {
            assert_eq!(event["distinct_id"], "id-1");
            assert_eq!(event["properties"]["$geoip_disable"], true);
            assert!(event["properties"]["os"].is_string());
            assert!(event["properties"]["app_version"].is_string());
        }
        assert_eq!(sent[0]["properties"].get("game"), None);
        assert_eq!(sent[1]["properties"]["source"], "library");
        assert_eq!(sent[1]["properties"]["game"], "minecraft");
        assert_eq!(sent[2]["properties"]["game"], "minecraft");
        assert!(sent[3]["properties"]["setup_seconds"].is_u64());
        assert_eq!(sent[4]["properties"]["puff"], 62);
        assert_eq!(sent[4]["properties"]["sip"], 4);
        assert_eq!(sent[4]["properties"].get("game"), None);
    }

    #[test]
    fn withdrawing_consent_drops_what_is_queued_and_stops() {
        let sink = Arc::new(Recorder::default());
        let telemetry = Telemetry::new("phc_test", false, Arc::clone(&sink) as Arc<dyn Sink>)
            .with_window(Duration::from_secs(60));
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        telemetry.saved();
        telemetry.apply_consent(NOTICE_VERSION, false, "");
        assert!(!telemetry.is_live());
        assert!(!telemetry.saved());
        settle();
        assert!(events(&sink).is_empty());
    }

    #[test]
    fn feedback_waits_for_the_batch_and_is_capped() {
        let (telemetry, sink) = rig("phc_test", false);
        assert!(!telemetry.send_feedback("hello"));
        telemetry.apply_consent(NOTICE_VERSION, true, "id-1");
        assert!(!telemetry.send_feedback("   "));
        let long = "a".repeat(MAX_FEEDBACK_CHARS + 50);
        assert!(telemetry.send_feedback(&long));
        let sent = events(&sink);
        let feedback = sent
            .iter()
            .find(|e| e["event"] == "feedback_submitted")
            .expect("sent");
        assert_eq!(
            feedback["properties"]["text"].as_str().map(str::len),
            Some(MAX_FEEDBACK_CHARS)
        );
    }

    #[test]
    fn a_crash_report_needs_a_chain_and_never_carries_its_message() {
        let (telemetry, sink) = rig("phc_test", false);
        assert!(!telemetry.send_crash_report(&json!({ "message": "secret" }), "id-1"));
        let report = json!({
            "chain": [{ "type": "System.IO.IOException", "message": "C:/Users/x/secret.csv",
                        "frames": [{ "function": "Save", "inApp": true, "filename": "Main.cs" }] }],
        });
        assert!(!telemetry.send_crash_report(&report, ""));
        assert!(telemetry.send_crash_report(&report, "id-1"));
        // No standing consent: the sender started for the report goes away.
        assert!(!telemetry.is_live());
        let sent = events(&sink);
        assert_eq!(sent.len(), 1);
        let text = sent[0].to_string();
        assert!(!text.contains("secret"));
        assert!(!text.contains("Main.cs"));
        assert!(!text.contains(CRASH_CONSENT_MARKER));
        assert_eq!(
            sent[0]["properties"]["$exception_type"],
            "System.IO.IOException"
        );
    }

    #[test]
    fn install_ids_are_random_v4() {
        let a = new_install_id().expect("random");
        let b = new_install_id().expect("random");
        assert_ne!(a, b);
        assert_eq!(a.len(), 36);
        assert_eq!(&a[14..15], "4");
    }

    #[test]
    fn a_panic_report_carries_function_names_and_never_files_or_messages() {
        let backtrace = "   0: std::backtrace::Backtrace::force_capture\n\
                         \x20            at /rustc/abc/library/std/src/backtrace.rs:312:9\n\
                         \x20  1: qcm_tauri_lib::diagnostics::install_panic_hook::{{closure}}::h0123456789abcdef\n\
                         \x20            at /Users/bassam/qcm/src-tauri/src/diagnostics.rs:285:25\n\
                         \x20  2: <qcm_core::Foo as core::fmt::Debug>::fmt\n\
                         \x20  3: main\n";
        let report = panic_report(backtrace, "2026-10-15T00:00:00Z");
        let text = report.to_string();
        assert!(!text.contains("bassam"));
        assert!(!text.contains(".rs"));
        assert!(report.get("message").is_none());
        let frames = report["chain"][0]["frames"].as_array().expect("frames");
        assert_eq!(frames.len(), 4);
        assert_eq!(
            frames[0]["function"],
            "std.backtrace.Backtrace.force_capture"
        );
        assert_eq!(frames[0]["inApp"], false);
        assert_eq!(
            frames[1]["function"],
            "qcm_tauri_lib.diagnostics.install_panic_hook.__closure__"
        );
        assert_eq!(frames[1]["inApp"], true);
        assert_eq!(
            frames[2]["function"],
            "<qcm_core.Foo_as_core.fmt.Debug>.fmt"
        );
        assert_eq!(frames[2]["inApp"], true);
        assert_eq!(report["chain"][0]["type"], "panic");
        let props = exception_properties(&report).expect("sendable");
        assert_eq!(props["$exception_type"], "panic");
    }

    #[test]
    fn a_hand_edited_report_cannot_put_a_path_on_the_wire() {
        let report = json!({
            "chain": [{ "type": "C:/Users/x/a.csv", "frames": [{ "function": "/home/x/b.csv" }] }],
            "app": "https://example.com",
        });
        let text = Value::Object(exception_properties(&report).expect("sendable")).to_string();
        assert!(!text.contains('/'));
        assert!(!text.contains(':') || !text.contains("C:/"));
    }

    #[test]
    fn only_the_four_cancel_reasons_come_from_a_window() {
        for (wire, before) in [
            ("cancelled_device", true),
            ("cancelled_folder", true),
            ("cancelled_default", false),
            ("cancelled_preferences", false),
        ] {
            let reason = InstallFailure::cancelled_from_wire(wire).expect(wire);
            assert_eq!(reason.wire(), wire);
            assert_eq!(reason.before_prepare(), before);
        }
        for refused in [
            "io_error",
            "has_errors",
            "no_profile",
            "not_a_quadstick",
            "",
        ] {
            assert!(
                InstallFailure::cancelled_from_wire(refused).is_none(),
                "{refused}"
            );
        }
    }
}
