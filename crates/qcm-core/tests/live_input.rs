//! Live input: what the window is shown, and what it must stop being shown.
//!
//! No hardware. The fake stages the three things that decide whether this code
//! is safe: a device that reports faster than anyone reads, a device that goes
//! away while a button is held, and a device that keeps reporting for hours.
//!
//! The one that matters most is the middle one. A held button drawn by a reader
//! that is no longer reading is somebody's input stuck down with nothing left
//! that can release it.

use qcm_core::clock::ManualClock;
use qcm_core::error::{DeviceError, ErrorCode};
use qcm_core::live::{LiveInputManager, LiveInputSettings, LiveStatus};
use qcm_testkit::FakeLiveInput;
use std::time::Duration;

fn settings() -> LiveInputSettings {
    LiveInputSettings {
        stale_after: Duration::from_secs(2),
        rescan_after: Duration::from_millis(1500),
        reconnect_after: Duration::from_millis(1000),
        error_after: Duration::from_millis(2000),
    }
}

type Manager<'a> = LiveInputManager<FakeLiveInput, &'a ManualClock>;

fn manager<'a>(fake: &FakeLiveInput, clock: &'a ManualClock) -> Manager<'a> {
    LiveInputManager::with_settings(fake.clone(), clock, settings())
}

/// Search, open, and take the first report. Leaves the manager reading.
fn reading<'a>(fake: &FakeLiveInput, clock: &'a ManualClock) -> Manager<'a> {
    let mut live = manager(fake, clock);
    live.start();
    live.poll(); // finds and opens
    live.poll(); // first report
    live
}

#[test]
fn nothing_plugged_in_is_a_state_and_not_an_error() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    let mut live = manager(&fake, &clock);

    live.start();
    live.poll();

    assert_eq!(live.status(), LiveStatus::Searching);
    assert_eq!(fake.opens(), 0);
}

#[test]
fn a_quadstick_in_xbox_360_native_mode_is_named_rather_than_hidden() {
    // Emulation mode 3 publishes interface class 0xFF, which is XInput and not
    // HID. Reporting it as "nothing plugged in" would leave the user staring at
    // a live view that never moves with no reason given.
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    let mut live = manager(&fake, &clock);
    fake.plug_in_xinput_only();

    live.start();
    live.poll();

    assert_eq!(live.status(), LiveStatus::XInputOnly);
    assert_eq!(
        fake.opens(),
        0,
        "nothing may try to open an XInput interface"
    );
}

#[test]
fn a_report_reaches_the_window_as_axes_and_buttons() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.5, -0.25, [1u16, 4]);

    let live = reading(&fake, &clock);
    let stream = live.stream();
    let snapshot = stream.take().expect("a snapshot");

    assert_eq!(snapshot.status.product(), Some("QuadStick"));
    let motion = snapshot.motion().expect("motion");
    assert!((motion.x() - 0.5).abs() < f64::EPSILON);
    assert!((motion.y() + 0.25).abs() < f64::EPSILON);
    assert_eq!(motion.buttons(), &[1, 4]);
}

// The dangerous one.
//
// A button is held, the stick is pulled out, and the window is slow. Every
// pressed frame is still sitting in the slot when the disconnect arrives, so a
// buffer that kept the oldest, or a reader that only published on a state
// change, would hand the window a pressed button and then never speak again.
#[test]
fn unplugging_while_a_button_is_held_releases_it() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.0, 0.0, [3u16]);
    fake.report(0.4, 0.0, [3u16]);
    fake.report(0.8, 0.0, [3u16]);

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll(); // open
    live.poll();
    live.poll();
    live.poll(); // three reports read, nothing drained

    let stream = live.stream();
    assert!(
        stream
            .peek()
            .and_then(|s| s.motion().map(|m| m.buttons().to_vec()))
            == Some(vec![3]),
        "the test needs a held button in the slot before the unplug"
    );

    fake.unplug();
    live.poll();

    let snapshot = stream.take().expect("a snapshot after the unplug");
    assert_eq!(
        snapshot.status,
        LiveStatus::Unavailable {
            code: ErrorCode::DeviceNotFound
        }
    );
    assert!(
        snapshot.motion().is_none(),
        "the unplug left a button held down"
    );
}

// The same release, through the other door: the stream stays open and simply
// stops saying anything.
#[test]
fn a_stream_that_goes_quiet_releases_a_held_button() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.0, 0.0, [7u16]);

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    live.poll();
    assert!(live.status().motion().is_some());

    // A second of silence is ordinary: the device sends when something moves.
    clock.advance(Duration::from_secs(1));
    live.poll();
    assert!(
        live.status().motion().is_some(),
        "one quiet second must not blank a stick at rest"
    );

    clock.advance(Duration::from_secs(1));
    live.poll();

    assert_eq!(live.status().as_str(), "stale");
    assert!(live.status().motion().is_none());
}

