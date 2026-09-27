# qs-bridge

Makes a QuadStick in emulation mode 2 (x360ce) show up as an Xbox 360 pad,
so XInput-only games see it while its keyboard, mouse and drive keep working.
It does on Linux what QMP does with ViGEmBus on Windows. Linux only for now.

It copies the stick, it does not remap. The profile on the stick decides
what each sip and puff does.

## From the Config Manager

The Linux download carries `qs-bridge` and `70-quadstick.rules` beside the
app. Device page, "Xbox controller bridge": the switch runs it while the app
is open and says what it is doing. Install the rule once (below, first three
lines), then replug the stick.

A host runs `qs-bridge --json`, reads one `{"status":"..."}` line per change
(`waiting`, `bridging`, `no_access`, `no_virtual_pad`, `unsupported`) and
stops it by closing its stdin, which releases every button first. The Tauri
app can use `qs_bridge::Status` directly.

## Install on Linux, without the app

    install -Dm755 qs-bridge ~/.local/bin/qs-bridge
    sudo install -m644 70-quadstick.rules /etc/udev/rules.d/
    sudo udevadm control --reload && sudo udevadm trigger
    install -Dm644 qs-bridge.service ~/.config/systemd/user/qs-bridge.service
    systemctl --user enable --now qs-bridge

Replug the stick after installing the rule. `journalctl --user -u qs-bridge`
says whether it found the stick.

## Unplugging

Unplug the stick, or stop the service, and every button and trigger on the
virtual pad is released first.

## Check what the stick sends

    qs-bridge --print

prints each report and what it decodes to. It works on macOS and Windows too,
and makes no pad.

## Known gap

The bridge hides the stick's gamepad from games that read evdev. Steam's own
controller driver reads hidraw and can still show a second pad. If it does,
turn that pad off in Steam's controller settings.
