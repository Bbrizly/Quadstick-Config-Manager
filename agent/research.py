#!/usr/bin/env python3
"""Find out how a game is controlled, in the device's own words.

Name any game. This reads the web for its control scheme and writes it as a
chart the rest of the pipeline already understands, so nothing downstream has
to know a chart was researched rather than hand written.

Two things make it safe to point at a game nobody has checked:

  - Every key it returns is checked against the device's real vocabulary. A
    name the device does not know is dropped and listed, never written. The
    model is handed the allowed names up front so it maps `Space` to `kb_space`
    rather than guessing.
  - Where it got each fact is written into the chart, and a control it could
    not source lands in `disputed` instead of quietly becoming a fact.

    python3 agent/research.py "Elden Ring"
    python3 agent/research.py "Elden Ring" --platform ps3 --replay
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
QSF = os.path.join(os.path.dirname(HERE), "tools/qsf/bin/Debug/net8.0/qsf")
CLAUDE_BIN = os.environ.get("QSF_CLAUDE_BIN") or "claude"
MODEL = os.environ.get("QSF_MODEL", "claude-sonnet-5")

ASK = """Find the default controls for {game} on {platform_words}.

Read the actual control pages. Do not answer from memory: this ends up on a \
device somebody plays with their mouth, and a control that is wrong costs them \
a playthrough.

Name every control using EXACTLY one of these device names, which are case \
sensitive. If a control has no name in this list, leave it out.

{tokens}

Reply with one JSON object and nothing else, no prose, no markdown fence:
{{"game": "...",
  "platform": "{platform}",
  "controls": {{"<device name>": {{"action": "what it does in game",
                                  "critical": true if it is used constantly}}}},
  "disputed": {{"<device name>": "why you are not sure"}},
  "source": {{"url": "the page you actually read", "note": "..."}}}}

Put anything sources disagree on, or that you could not find, in `disputed` \
rather than guessing it into `controls`."""


def vocab(platform):
    out = subprocess.run([QSF, "vocab"], capture_output=True, text=True, env={
        **os.environ, "DOTNET_ROOT": os.environ.get("DOTNET_ROOT",
                                                    os.path.expanduser("~/.dotnet"))})
    if out.returncode != 0:
        raise SystemExit(f"could not read the device vocabulary: {out.stderr[:200]}")
    return set(json.loads(out.stdout)["outputs"][platform])


def research(game, platform, names, mode):
    """One web-reading call, cached, so a rehearsed run needs no network."""
    # Buttons and keys only. The rig rows (mouse speed, deflection, mode
    # switching) are the player's own setup and are not a fact about the game.
    offer = sorted(n for n in names if not n.startswith(
        ("mouse_", "deflection_", "joystick_", "bluetooth_", "digital_out",
         "acceleration_", "increment_", "decrement_")))
    words = {"xbox": "PC, keyboard and mouse, or Xbox controller",
             "ps3": "PC or PlayStation controller"}[platform]
    prompt = ASK.format(game=game, platform=platform, platform_words=words,
                        tokens=", ".join(offer))
    key = hashlib.sha256(f"{MODEL}|{prompt}".encode()).hexdigest()[:32]
    path = os.path.join(CACHE, "research-" + key + ".json")

    if os.path.exists(path):
        return json.load(open(path)), "cache"
    if mode == "replay":
        raise SystemExit(f"replay mode and no research recorded for {game} ({key}).")

    done = subprocess.run([
        CLAUDE_BIN, "-p", prompt, "--model", MODEL, "--output-format", "json",
        "--system-prompt", "You research how games are controlled. You read pages "
                           "before answering. You reply with one JSON object and "
                           "nothing else.",
        "--tools", "WebSearch,WebFetch",
        "--disable-slash-commands", "--strict-mcp-config",
        "--mcp-config", '{"mcpServers":{}}', "--safe-mode", "--no-session-persistence",
    ], capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
    if done.returncode != 0:
        raise SystemExit(f"the research call failed: {(done.stderr or done.stdout)[:300]}")

    text = (json.loads(done.stdout).get("result") or "").strip()
    if text.startswith("```"):
        text = text.split("```")[1].removeprefix("json").strip()
    try:
        found = json.loads(text)
    except json.JSONDecodeError:
        raise SystemExit(f"the researcher did not return JSON:\n{text[:400]}")
    os.makedirs(CACHE, exist_ok=True)
    with open(path, "w") as f:
        json.dump(found, f, indent=2)
    return found, "live"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("game")
    ap.add_argument("--platform", default="xbox", choices=["xbox", "ps3"])
    ap.add_argument("--out", help="defaults to agent/charts/<slug>.json")
    ap.add_argument("--replay", action="store_true", help="from the recording, no network")
    args = ap.parse_args()

    slug = re.sub(r"[^a-z0-9]+", "-", args.game.lower()).strip("-")
    out = args.out or os.path.join(HERE, "charts", slug + ".json")
    names = vocab(args.platform)

    found, origin = research(args.game, args.platform, names, "replay" if args.replay else "auto")

    # Nothing the researcher says becomes a control until the device agrees the
    # name exists. This is the same rule qsf applies at the write end; applying
    # it here means a bad name is reported now rather than at the last step.
    kept, refused = {}, {}
    for name, detail in (found.get("controls") or {}).items():
        if name in names:
            kept[name] = {"action": str(detail.get("action", detail))[:120],
                          "critical": bool(detail.get("critical", False))
                          if isinstance(detail, dict) else False}
        else:
            refused[name] = str(detail.get("action", detail) if isinstance(detail, dict)
                                else detail)[:80]

    chart = {
        "game": found.get("game", args.game),
        "family": slug,
        "platform": args.platform,
        "source": {**(found.get("source") or {}),
                   "method": "read from the web by agent/research.py",
                   "verified_by_hand": False},
        "confidence": "researched, not checked by a person",
        "controls": kept,
        "disputed": found.get("disputed") or {},
        "not_covered": sorted(refused),
    }
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w") as f:
        json.dump(chart, f, indent=2)

    print(f"{args.game} [{origin}]: {len(kept)} controls the device knows, "
          f"{len(chart['disputed'])} it would not commit to, {len(refused)} dropped")
    for name, action in list(refused.items())[:6]:
        print(f"   dropped '{name}' ({action[:50]}): not a name the device knows")
    print(f"source: {chart['source'].get('url', 'none given')}")
    print(f"written to {out}")
    return 0 if kept else 1


if __name__ == "__main__":
    sys.exit(main())
