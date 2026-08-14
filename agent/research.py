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

# Both are part of the cache key, so changing either one asks again instead of
# replaying an answer that was given under different rules.
SYSTEM = ("You research how games are controlled. You read pages before "
          "answering. You reply with one JSON object and nothing else.")
TOOLS = "WebSearch,WebFetch"

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
    # Everything that could change the answer is in the key, not just the
    # question: the instructions it was given and the tools it was allowed to
    # use decide what an answer is worth, and replaying one recorded under
    # different instructions would be a stale hit wearing a fresh answer's face.
    key = hashlib.sha256(
        json.dumps([MODEL, SYSTEM, TOOLS, prompt]).encode()).hexdigest()[:32]
    path = os.path.join(CACHE, "research-" + key + ".json")

    if os.path.exists(path) and mode != "live":
        try:
            recorded = json.load(open(path))
        except json.JSONDecodeError:
            raise SystemExit(f"the recorded research at {path} is not readable JSON. "
                             f"Delete it and run again to research this game fresh.")
        if not isinstance(recorded, dict):
            raise SystemExit(f"the recorded research at {path} is not a control chart. "
                             f"Delete it and run again.")
        return recorded, "cache"
    if mode == "replay":
        raise SystemExit(f"replay mode and no research recorded for {game} ({key}).")

    done = subprocess.run([
        CLAUDE_BIN, "-p", prompt, "--model", MODEL, "--output-format", "json",
        "--system-prompt", SYSTEM,
        "--tools", TOOLS,
        "--disable-slash-commands", "--strict-mcp-config",
        "--mcp-config", '{"mcpServers":{}}', "--safe-mode", "--no-session-persistence",
    ], capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL)
    if done.returncode != 0:
        raise SystemExit(f"the research call failed: {(done.stderr or done.stdout)[:300]}")

    try:
        text = (json.loads(done.stdout).get("result") or "").strip()
    except (json.JSONDecodeError, AttributeError):
        raise SystemExit(f"the research call did not return JSON:\n{done.stdout[:400]}")
    if text.startswith("```"):
        text = text.split("```")[1].removeprefix("json").strip()
    try:
        found = json.loads(text)
    except json.JSONDecodeError:
        raise SystemExit(f"the researcher did not return JSON:\n{text[:400]}")
    if not isinstance(found, dict):
        raise SystemExit(f"the researcher returned a {type(found).__name__}, not a "
                         f"control chart:\n{text[:400]}")
    os.makedirs(CACHE, exist_ok=True)
    with open(path, "w") as f:
        json.dump(found, f, indent=2)
    return found, "live"


def slugify(game):
    """One filename per game, so "Elden Ring" and "elden-ring" find one chart."""
    return re.sub(r"[^a-z0-9]+", "-", game.lower()).strip("-")


def build_chart(game, platform="xbox", out=None, mode="auto", log=print):
    """Research one game and write its chart. Returns the chart and what it cost.

    Every count the caller needs is in the return value, because the app shows
    them and nothing here should have to be read back out of printed prose.
    """
    slug = slugify(game)
    out = out or os.path.join(HERE, "charts", slug + ".json")
    names = vocab(platform)

    found, origin = research(game, platform, names, mode)

    # Nothing the researcher says becomes a control until the device agrees the
    # name exists. This is the same rule qsf applies at the write end; applying
    # it here means a bad name is reported now rather than at the last step.
    # Each of these three is checked for its shape before it is read. A chart
    # whose `controls` came back as a list, or whose `source` came back as a
    # bare URL string, used to end the run in a traceback naming nothing.
    controls = found.get("controls") or {}
    if not isinstance(controls, dict):
        raise SystemExit(f"the researcher returned `controls` as a "
                         f"{type(controls).__name__}, so no control could be read "
                         f"from it. Nothing was written.")
    source = found.get("source") or {}
    if isinstance(source, str):
        source = {"url": source}
    elif not isinstance(source, dict):
        source = {"note": f"the researcher gave a {type(source).__name__} as its source"}
    disputed = found.get("disputed") or {}
    if not isinstance(disputed, dict):
        disputed = {"unnamed": str(disputed)[:200]}

    kept, refused = {}, {}
    for name, detail in controls.items():
        if name in names:
            kept[name] = {"action": str(detail.get("action", detail))[:120],
                          "critical": bool(detail.get("critical", False))
                          if isinstance(detail, dict) else False}
        else:
            refused[name] = str(detail.get("action", detail) if isinstance(detail, dict)
                                else detail)[:80]

    chart = {
        "game": found.get("game", game),
        "family": slug,
        "platform": platform,
        "source": {**source,
                   "method": "read from the web by agent/research.py",
                   "verified_by_hand": False},
        "confidence": "researched, not checked by a person",
        "controls": kept,
        "disputed": disputed,
        "not_covered": sorted(refused),
    }
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w") as f:
        json.dump(chart, f, indent=2)

    log(f"{game} [{origin}]: {len(kept)} controls the device knows, "
        f"{len(chart['disputed'])} it would not commit to, {len(refused)} dropped")
    for name, action in list(refused.items())[:6]:
        log(f"   dropped '{name}' ({action[:50]}): not a name the device knows")
    log(f"source: {chart['source'].get('url', 'none given')}")
    log(f"written to {out}")
    return {"ok": bool(kept), "chart": chart, "path": out, "origin": origin,
            "kept": len(kept), "disputed": len(chart["disputed"]), "dropped": refused,
            "source": chart["source"].get("url")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("game")
    ap.add_argument("--platform", default="xbox", choices=["xbox", "ps3"])
    ap.add_argument("--out", help="defaults to agent/charts/<slug>.json")
    ap.add_argument("--replay", action="store_true", help="from the recording, no network")
    args = ap.parse_args()
    done = build_chart(args.game, args.platform, args.out,
                       "replay" if args.replay else "auto")
    return 0 if done["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