#[test]
fn stopping_while_a_button_is_held_releases_it() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.0, 0.0, [2u16]);

    let mut live = reading(&fake, &clock);
    assert!(live.status().motion().is_some());

    live.stop();

    assert_eq!(live.status(), LiveStatus::Stopped);
    let last = live.stream().take().expect("a snapshot");
    assert!(last.motion().is_none(), "stop left a button held down");
    assert!(!live.is_running());
}

#[test]
fn a_stopped_reader_does_nothing_until_it_is_started_again() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");

    let mut live = manager(&fake, &clock);
    live.poll();
    assert_eq!(fake.opens(), 0, "poll before start opened a device");

    live.start();
    live.poll();
    assert_eq!(fake.opens(), 1);

    live.stop();
    live.poll();
    assert_eq!(fake.opens(), 1, "poll after stop reopened the device");

    live.start();
    live.poll();
    assert_eq!(fake.opens(), 2, "restart did not reopen the device");
}

#[test]
fn a_fast_device_and_a_slow_window_do_not_grow_the_buffer() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    let stream = live.stream();

    // Twenty thousand distinct reports, nobody draining. Distinct so the jitter
    // filter cannot be what keeps the slot small.
    for step in 0..20_000u32 {
        let x = f64::from(step % 200) / 100.0 - 1.0;
        fake.report(x, 0.0, [u16::try_from(step % 13 + 1).expect("small")]);
        live.poll();
        assert!(stream.depth() <= 1, "the buffer grew past one snapshot");
    }

    let stats = stream.stats();
    assert_eq!(stats.delivered, 0);
    assert!(stats.coalesced > 0, "back pressure was never exercised");
    assert_eq!(stats.published, stats.coalesced + 1);

    // What the window finally gets is the newest state, not the oldest.
    let last = stream.take().expect("a snapshot");
    assert_eq!(last.seq, stats.published);
}

#[test]
fn a_still_stick_is_not_redrawn_for_every_report() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    let stream = live.stream();
    let before = stream.stats().published;

    fake.report(0.0, 0.0, []);
    live.poll();
    let after_first = stream.stats().published;
    assert_eq!(after_first, before + 1, "the first report must draw");

    // A count or two of jitter, well under one percent of travel.
    for _ in 0..50 {
        fake.report(0.004, -0.003, []);
        live.poll();
    }
    assert_eq!(
        stream.stats().published,
        after_first,
        "jitter under one percent redrew the page"
    );

    // A real move does draw.
    fake.report(0.3, 0.0, []);
    live.poll();
    assert_eq!(stream.stats().published, after_first + 1);
}

#[test]
fn a_button_pressed_without_the_stick_moving_still_draws() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    let stream = live.stream();

    fake.report(0.0, 0.0, []);
    live.poll();
    let quiet = stream.stats().published;

    fake.report(0.0, 0.0, [5u16]);
    live.poll();

    assert_eq!(stream.stats().published, quiet + 1);
    assert_eq!(
        live.status().motion().map(|m| m.buttons().to_vec()),
        Some(vec![5])
    );
}

#[test]
fn a_device_that_will_not_open_is_retried_rather_than_spun_on() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.fail_to_open(DeviceError::NotFound);

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    assert_eq!(
        live.status(),
        LiveStatus::Unavailable {
            code: ErrorCode::DeviceNotFound
        }
    );

    // Inside the backoff nothing is attempted again. Failed opens are counted
    // too: a reader hammering a device that will not open still shows one
    // successful open and would pass a test that only counted those.
    let attempts = (fake.enumerations(), fake.open_attempts());
    for _ in 0..100 {
        live.poll();
    }
    clock.advance(Duration::from_millis(1999));
    live.poll();
    assert_eq!(
        (fake.enumerations(), fake.open_attempts()),
        attempts,
        "the backoff was ignored"
    );

    clock.advance(Duration::from_millis(2));
    fake.plug_in("QuadStick");
    live.poll();
    assert_eq!(fake.opens(), 1, "the device was never picked up again");
}

#[test]
fn enumeration_failing_is_a_state_the_window_can_render() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.fail_to_enumerate(DeviceError::NotQuadStick);

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();

    assert_eq!(
        live.status(),
        LiveStatus::Unavailable {
            code: ErrorCode::DeviceNotQuadStick
        }
    );
    assert!(live.status().motion().is_none());
}

