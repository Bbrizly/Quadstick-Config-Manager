#!/usr/bin/env python3
"""Dump the QuadStick firmware's keyword tables into the test corpus.

The tests hold a transcription of the device's CSV reader (FirmwareOracle) and
check that the app never disagrees with it in silence. That transcription is
only worth something if its keyword tables really are the firmware's, so they
are generated from the firmware source rather than typed by hand.

    python3 scripts/dump-firmware-keywords.py ~/Downloads/quadstick-master

Run this whenever a newer firmware source arrives, then run the tests. What
fails is the list of things the app now believes that the device does not.
"""
import json
import re
import sys
from pathlib import Path

CORPUS = Path(__file__).resolve().parent.parent / "tests/QuadStick.Format.Tests/corpus"


def table(path, name):
    text = path.read_text(errors="replace")
    body = re.search(
        r"keyword_t\s+" + name + r"\s*\[[^\]]*\]\s*=\s*\{(.*?)\n\};", text, re.S)
    if not body:
        sys.exit(f"could not find {name} in {path}")
    return re.findall(r'\{\s*"([^"]+)"', body.group(1))


def define(path, name):
    m = re.search(r"#define\s+" + name + r"\s+(\d+)", path.read_text(errors="replace"))
    if not m:
        sys.exit(f"could not find #define {name} in {path}")
    return int(m.group(1))


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    root = Path(sys.argv[1]).expanduser() / "Joystick"
    if not root.is_dir():
        sys.exit(f"no Joystick directory under {sys.argv[1]}")

    version = define(root / "build.h", "FW_VERSION")
    data = {
        "_source": f"QuadStick firmware Joystick/*.h, FW_VERSION {version} (build.h). "
                   "Regenerate with scripts/dump-firmware-keywords.py against the firmware source.",
        "fw_version": version,
        "max_keyword_length": define(root / "InputOutputChannels.h", "MAX_KEYWORD_LENGTH"),
        "max_bindings": define(root / "Configuration.h", "MAX_NUMBER_OF_BINDINGS"),
        "max_profiles": define(root / "Configuration.h", "MAX_NUMBER_OF_PROFILES"),
        "line_buffer": 1024,  # char line_buffer[1024] in Configuration.c
        "outputs": table(root / "output_keywords.h", "output_keywords"),
        "inputs": table(root / "input_keywords.h", "input_keywords"),
        "preferences": table(root / "preference_keywords.h", "preference_keywords"),
        "functions": table(root / "preference_keywords.h", "function_keywords"),
        # The third line of a mode sheet. Left out of the first dump, so the
        # oracle kept a hand written list and stayed on the 2017 one after the
        # rest of it moved to 2373.
        "connections": table(root / "preference_keywords.h", "connections_keywords"),
    }

    # Named after the version it came from, so a newer firmware can never
    # overwrite an older dump and quietly change what the tests call the truth.
    out = CORPUS / f"firmware-{version}.json"
    out.write_text(json.dumps(data, indent=1) + "\n")
    print(f"wrote {out.name}: firmware {version}, "
          + ", ".join(f"{len(data[k])} {k}" for k in
                      ("outputs", "inputs", "preferences", "functions", "connections")))
    print("Point FirmwareOracle at it if this is the firmware to model, and keep "
          "the older dump: DeviceAgreementTests proves the new one removed nothing.")


if __name__ == "__main__":
    main()
