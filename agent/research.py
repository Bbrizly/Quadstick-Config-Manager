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
import time

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


def brief(content):
    """One line for what a web call came back with, out of a page of text."""
    if isinstance(content, list):
        content = " ".join(b.get("text", "") for b in content if isinstance(b, dict))
    text = " ".join(str(content or "").split())
    links = text.count('"url":') or text.count("http")
    if text.startswith("Web search results"):
        return f"{links} results"
    return f"{len(text):,} characters read" if len(text) > 400 else text[:160]


def stream_cli(command, on_event):
    """Run the CLI in streaming mode and report every step while it happens.

    The web searches and page reads this makes are the most interesting thing
    the whole run does, and the non-streaming form hid all of them behind one
    long silence. A tool nobody can watch working is a tool nobody can check.
    """
    running = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, stdin=subprocess.DEVNULL, bufsize=1)
    answer, failed = None, None
    deadline = time.monotonic() + 900
    # Kept so a replay can show the same searches and page reads a live run did.
    # Without this the offline run, which is the one to fall back on when the
    # room's wifi gives out, showed nothing of the only part that touches the web.
    steps = []

    def report(kind, **fields):
        steps.append({"kind": kind, **fields})
        on_event(kind, **fields)
    for line in running.stdout:
        # Reading a stream has no timeout of its own, so a researcher that stops
        # answering would hang the whole run with nothing on screen to say why.
        if time.monotonic() > deadline:
            running.kill()
            raise SystemExit("the research call ran for 15 minutes without finishing, "
                             "so it was stopped and nothing was written.")
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        kind = event.get("type")
        if kind in ("assistant", "user"):
            for block in (event.get("message") or {}).get("content") or []:
                if not isinstance(block, dict):
                    continue
                if block.get("type") == "tool_use":
                    report("tool", id=block.get("id"), name=block.get("name", ""),
                           input=block.get("input"))
                elif block.get("type") == "tool_result":
                    report("tool_result", id=block.get("tool_use_id"),
                           summary=brief(block.get("content")),
                           failed=bool(block.get("is_error")))
                elif block.get("type") == "text" and (block.get("text") or "").strip():
                    report("said", text=block["text"].strip()[:400])
        elif kind == "result":
            answer = event.get("result")
            if event.get("is_error"):
                failed = str(answer)[:300]
    running.wait()
    if running.returncode != 0 or failed:
        raise SystemExit(f"the research call failed: "
                         f"{failed or (running.stderr.read() or '')[:300]}")
    return answer or "", steps


def research(game, platform, names, mode, on_event=lambda *a, **k: None):
    """One web-reading call, cached, so a rehearsed run needs no network."""
    prompt = asking(game, platform, names)
    key = keyed(prompt)
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
        for step in replay_steps(key):
            on_event(step.pop("kind"), **step)
        return recorded, "cache"
    if mode == "replay":
        raise SystemExit(f"replay mode and no research recorded for {game} ({key}).")

    text, steps = stream_cli([
        CLAUDE_BIN, "-p", prompt, "--model", MODEL,
        "--output-format", "stream-json", "--verbose",
        "--system-prompt", SYSTEM,
        "--tools", TOOLS,
        "--disable-slash-commands", "--strict-mcp-config",
        "--mcp-config", '{"mcpServers":{}}', "--safe-mode", "--no-session-persistence",
    ], on_event)
    text = text.strip()
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
    # Beside the answer, not inside it: an older recording has no steps file and
    # still replays, it just replays quietly.
    with open(steps_path(key), "w") as f:
        json.dump(steps, f, indent=2)
    return found, "live"


def steps_path(key):
    return os.path.join(CACHE, "research-" + key + "-steps.json")


def replay_steps(key):
    """What the researcher did last time, if it was recorded."""
    path = steps_path(key)
    if not os.path.exists(path):
        return []
    try:
        steps = json.load(open(path))
    except json.JSONDecodeError:
        return []
    return [s for s in steps if isinstance(s, dict) and "kind" in s]


def asking(game, platform, names):
    """The exact question the researcher is given."""
    # Buttons and keys only. The rig rows (mouse speed, deflection, mode
    # switching) are the player's own setup and are not a fact about the game.
    offer = sorted(n for n in names if not n.startswith(
        ("mouse_", "deflection_", "joystick_", "bluetooth_", "digital_out",
         "acceleration_", "increment_", "decrement_")))
    words = {"xbox": "PC, keyboard and mouse, or Xbox controller",
             "ps3": "PC or PlayStation controller"}[platform]
    return ASK.format(game=game, platform=platform, platform_words=words,
                      tokens=", ".join(offer))


def keyed(prompt):
    """Everything that could change the answer is in the key, not just the
    question: the instructions it was given and the tools it was allowed to use
    decide what an answer is worth, and replaying one recorded under different
    instructions would be a stale hit wearing a fresh answer's face."""
    return hashlib.sha256(
        json.dumps([MODEL, SYSTEM, TOOLS, prompt]).encode()).hexdigest()[:32]


def recorded_for(game, platform="xbox"):
    """Whether the searches and page reads for this game were recorded."""
    return bool(replay_steps(keyed(asking(game, platform, vocab(platform)))))


def slugify(game):
    """One filename per game, so "Elden Ring" and "elden-ring" find one chart."""
    return re.sub(r"[^a-z0-9]+", "-", game.lower()).strip("-")


def build_chart(game, platform="xbox", out=None, mode="auto", log=print,
                on_event=lambda *a, **k: None):
    """Research one game and write its chart. Returns the chart and what it cost.

    Every count the caller needs is in the return value, because the app shows
    them and nothing here should have to be read back out of printed prose.
    """
    slug = slugify(game)
    out = out or os.path.join(HERE, "charts", slug + ".json")
    names = vocab(platform)

    found, origin = research(game, platform, names, mode, on_event)

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