#[test]
fn a_device_that_comes_back_is_read_again() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.0, 0.0, [1u16]);

    let mut live = reading(&fake, &clock);
    assert_eq!(
        live.status().motion().map(|m| m.buttons().to_vec()),
        Some(vec![1])
    );

    fake.unplug();
    live.poll();
    assert!(live.status().motion().is_none());

    clock.advance(Duration::from_millis(1001));
    fake.plug_in("QuadStick");
    fake.report(0.0, 0.0, [1u16]);
    live.poll(); // reopen
    live.poll(); // the report

    assert_eq!(fake.opens(), 2);
    // The same reading as before the unplug: the jitter filter must not have
    // remembered it across the gap and swallowed the first frame back.
    assert_eq!(
        live.status().motion().map(|m| m.buttons().to_vec()),
        Some(vec![1])
    );
}

// The soak. Two hours of a 250 Hz stream is 1.8 million reports; this runs the
// shape of it at a size a test suite can afford, on a clock that does not sleep.
// What it proves is that nothing accumulates: not the buffer, not the manager,
// not the fake.
#[test]
fn a_long_stream_does_not_accumulate() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");

    let mut live = manager(&fake, &clock);
    live.start();
    live.poll();
    let stream = live.stream();

    const REPORTS: u32 = 200_000;
    // Four milliseconds a report is 250 Hz.
    const TICK: Duration = Duration::from_millis(4);

    for step in 0..REPORTS {
        let x = f64::from(step % 200) / 100.0 - 1.0;
        let y = f64::from(step % 150) / 75.0 - 1.0;
        fake.report(x, y, [u16::try_from(step % 16 + 1).expect("small")]);
        clock.advance(TICK);
        live.poll();

        assert!(stream.depth() <= 1, "the buffer grew at report {step}");
        assert_eq!(fake.queued(), 0, "a report went unread at {step}");

        // A window that paints now and then, the way a real one does.
        if step % 60 == 0 {
            stream.take();
        }
    }

    let stats = stream.stats();
    let pending = u64::try_from(stream.depth()).expect("depth is 0 or 1");
    assert_eq!(stats.published, stats.delivered + stats.coalesced + pending);
    assert_eq!(fake.opens(), 1, "the stream was reopened during the soak");
    assert!(
        live.status().motion().is_some(),
        "the stream stopped delivering"
    );
}

// The jitter filter must not remember across a quiet spell. If it did, a stick
// held in one place while the stream went stale would never come back on
// screen: the first report after the silence would be identical to the last one
// before it, and identical is what the filter throws away.
#[test]
fn the_first_report_after_a_quiet_spell_draws_even_if_nothing_moved() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    fake.plug_in("QuadStick");
    fake.report(0.6, 0.0, [9u16]);

    let mut live = reading(&fake, &clock);
    assert!(live.status().motion().is_some());

    clock.advance(Duration::from_secs(3));
    live.poll();
    assert_eq!(live.status().as_str(), "stale");

    fake.report(0.6, 0.0, [9u16]);
    live.poll();

    assert_eq!(live.status().as_str(), "reading");
    assert_eq!(
        live.status().motion().map(|m| m.buttons().to_vec()),
        Some(vec![9])
    );
}

// Enumerating means walking every USB device on the machine. Doing it on every
// poll of a loop that is meant to run for hours is a reader that eats a core
// while nothing is plugged in.
#[test]
fn an_empty_machine_is_rescanned_on_a_timer_and_not_in_a_spin() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    let mut live = manager(&fake, &clock);

    live.start();
    live.poll();
    assert_eq!(fake.enumerations(), 1);

    for _ in 0..500 {
        live.poll();
    }
    clock.advance(Duration::from_millis(1499));
    live.poll();
    assert_eq!(fake.enumerations(), 1, "the rescan timer was ignored");

    clock.advance(Duration::from_millis(2));
    live.poll();
    assert_eq!(fake.enumerations(), 2, "the rescan never came round");
}

// The core owns no thread, so it cannot sleep. It has to be able to tell the
// worker how long to sleep instead, or the worker's only options are spinning
// and guessing.
#[test]
fn the_worker_is_told_how_long_to_wait() {
    let fake = FakeLiveInput::new();
    let clock = ManualClock::new();
    let mut live = manager(&fake, &clock);

    live.start();
    assert_eq!(
        live.next_attempt_in(),
        None,
        "there is nothing to wait for yet"
    );

    live.poll(); // nothing plugged in, so back off for the rescan window
    assert_eq!(live.next_attempt_in(), Some(Duration::from_millis(1500)));

    clock.advance(Duration::from_millis(1400));
    assert_eq!(live.next_attempt_in(), Some(Duration::from_millis(100)));

    clock.advance(Duration::from_millis(100));
    assert_eq!(live.next_attempt_in(), None, "the wait is over");

    fake.plug_in("QuadStick");
    live.poll();
    assert_eq!(
        live.next_attempt_in(),
        None,
        "an open stream has nothing to wait for"
    );
}
