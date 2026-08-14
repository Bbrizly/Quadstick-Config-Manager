# SipSight: iOS companion app for QuadStick, Shipaton 2026

Superseded 2026-08-14 by `20260814-shipaton-ios-configurator.md`: the
Shipaton entry is now a configurator. The firmware verification and
bridge design below stay valid for the post-Shipaton telemetry layer.

Date: 2026-08-13. Deadline: app live on the App Store and Devpost submission
in by Oct 1, 2026, 3:45am ADT (Sep 30, 11:45pm PDT). Registered on Devpost.
Judges: Charlie Chapman and David Barnard of RevenueCat. Working name
SipSight, pending a trademark check with Fred before using "QuadStick"
anywhere in the store listing.

## What it is

A phone becomes a live window into a QuadStick. A clinician or caregiver holds
the phone next to the user and watches sip and puff pressure, joystick
position, lip switch state, and the active profile in real time while the user
plays. Today they guess from behavior. This shows them the numbers, so
threshold tuning goes from trial and error to sight.

One sentence for judges: see what a paralyzed gamer's breath is doing, live.

## Why this can win

Target categories, in order:

1. **RevenueCat Peace Prize** ($15k, social good). An accessibility tool for
   quadriplegic gamers is the strongest fit in the field. Few entries will
   touch this category with real hardware and a real community.
2. **RevenueCat Design Award** ($15k). Native SwiftUI, one screen that looks
   alive (pressure waves, LED replica). Small surface, high polish.
3. **#BuildInPublic** ($30k). Weekly posts write themselves: real firmware,
   real users, the manufacturer mailing a device mid-hackathon.
