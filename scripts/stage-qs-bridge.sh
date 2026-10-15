#!/bin/sh
# Builds qs-bridge and puts it where Tauri's externalBin looks for it
# (src-tauri/binaries/qs-bridge-<host triple>). The bundle then installs it
# beside the app as plain `qs-bridge`, which is where controller::find_binary
# looks, the same place the Avalonia tarball ships it (ca813aa).
# Linux only: run by tauri.linux.conf.json's beforeBuildCommand.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
# Tauri names the sidecar by the target it builds for; the host when unset.
host=$(rustc -vV | sed -n 's/^host: //p')
triple=${TAURI_ENV_TARGET_TRIPLE:-$host}
target_dir=${CARGO_TARGET_DIR:-$root/target}
if [ "$triple" = "$host" ]; then
  out="$target_dir/release/qs-bridge"
  cargo build --release --locked -p qs-bridge --config profile.release.strip=true \
    --manifest-path "$root/Cargo.toml"
else
  out="$target_dir/$triple/release/qs-bridge"
  cargo build --release --locked -p qs-bridge --config profile.release.strip=true \
    --manifest-path "$root/Cargo.toml" --target "$triple"
fi
mkdir -p "$root/src-tauri/binaries"
install -m755 "$out" "$root/src-tauri/binaries/qs-bridge-$triple"
