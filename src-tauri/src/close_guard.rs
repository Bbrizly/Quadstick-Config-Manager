//! Closing the window with unsaved work asks first (`MainWindow.Closing`).
//!
//! The close is held and the window is told; it shows Save / Don't save /
//! Cancel, and only its answer lets the app go. With nothing unsaved, or no
//! window listening (a page that failed to load), the close goes through and
//! the rescue draft keeps the work, so a broken page can never trap the user.
//! A send only means the request was handed to the WebView, so the page says
//! it got it; a close whose request was never acknowledged lets the next one
//! through.

use std::sync::{Mutex, MutexGuard, PoisonError};
use tauri::State;

type Listener = Box<dyn Fn() -> bool + Send>;

#[derive(Default)]
pub struct CloseGuard {
    inner: Mutex<Inner>,
}

#[derive(Default)]
struct Inner {
    listener: Option<Listener>,
    /// Which subscription the listener belongs to, so a late unsubscribe from
    /// a window that remounted cannot remove its replacement.
    generation: u64,
    /// The window answered; the next close or exit goes through.
    released: bool,
    /// A request was sent and the page has not said it got it.
    unacknowledged: bool,
}

impl CloseGuard {
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Whether to hold this close and ask the window. True only when there is
    /// unsaved work and the window took the request.
    pub fn hold(&self, unsaved: bool) -> bool {
        let mut inner = self.lock();
        if inner.released || !unsaved {
            return false;
        }
        if inner.unacknowledged {
            // The last request reached no live page: let this one go, and the
            // exit after it, rather than keep a window nobody can close.
            inner.released = true;
            return false;
        }
        let asked = inner.listener.as_ref().is_some_and(|ask| ask());
        inner.unacknowledged = asked;
        asked
    }

    /// The page got the request and is asking the person.
    pub fn acknowledge(&self) {
        self.lock().unacknowledged = false;
    }

    pub fn subscribe(&self, listener: Listener) -> u64 {
        let mut inner = self.lock();
        inner.listener = Some(listener);
        inner.generation += 1;
        inner.generation
    }

    pub fn unsubscribe(&self, generation: u64) {
        let mut inner = self.lock();
        if generation == inner.generation {
            inner.listener = None;
        }
    }

    /// The unsaved work was saved or dropped on purpose.
    pub fn release(&self) {
        self.lock().released = true;
    }
}

#[tauri::command]
pub fn subscribe_close_requests(
    guard: State<'_, CloseGuard>,
    on_request: tauri::ipc::Channel<bool>,
) -> crate::streaming::SubscriptionDto {
    let generation = guard.subscribe(Box::new(move || on_request.send(true).is_ok()));
    crate::streaming::SubscriptionDto {
        subscription_id: format!("close-{generation}"),
    }
}

#[tauri::command]
pub fn unsubscribe_close_requests(guard: State<'_, CloseGuard>, subscription_id: String) {
    if let Some(generation) = subscription_id
        .strip_prefix("close-")
        .and_then(|n| n.parse().ok())
    {
        guard.unsubscribe(generation);
    }
}

#[tauri::command]
pub fn ack_close_request(guard: State<'_, CloseGuard>) {
    guard.acknowledge();
}

/// The window settled the unsaved work (saved, or Don't save); quit now.
#[tauri::command]
pub fn quit_app(app: tauri::AppHandle, guard: State<'_, CloseGuard>) {
    guard.release();
    app.exit(0);
}

#[cfg(test)]
mod tests {
    use super::CloseGuard;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn counting(guard: &CloseGuard) -> (Arc<AtomicUsize>, u64) {
        let asked = Arc::new(AtomicUsize::new(0));
        let seen = Arc::clone(&asked);
        let generation = guard.subscribe(Box::new(move || {
            seen.fetch_add(1, Ordering::SeqCst);
            true
        }));
        (asked, generation)
    }

    #[test]
    fn a_clean_window_closes_without_asking() {
        let guard = CloseGuard::default();
        let (asked, _) = counting(&guard);
        assert!(!guard.hold(false));
        assert_eq!(asked.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn unsaved_work_holds_the_close_and_asks_the_window() {
        let guard = CloseGuard::default();
        let (asked, _) = counting(&guard);
        assert!(guard.hold(true));
        assert_eq!(asked.load(Ordering::SeqCst), 1);
        // A live page says it got it, and is asked again next time.
        guard.acknowledge();
        assert!(guard.hold(true));
        assert_eq!(asked.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn a_page_that_never_acknowledges_cannot_keep_the_window_open() {
        let guard = CloseGuard::default();
        let (asked, _) = counting(&guard);
        assert!(guard.hold(true));
        // No acknowledgement: the second close goes through, and so does the
        // exit that follows it.
        assert!(!guard.hold(true));
        assert!(!guard.hold(true));
        assert_eq!(asked.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn with_no_window_listening_the_close_goes_through() {
        let guard = CloseGuard::default();
        assert!(!guard.hold(true));
        let (_, generation) = counting(&guard);
        guard.unsubscribe(generation);
        assert!(!guard.hold(true));
        // A listener whose window is gone cannot hold it either.
        guard.subscribe(Box::new(|| false));
        assert!(!guard.hold(true));
    }

    #[test]
    fn a_stale_unsubscribe_keeps_the_new_listener() {
        let guard = CloseGuard::default();
        let (_, old) = counting(&guard);
        let (asked, _) = counting(&guard);
        guard.unsubscribe(old);
        assert!(guard.hold(true));
        assert_eq!(asked.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn the_windows_answer_lets_the_next_close_through() {
        let guard = CloseGuard::default();
        let (asked, _) = counting(&guard);
        guard.release();
        assert!(!guard.hold(true));
        assert_eq!(asked.load(Ordering::SeqCst), 0);
    }
}