4. **HAMM** ($15k). Judged as "smartest use of RevenueCat to drive real
   revenue", so sandbox purchases are not enough. Get Pro live on day one of
   the store release and push the QuadStick community (forum, Facebook group,
   Fred's mailing list) so real purchases exist before judging.

Not targeting the Grand Prize. It rewards traction and the QuadStick community
is thousands, not millions. Peace Prize judges on impact per user, which is
where this app is unbeatable.

Rules check: must be a brand-new app first released Aug 1 to Sep 30 (QCM
desktop being live does not conflict, this is a new app), must use the
RevenueCat SDK for at least one purchase, submission needs a 2-minute video,
live store URL, 1024 icon, 1179x2556 screenshots, and a promo code for judges.

## Architecture

Phone cannot talk to the QuadStick directly. Half the units have no Bluetooth
radio, the USB port is HID not CDC, and iOS blocks Bluetooth Classic SPP
without MFi. Decided 2026-08-12: computer as bridge.

    QuadStick --serial (DI 1&2 jack)--> QCM desktop --WebSocket over Wi-Fi--> iPhone

Three layers, each shippable without the one below it:

1. **App with demo mode** (no hardware, no QCM needed). Two demo sources:
   bundled recorded sessions, and live mic input where blowing into the phone
   drives the pressure gauge. This is what App Review and every judge will
   actually experience. It must be indistinguishable in quality from the real
   feed.
2. **QCM bridge** (this repo). A "Phone Link" window in QCM: starts a local
   WebSocket server, shows a QR code with `ws://ip:port` plus a random token.
   Phone scans, connects, done. No mDNS dependency, QR pairing works on
   locked-down clinic Wi-Fi. QCM parses the existing firmware debug output
   from the serial port into telemetry frames.
3. **Firmware telemetry** (Fred's side, optional). The proposed `telemetry,20`
   lightweight feed. If it ships, QCM uses it. If not, the debug-output parse
   at a lower rate still works. Not on the critical path.

## Firmware verification (FW 2373 tree, read 2026-08-13)

Every claim below was read from the C, not asserted. The 1476 tree
(quadstick-master) is no longer in ~/Downloads, so old-firmware parity is
unverified; re-check when it is restored.

What works, with evidence:

- Console is UART0 on P0.2/P0.3 (the DI 1&2 jack), 115200 8N1
  (bsp_MCB2300.h:41-43, Joystick.c "Serial_Init(115200, false)").
- RX is drained in the timer1 ISR, so commands are received even while the
  main loop is busy (sound.c:183-210, Console.c:621).
- Sending any byte on the serial port claims console output back from
  Bluetooth (sound.c:206: ConsolePort follows the last port that received).
- `debug\r` toggles a telemetry stream at runtime. No preference write, no
  file touch, no config reload (Console.c:322-325).
- The stream includes raw pre-threshold analog values: each sip/puff sensor
  prints `value,soft_value,timer,state,prior,sign,RAW` (DataFlow.c:379),
  lip prints raw and state (DataFlow.c:1166), joystick prints deflection,
  offsets and per-axis values (DataFlow.c:1073). Exactly what the visualizer
  needs. Output is ANSI-terminal formatted; the parser strips escapes.
- Handshake: `build\r` prints the firmware version (Console.c:341-343).
  `pref\r` dumps every preference with its value (Console.c:335-340), which
  gives the app live thresholds to draw without parsing config files.

Measured constraints:

- Rate: debug prints every 100th main-loop pass (Console.c:636-640); the
  loop runs ~1ms/scan (DataFlow.c:2471, counter at 8kHz per sound.c:184).
  A frame is a few hundred bytes and console writes are BLOCKING
  (bsp.c:133), ~30-40ms per frame at 115200. Effective stream is ~5-10Hz
  with ~35ms input-latency spikes. Fine for a tuning session, not for
  competitive play. `telemetry,20` remains the smooth-feed upgrade.
- Update the app copy accordingly: this is a tuning view, and the live view
  states the refresh rate honestly.

Failure modes the bridge must detect and name (never say nothing):

- `debug,0` in a profile's preferences silences all console output
  (Configuration.c:375 gates bsp.c:133) while commands still execute. It
  cannot be re-enabled over the console: "debug" hits the command table
  before the preference table (Console.c:215 runs before :568), so the
  console `debug` word toggles the stream flag, never the preference. QCM
  detects a silent console after `build\r` and tells the user which
  preference to change in their profile.
- `digital_out_3` / `digital_out_4` preferences repurpose P0.2/P0.3 as
  GPIO outputs, killing the serial port (Configuration.c:383-385,
  bsp_MCB2300.c:703-712 with DI_INPUT_PORT 0). Same detection path.
- P0.3 doubles as the config-bypass strap read at boot with a pullup
  (Joystick.c:551-563). UART idle is high so a powered adapter is safe,
  but an unpowered or DTR-low adapter at boot can force default config.
  App and docs say: plug the cable in after the QuadStick boots. Field
  test on the unit Fred sends.

Safety rule for the bridge, absolute: commands sent to the device come from
a fixed whitelist (`build`, `debug`, `pref`, `print`, `status`). Nothing
else, ever. Any output name sent as CSV actuates real controls on the
user's device (Console.c:542-565 turns e.g. `left_stick_up,1,100` into a
live input), and preference names silently rewrite settings (Console.c:568-
580). No free text is ever forwarded to the serial port.

Protocol: JSON frames over the socket, versioned.

    {"v":1,"t":123.45,"sip":-12,"puff":0,"x":510,"y":498,"lip":0,"profile":3,"mode":1}

Raw pre-threshold values, because the whole point is seeing what happens
below the threshold. Never rewritten or clamped, same rule as QCM.

## The app

SwiftUI, iOS 17+, iPhone first (iPad works via scaling). No backend, no
accounts, no analytics. Data never leaves the phone except the local socket.

Screens:

1. **Connect.** Scan QR from QCM, or "Try demo". Big targets, VoiceOver
   labeled, no color-only state.
2. **Live.** The one screen that matters. Sip/puff as a vertical pressure wave
   with the profile's threshold lines drawn on it and labeled, joystick as a
   position dot with deflection ring, lip switch state, and an LED replica
   showing active profile position and mode flash, same visualization language
   the QCM redesign is adopting. Landscape support so it props next to a
   monitor.
3. **Record / History** (Pro). Start and stop a session, list past sessions,
   scrub playback, trend line across sessions ("her puff strength is up 20%
   since June").
4. **Report** (Pro). One-tap PDF of a session for the clinic file, share
   sheet.
5. **Settings.** Paywall, tip jar, restore purchases, about, privacy.

Accessibility is correctness here too: full VoiceOver pass, Dynamic Type,
thresholds labeled with numbers not just color, reduced motion respected. An
accessibility app that fails an accessibility audit loses the Peace Prize.

## Monetization (the HAMM story)

Rule: nothing a disabled user needs to operate their own hardware is ever
paywalled. Live view is free forever.

- **Free:** connect, live view, demo mode.
- **Pro:** recording, history, trends, PDF reports. $4.99/mo, $29.99/yr,
  $49.99 lifetime. Buyers are clinics and family, not the user.
- **Tip jar:** one-time $2/$5/$10 for people who want to fund the free tier.

RevenueCat: purchases-ios via SPM, products and entitlement ("pro") in the
dashboard, RevenueCat Paywalls for the paywall UI instead of custom paywall
code. Promo codes generated for judges per the submission rules.

## QCM-side work (this repo)

- Serial port reader for the telemetry feed (System.IO.Ports, already the
  plan for the desktop visualizer).
- WebSocket server: HttpListener + AcceptWebSocketAsync from the BCL, no new
  dependency. Token from the QR checked on connect, localhost-plus-LAN only.
- Phone Link window: start/stop, QR code, connected-client status.
- Replay file support so the bridge can be developed before the hardware
  arrives.

## Store and submission plan

- App Store metadata: name SipSight, subtitle "Live breath telemetry for
  sip-and-puff players", privacy label "no data collected", mic usage string
  for demo mode only.
- Reviewer notes: hardware companion app, full demo mode included, no login.
  Expect one rejection round anyway (4.2 minimum functionality is the risk,
  demo mode is the answer). Budget two review cycles.
- Devpost: 2-min video showing a real or demo session, blow-into-the-phone
  moment in the first 20 seconds, description, promo code.
- #BuildInPublic: one post per week from day one. Repo public from day one.
  Post links in the Shipaton Discord #post-engagement-boost channel; 16k
  participants boosting each other is free reach. Prize is $30k/$20k/$10k
  across three places, deepest non-Grand pool in the event.
- Ship Kit: claim the sponsor freebies now, some are while-supplies-last.
- If you still count as an active student (.edu or equivalent email), also
  enter Next Gen: video plus open-source code, no store release needed, a
  second $15k category for work already being done.

## Schedule (7 weeks, external deadline, held)

- **W1, Aug 13-17:** new repo `sipsight` with remote day one. SwiftUI
  skeleton, demo mode with simulated feed, Live screen v1. RevenueCat account
  and products created.
- **W2, Aug 18-24:** QCM bridge server, QR pairing, replay-file feed end to
  end phone-to-desktop. Ask Fred to ship the offered test unit this week.
- **W3, Aug 25-31:** recording, history, mic demo mode, RevenueCat paywall
  and tip jar working in sandbox.
- **W4, Sep 1-7:** real hardware bring-up if the unit arrived, PDF report,
  VoiceOver and Dynamic Type pass, icon, screenshots, TestFlight.
- **W5, Sep 8-14:** submit to App Review no later than **Sep 10**. Fix round.
- **W6, Sep 15-21:** live on store target **Sep 19**. Record demo video,
  draft Devpost.
- **W7, Sep 22-30:** Devpost submitted **Sep 25**, five days of buffer.
  Anything not done by Sep 22 gets cut, not extended.

## Risks

- **No hardware in hand.** Fred offered a unit; ask this week. Everything is
  built demo-first so the hardware is a bonus, not a blocker.
- **Firmware telemetry never ships.** Fine, debug-output parse plus demo mode
  cover it.
- **App Review rejects a hardware companion.** Demo mode plus reviewer notes.
  Submitting Sep 10 leaves ~20 days for two cycles.
- **Trademark.** "QuadStick" belongs to Fred. Use SipSight in the listing,
  mention compatibility in the description, confirm with him in the same
  email as the hardware ask.
- **Scope creep.** The Live screen is the product. History, reports, and
  everything Pro exists to serve the paywall requirement and can shrink to
  recording-plus-list if time runs out.
