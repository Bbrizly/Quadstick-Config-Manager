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
            kill_switch_from_env(),
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
        let _ = self.flush(SHUTDOWN_FLUSH);
        self.usage.store(false, Ordering::SeqCst);
        let taken = lock(&self.worker).take();
        if let Some(mut worker) = taken {
            let _ = worker.tx.send(Msg::Stop);
            if let Some(handle) = worker.handle.take() {
                let _ = handle.join();
            }
        }
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

/// `$exception` from a stored report's `chain`: type and frame function names
/// only, never a message, file name or line. `None` when there is nothing to
/// group on, so the caller keeps the file.
#[must_use]
pub fn exception_properties(report: &Value) -> Option<Map<String, Value>> {
    let chain = report.get("chain")?.as_array()?;
    if chain.is_empty() {
        return None;
    }
    let mut items = Vec::new();
    for link in chain {
        let kind = link
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("Unknown");
        let frames: Vec<Value> = link
            .get("frames")
            .and_then(Value::as_array)
            .map(|frames| {
                frames
                    .iter()
                    .map(|frame| {
                        json!({
                            "platform": "custom",
                            "lang": "rust",
                            "function": frame.get("function").and_then(Value::as_str).unwrap_or(""),
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
    let first = chain[0]
        .get("type")
        .and_then(Value::as_str)
        .unwrap_or("Unknown");
    let mut props = Telemetry::envelope();
    props.insert("$exception_list".into(), Value::Array(items));
    props.insert("$exception_type".into(), json!(first));
    for (key, field) in [
        ("os", "os"),
        ("os_version", "osVersion"),
        ("app_version", "app"),
        ("is_debug", "isDebug"),
    ] {
        if let Some(value) = report.get(field) {
            props.insert(key.into(), value.clone());
        }
    }
    Some(props)
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
}
