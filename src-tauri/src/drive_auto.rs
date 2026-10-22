//! Backup on every save (`MainWindow.FireBackupPush` / `FireBackupRetry`).
//!
//! A save, a Save As, a save on close and a Get the latest replace hand the
//! saved profile to Drive in the background; opening a profile whose last
//! push failed tries again. The save never waits on the network. What came of
//! the push reaches the window over a channel, so a conflict or a missing
//! sheet asks the same question the Back up button does.

use crate::drive::{DriveBackupOutcomeDto, DriveService};
use crate::shell::DriveProfileSnapshot;
use crate::streaming::SubscriptionDto;
use qcm_core::error::QcmError;
use serde::Serialize;
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use tauri::State;
use tauri::ipc::Channel;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum DriveBackupEventDto {
    Pushed,
    /// The push failed and the link is marked dirty; the next save or open
    /// tries again.
    Pending,
    Conflict {
        resolution_id: String,
    },
    Missing {
        resolution_id: String,
    },
    /// A newer save of the same profile is being pushed; that push asks
    /// again if it still has to, so this unanswered question is void.
    Superseded {
        resolution_id: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackupMode {
    AfterSave,
    RetryIfDirty,
}

/// Run one background push now, on this thread. `None`: backup is off, a
/// retry found nothing dirty, or a newer save of the profile superseded it.
pub fn run_backup(
    drive: &DriveService,
    profile: DriveProfileSnapshot,
    mode: BackupMode,
) -> Option<DriveBackupEventDto> {
    let ticket = ticket_for(drive, &profile.persistent_key, mode);
    run_ticketed(drive, profile, mode, ticket, &|_| {})
}

/// A save takes a new ticket, in save order. A retry only reads the newest
/// one, so it never supersedes a save queued before it.
fn ticket_for(drive: &DriveService, key: &str, mode: BackupMode) -> u64 {
    match mode {
        BackupMode::AfterSave => drive.ticket(key),
        BackupMode::RetryIfDirty => drive.current_ticket(key),
    }
}

fn run_ticketed(
    drive: &DriveService,
    profile: DriveProfileSnapshot,
    mode: BackupMode,
    ticket: u64,
    deliver: &dyn Fn(DriveBackupEventDto),
) -> Option<DriveBackupEventDto> {
    let result = match mode {
        BackupMode::AfterSave => drive.backup_after_save(profile, ticket, &|resolution_id| {
            deliver(DriveBackupEventDto::Superseded { resolution_id });
        }),
        BackupMode::RetryIfDirty => drive.retry_if_dirty(profile, ticket, &|resolution_id| {
            deliver(DriveBackupEventDto::Superseded { resolution_id });
        }),
    }?;
    Some(event_of(result))
}

fn event_of(result: Result<DriveBackupOutcomeDto, QcmError>) -> DriveBackupEventDto {
    match result {
        Ok(DriveBackupOutcomeDto::Pushed { .. }) => {
            // Counted on a real push, as the shipped app does.
            crate::telemetry::global().feature_used(crate::telemetry::AppFeature::DriveBackup);
            DriveBackupEventDto::Pushed
        }
        Ok(DriveBackupOutcomeDto::Conflict { resolution_id }) => {
            DriveBackupEventDto::Conflict { resolution_id }
        }
        Ok(DriveBackupOutcomeDto::Missing { resolution_id }) => {
            DriveBackupEventDto::Missing { resolution_id }
        }
        // A plain push never turns backup off; only a resolve does.
        Ok(DriveBackupOutcomeDto::Disabled) | Err(_) => DriveBackupEventDto::Pending,
    }
}

/// Fire and forget: the caller has already returned its own answer.
pub fn spawn_backup(
    drive: Arc<DriveService>,
    profile: Option<DriveProfileSnapshot>,
    mode: BackupMode,
    deliver: impl Fn(DriveBackupEventDto) + Send + 'static,
) {
    let Some(profile) = profile else { return };
    // Taken here, on the saving thread, so save order holds.
    let ticket = ticket_for(&drive, &profile.persistent_key, mode);
    std::thread::spawn(move || {
        if let Some(event) = run_ticketed(&drive, profile, mode, ticket, &deliver) {
            deliver(event);
        }
    });
}

/// The commands' entry: push in the background and tell every window.
pub fn fire(
    drive: &State<'_, Arc<DriveService>>,
    hub: &State<'_, Arc<DriveBackupHub>>,
    profile: Option<DriveProfileSnapshot>,
    mode: BackupMode,
) {
    let hub = Arc::clone(hub.inner());
    spawn_backup(Arc::clone(drive.inner()), profile, mode, move |event| {
        hub.notify(&event);
    });
}

/// The push a save or an open makes on its own, when this run backs up on
/// save. A host with backup or network off has nothing to push to.
pub fn fire_auto<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    profile: Option<DriveProfileSnapshot>,
    mode: BackupMode,
) {
    use tauri::Manager;
    if !crate::host::drive_auto_backup() {
        return;
    }
    let (Some(drive), Some(hub)) = (
        app.try_state::<Arc<DriveService>>(),
        app.try_state::<Arc<DriveBackupHub>>(),
    ) else {
        return;
    };
    fire(&drive, &hub, profile, mode);
}

#[derive(Default)]
pub struct DriveBackupHub {
    listeners: Mutex<BTreeMap<u64, Channel<DriveBackupEventDto>>>,
    next_id: AtomicU64,
}

impl DriveBackupHub {
    fn listeners(&self) -> MutexGuard<'_, BTreeMap<u64, Channel<DriveBackupEventDto>>> {
        self.listeners
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    pub fn subscribe(&self, channel: Channel<DriveBackupEventDto>) -> SubscriptionDto {
        let id = self
            .next_id
            .fetch_add(1, Ordering::Relaxed)
            .saturating_add(1);
        self.listeners().insert(id, channel);
        SubscriptionDto {
            subscription_id: format!("drive-backups-{id}"),
        }
    }

    pub fn unsubscribe(&self, raw: &str) {
        if let Some(id) = raw
            .strip_prefix("drive-backups-")
            .and_then(|n| n.parse().ok())
        {
            self.listeners().remove(&id);
        }
    }

    /// A channel that fails belongs to a window that went away; drop it.
    pub fn notify(&self, event: &DriveBackupEventDto) {
        let listeners: Vec<(u64, Channel<DriveBackupEventDto>)> = self
            .listeners()
            .iter()
            .map(|(&id, channel)| (id, channel.clone()))
            .collect();
        let failed: Vec<u64> = listeners
            .into_iter()
            .filter_map(|(id, channel)| channel.send(event.clone()).err().map(|_| id))
            .collect();
        if !failed.is_empty() {
            let mut listeners = self.listeners();
            for id in failed {
                listeners.remove(&id);
            }
        }
    }
}

#[tauri::command]
pub fn subscribe_drive_backups(
    hub: State<'_, Arc<DriveBackupHub>>,
    on_event: Channel<DriveBackupEventDto>,
) -> SubscriptionDto {
    hub.subscribe(on_event)
}

#[tauri::command]
pub fn unsubscribe_drive_backups(hub: State<'_, Arc<DriveBackupHub>>, subscription_id: String) {
    hub.unsubscribe(&subscription_id);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drive::{ConflictChoice, DriveAuth, DriveResolution};
    use qcm_config::ProfileFile;
    use std::io::{BufRead, BufReader, Read, Write};
    use std::net::{TcpListener, TcpStream};
    use std::path::{Path, PathBuf};
    use std::sync::mpsc;
    use std::time::{Duration, Instant};

    struct FakeAuth(bool);
    impl DriveAuth for FakeAuth {
        fn connected(&self) -> bool {
            self.0
        }
        fn access_token(&self) -> Result<String, QcmError> {
            Ok("token".to_owned())
        }
    }

    #[derive(Default)]
    struct FakeDrive {
        modified: u64,
        pushes: Vec<String>,
        creates: u32,
        down: bool,
    }

    /// A Drive and Sheets that answer on localhost: one sheet, `s1`.
    fn serve(drive: Arc<Mutex<FakeDrive>>) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind fake Drive");
        let base = format!("http://{}", listener.local_addr().expect("addr"));
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                answer(stream, &drive);
            }
        });
        base
    }

    fn answer(mut stream: TcpStream, drive: &Mutex<FakeDrive>) {
        let mut reader = BufReader::new(stream.try_clone().expect("clone"));
        let mut line = String::new();
        if reader.read_line(&mut line).is_err() {
            return;
        }
        let mut length = 0;
        loop {
            let mut header = String::new();
            if reader.read_line(&mut header).is_err() || header.trim().is_empty() {
                break;
            }
            if let Some(value) = header.to_ascii_lowercase().strip_prefix("content-length:") {
                length = value.trim().parse().unwrap_or(0);
            }
        }
        let mut body = vec![0; length];
        let _ = reader.read_exact(&mut body);
        let mut parts = line.split_whitespace();
        let method = parts.next().unwrap_or("");
        let target = parts.next().unwrap_or("");
        let path = target.split('?').next().unwrap_or("");
        let mut d = drive.lock().unwrap_or_else(PoisonError::into_inner);
        let (status, reply) = if d.down {
            ("503 Service Unavailable", "{}".to_owned())
        } else {
            match (method, path) {
                ("POST", "/sheets") => {
                    d.creates += 1;
                    d.modified += 1;
                    ("200 OK", r#"{"spreadsheetId":"s1"}"#.to_owned())
                }
                ("GET", "/sheets/s1") => ("200 OK", r#"{"sheets":[]}"#.to_owned()),
                ("POST", "/sheets/s1/values:batchUpdate") => {
                    d.pushes.push(String::from_utf8_lossy(&body).into_owned());
                    d.modified += 1;
                    ("200 OK", "{}".to_owned())
                }
                ("POST", "/sheets/s1:batchUpdate" | "/sheets/s1/values:batchClear") => {
                    ("200 OK", "{}".to_owned())
                }
                ("GET", "/drive/s1") => {
                    ("200 OK", format!(r#"{{"modifiedTime":"t{}"}}"#, d.modified))
                }
                _ => ("404 Not Found", "{}".to_owned()),
            }
        };
        drop(d);
        let _ = write!(
            stream,
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{reply}",
            reply.len()
        );
    }

    fn state_file(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "qcm-drive-auto-{name}-{}-{:?}.json",
            std::process::id(),
            Instant::now()
        ));
        let _ = std::fs::remove_file(&path);
        path
    }

    fn service(base: &str, connected: bool, state: &Path) -> DriveService {
        DriveService::with_endpoints(
            Arc::new(FakeAuth(connected)),
            &format!("{base}/sheets"),
            &format!("{base}/drive"),
            Some(state.to_path_buf()),
        )
        .expect("service")
    }

    fn profile(lip: &str) -> DriveProfileSnapshot {
        DriveProfileSnapshot {
            persistent_key: "/profiles/Game.csv".to_owned(),
            display_name: "Game.csv".to_owned(),
            revision: 1,
            file: ProfileFile::load(&format!("Game,,\nmode,1,\nlip,{lip},\n")),
        }
    }

    fn dirty(state: &Path) -> bool {
        std::fs::read_to_string(state)
            .expect("state written")
            .contains("\"backupDirty\": true")
    }

    /// A port nothing listens on: refused at once, like a machine offline.
    fn offline_base() -> String {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
        let base = format!("http://{}", listener.local_addr().expect("addr"));
        drop(listener);
        base
    }

    #[test]
    fn a_replace_or_save_pushes_the_new_profile_when_backup_is_on() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("push");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);

        // First save creates the sheet; the replace after it pushes to it.
        assert_eq!(
            run_backup(&drive, profile("mouse"), BackupMode::AfterSave),
            Some(DriveBackupEventDto::Pushed)
        );
        assert_eq!(
            run_backup(&drive, profile("joystick"), BackupMode::AfterSave),
            Some(DriveBackupEventDto::Pushed)
        );
        let d = fake.lock().expect("fake");
        assert_eq!(d.creates, 1, "one sheet, not one per save");
        assert_eq!(d.pushes.len(), 2);
        assert!(
            d.pushes[1].contains("joystick"),
            "the replaced bytes went up"
        );
        drop(d);
        assert!(!dirty(&state));
    }

    #[test]
    fn an_older_save_that_reaches_drive_late_never_overwrites_a_newer_one() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("order");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);
        let older = drive.ticket("/profiles/Game.csv");
        let newer = drive.ticket("/profiles/Game.csv");
        // The newer save's thread got to the gate first.
        assert_eq!(
            run_ticketed(
                &drive,
                profile("joystick"),
                BackupMode::AfterSave,
                newer,
                &|_| {}
            ),
            Some(DriveBackupEventDto::Pushed)
        );
        assert_eq!(
            run_ticketed(
                &drive,
                profile("mouse"),
                BackupMode::AfterSave,
                older,
                &|_| {}
            ),
            None
        );
        let d = fake.lock().expect("fake");
        assert_eq!(d.pushes.len(), 1);
        assert!(d.pushes[0].contains("joystick"));
    }

    #[test]
    fn backup_off_never_touches_drive() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("off");
        let drive = service(&serve(Arc::clone(&fake)), false, &state);
        assert_eq!(
            run_backup(&drive, profile("mouse"), BackupMode::AfterSave),
            None
        );
        assert_eq!(fake.lock().expect("fake").creates, 0);
    }

    #[test]
    fn a_failed_push_marks_the_link_dirty_and_the_next_open_retries_it() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("retry");
        let base = serve(Arc::clone(&fake));
        let drive = service(&base, true, &state);
        run_backup(&drive, profile("mouse"), BackupMode::AfterSave);
        // Nothing dirty: an open does not push.
        assert_eq!(
            run_backup(&drive, profile("mouse"), BackupMode::RetryIfDirty),
            None
        );

        fake.lock().expect("fake").down = true;
        assert_eq!(
            run_backup(&drive, profile("joystick"), BackupMode::AfterSave),
            Some(DriveBackupEventDto::Pending)
        );
        assert!(
            dirty(&state),
            "the failure is on disk, so a restart retries too"
        );

        // A new run reads the dirty mark back and retries on open.
        fake.lock().expect("fake").down = false;
        let restarted = service(&base, true, &state);
        assert_eq!(
            run_backup(&restarted, profile("joystick"), BackupMode::RetryIfDirty),
            Some(DriveBackupEventDto::Pushed)
        );
        assert!(!dirty(&state));
        let d = fake.lock().expect("fake");
        assert!(
            d.pushes
                .last()
                .is_some_and(|body| body.contains("joystick"))
        );
    }

    #[test]
    fn an_online_edit_asks_and_either_answer_settles_the_link() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("conflict");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);
        run_backup(&drive, profile("mouse"), BackupMode::AfterSave);

        // Someone edits the sheet online: the save asks instead of writing.
        fake.lock().expect("fake").modified += 1;
        let Some(DriveBackupEventDto::Conflict { resolution_id }) =
            run_backup(&drive, profile("joystick"), BackupMode::AfterSave)
        else {
            panic!("expected a conflict");
        };
        assert_eq!(
            fake.lock().expect("fake").pushes.len(),
            1,
            "nothing written yet"
        );
        let resolved = drive
            .resolve(&resolution_id, ConflictChoice::ReplaceWithMine)
            .expect("resolve");
        assert!(matches!(
            resolved,
            DriveResolution::Finished(DriveBackupOutcomeDto::Pushed { .. })
        ));
        assert_eq!(fake.lock().expect("fake").pushes.len(), 2);
        assert!(!dirty(&state));
    }

    #[test]
    fn a_newer_save_voids_an_older_unanswered_conflict() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("void");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);
        run_backup(&drive, profile("mouse"), BackupMode::AfterSave);
        fake.lock().expect("fake").modified += 1;
        let Some(DriveBackupEventDto::Conflict {
            resolution_id: older,
        }) = run_backup(&drive, profile("joystick"), BackupMode::AfterSave)
        else {
            panic!("expected a conflict");
        };
        let voided = Mutex::new(Vec::new());
        let ticket = drive.ticket("/profiles/Game.csv");
        let next = run_ticketed(
            &drive,
            profile("buttons"),
            BackupMode::AfterSave,
            ticket,
            &|event| voided.lock().expect("voided").push(event),
        );
        assert!(matches!(next, Some(DriveBackupEventDto::Conflict { .. })));
        assert_eq!(
            *voided.lock().expect("voided"),
            vec![DriveBackupEventDto::Superseded {
                resolution_id: older.clone()
            }]
        );
        // Yes on the old question can no longer push the older bytes.
        assert!(
            drive
                .resolve(&older, ConflictChoice::ReplaceWithMine)
                .is_err()
        );
        assert_eq!(fake.lock().expect("fake").pushes.len(), 1);
    }

    // A save queued behind other Drive work, then the same file opened: the
    // open's retry must not stand the save down, or the save never uploads.
    #[test]
    fn an_open_right_after_a_save_does_not_cancel_its_push() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("open-after-save");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);
        run_backup(&drive, profile("mouse"), BackupMode::AfterSave);

        let save = ticket_for(&drive, "/profiles/Game.csv", BackupMode::AfterSave);
        // The open runs first and finds nothing dirty yet.
        assert_eq!(
            run_backup(&drive, profile("joystick"), BackupMode::RetryIfDirty),
            None
        );
        assert_eq!(
            run_ticketed(
                &drive,
                profile("joystick"),
                BackupMode::AfterSave,
                save,
                &|_| {}
            ),
            Some(DriveBackupEventDto::Pushed)
        );
        let d = fake.lock().expect("fake");
        assert!(
            d.pushes
                .last()
                .is_some_and(|body| body.contains("joystick"))
        );
    }

    // A Back up press after an unanswered question: the old question goes, so
    // a late Yes to it cannot put the older bytes back over the newer ones.
    #[test]
    fn a_back_up_press_voids_older_questions() {
        let fake = Arc::new(Mutex::new(FakeDrive::default()));
        let state = state_file("press-void");
        let drive = service(&serve(Arc::clone(&fake)), true, &state);
        run_backup(&drive, profile("mouse"), BackupMode::AfterSave);
        fake.lock().expect("fake").modified += 1;
        let Some(DriveBackupEventDto::Conflict {
            resolution_id: older,
        }) = run_backup(&drive, profile("joystick"), BackupMode::AfterSave)
        else {
            panic!("expected a conflict");
        };
        let _ = drive.backup(profile("buttons"));
        assert!(
            drive
                .resolve(&older, ConflictChoice::ReplaceWithMine)
                .is_err()
        );
    }

    #[test]
    fn offline_does_not_hold_up_the_save_or_replace() {
        let state = state_file("offline");
        let drive = Arc::new(service(&offline_base(), true, &state));
        let (tx, rx) = mpsc::channel();
        let started = Instant::now();
        spawn_backup(
            Arc::clone(&drive),
            Some(profile("mouse")),
            BackupMode::AfterSave,
            move |event| {
                let _ = tx.send(event);
            },
        );
        // The caller is back before the network has answered anything.
        assert!(started.elapsed() < Duration::from_millis(50));
        assert_eq!(
            rx.recv_timeout(Duration::from_secs(10)).expect("event"),
            DriveBackupEventDto::Pending
        );
    }

    #[test]
    fn a_profile_drive_never_backs_up_is_not_spawned() {
        let state = state_file("none");
        let drive = Arc::new(service(&offline_base(), true, &state));
        let (tx, rx) = mpsc::channel::<DriveBackupEventDto>();
        spawn_backup(drive, None, BackupMode::AfterSave, move |event| {
            let _ = tx.send(event);
        });
        assert!(rx.recv_timeout(Duration::from_millis(200)).is_err());
    }
}
