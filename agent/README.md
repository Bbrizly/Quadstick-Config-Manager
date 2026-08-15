# Set up a game

Name any game. This reads how that game is controlled, translates it into the
control scheme one person spent years building, asks about anything their own
history cannot settle, and writes nothing until they say so.

A QuadStick is a mouth-operated controller: sips, puffs, a lip sensor and a
joystick. The people using it play, work and talk through the file this writes.
That is the whole reason for the shape of this thing. A binding it gets wrong is
somebody's hands.

## Run it

    agent/setup.sh "Hollow Knight Silksong"
    agent/setup.sh "Elden Ring" --replay      # from the recording, no network
    agent/setup.sh "Celeste" --live           # ask every time, reuse nothing
    agent/setup.sh --edit mine.csv "make sprint a hard puff"

Or open QuadStick Config Manager and press **Set up a game**, the first card on
the home screen. Both front ends run `agent/run.py`, which emits one JSON event
per line. The window draws those events as cards; the terminal prints them as
lines. One pipeline, two front ends, so nothing can be true in one and not the
other.

## Seeing it on your own device

Before anybody is asked anything, the whole profile is drawn on a picture of
the QuadStick and walked through one part at a time: the joystick, each
mouthpiece hole, the combos, the side tube, the lip switch. The picture is the
device, not a list of it: three holes in a row with the lip switch under them
and the side tube beside them, all inside the frame that is the joystick,
because moving the whole mouthpiece is what the joystick is. Each part carries
how many controls landed on it, and the part being walked through lights up.

What landed there is written above the picture, in the game's own words and
gathered by the thing you do to fire it, so a hole reads **soft puff: Dash**,
not `kb_x, mp_left_puff_soft`. A control the chart had no word for is still
said in English: `kb_escape` is "Escape key". The device is on screen at every
step, whatever is being said above it, and shrinks rather than losing a part
off the edge.

The questions are then asked over the same picture. Reaching an option lights
the part of the mouthpiece it would land on, by keyboard as well as by mouse,
so tabbing the options walks the device. A question that arrives while somebody
is still being shown their device waits for them; the run is blocked on the
answer either way, and an answer given before the tour is an answer given
without it.

This is not decoration. A list of forty rows saying `kb_x, mp_left_puff_soft`
is a correct answer nobody can check, and an approval over something unreadable
is not an approval. The list is still there, one button away, and the approval
at the end is still over the list.

## Watching it work

Every call is on screen before it runs, not after it finished. A step that is
still going counts its own seconds, so a model call that takes three minutes is
visibly working rather than indistinguishable from a run that hung. The web
searches and page reads the research step makes each get their own card as they
happen, including the pages that answered with a 403.

Each finished step says how long it took and where its answer came from, in
words: **asked the model**, **from the recording**, or **on this machine, no
model**. A run that finished in a second because every answer was already
recorded looks exactly like a run that invented them, and that line is the only
thing that tells them apart. The window says which of the two it is before it
starts, too.

In the window it either asks or it replays, and there is no quiet third state
that reuses an old answer without saying so. The terminal keeps `auto` for when
you are iterating and do not want to pay for the same answer twice.

No API key. The model is reached through the Claude Code CLI, which is already
signed in. `ANTHROPIC_API_KEY` switches to the API if you want it.

## What happens

1. **Read how the game is controlled.** If nobody has charted the game,
   `research.py` searches the web and reads the actual control pages. It is
   handed the device's real output names up front, so it returns `kb_space`, not
   `Space`. Anything the device does not know is dropped and reported, never
   written. Where it read each fact goes into the chart.
2. **Answer what their own profiles already answer.** `predict.py` reads 131 of
   this author's published profiles and, for each control the game needs, ranks
   what he actually does. No model is involved. A lopsided history is an answer;
   an even split is not, and becomes a question.
3. **Settle the rest, or ask.** `qsagent.py` gets one bounded turn over the
   leftovers, holding every habit and every control meaning already, so there is
   nothing for it to fetch. It proposes a binding with its evidence, asks, says a
   control should stay unbound and why, or finishes. It cannot write a cell.
   Fetching those facts a control at a time used to cost eight model calls and
   most of the run.
4. **Show the whole thing on the device, then ask.** Everything worked out so
   far is drawn on the QuadStick and walked through part by part before a
   single question. Every option then carries the device tokens it will be
   bound to and lights the part it would land on, so what they pick is written
   exactly as it was shown.
5. **Write, once they say so.** The whole profile is built on a copy first, so
   starting a run writes nothing. Every proposal becomes one `qsf` op carrying
   its own reason. If one op is refused, or the result would not validate,
   nothing is written and the profile is exactly as it was.
6. **Open it, and install it if you want.** The result goes to the editor
   through the same open path as any other file. When a QuadStick is plugged in
   it also offers to install, which starts the app's own install: it rechecks
   the file, asks which drive, and confirms before replacing anything. Nothing
   here writes to a device on its own.

## The rules it is built to keep

**Never rewrite a value the user did not type.** An answer with no function is
refused, not defaulted to `normal`. A history that says `delay_on 500 16000`
carries those timings through; reading it as `delay_on` deleted a setting he
made, and that was a real bug, found in review and fixed.

**Never say nothing.** Controls the agent never reached are named. Names the
research dropped are named. A refusal says which cell and why. A run that stops
says so on screen rather than going quiet. Every phase says why it is happening
at all, and the three numbers the whole run is about (what the game uses, what
his profiles answered, what needs a person) are said once, in one place, as a
bar and in words.

**An approval over something unreadable is not an approval.** This is why the
profile is drawn on the device and walked through before anybody is asked
anything, and why every row says what the game calls it. The rows and the
evidence are still there, one button away, and the approval at the end is still
over that list.

**Nothing unanswered is filled in.** A question nobody answered leaves that
control alone, and the confirm card lists what is staying unbound alongside what
is being written. A control left alone on purpose carries the reason it was
left, and is listed apart from the ones the agent never reached, because those
are not the same thing to the person reading them.

**A row says what it is in the game's words.** The chart already knows the game
calls `kb_space` Jump, so Jump goes in column L beside the binding. The parser,
the device and both official converters ignore that column, so it costs nothing
and survives being shared. It is written in its own pass after the bindings are
accepted: a name is worth nothing to the device and must never cost a binding.

**The device is the oracle.** `qsf` refuses any token the firmware would not
read, and any function parameter it would read as something other than what it
says: `tap banana` becomes `tap 0` on the device, so it is refused rather than
written.

## What it is measured against

Leave-one-family-out over 93 game families, 131 profiles, registered in
`eval/manifest.json` before any held-out result was produced.

| baseline | gameplay exact | with timings | inputs | coverage | rig exact |
|---|---|---|---|---|---|
| his most common answer | 57.9% | 53.2% | 63.9% | 99.7% | 92.8% |
| copy the nearest profile | 49.1% | 42.6% | 55.6% | 97.5% | 91.0% |

`gameplay exact` is the registered measure: same inputs, same function name.
`with timings` scores the same rows with the function's parameters too, so
`tap 200` and `tap 500` count as different. It is the stricter number and the
more honest one.

These are the floor, not the result. An agent that cannot beat copying the
closest profile you already own has added nothing.

The registered number moved from 58.1% to 57.9% when the pipeline stopped
dropping function parameters, because the baseline now picks over whole
functions. That is reported as a drop rather than absorbed.

## What it does not claim

- That the held-out profile is the only correct configuration. It is what one
  expert built at one time.
- That a firmware-valid profile is a usable one. Validity is necessary and
  nowhere near sufficient.
- That any accuracy figure predicts whether this author would accept the result.
  Only he can say that.

## Checks

    agent/pipeline_check.sh     # the whole path, no model, no network
    python3 agent/selfcheck.py  # the agent loop against a scripted model
    python3 agent/eval/evaluate.py

Every fix in here landed with a check that fails without it. `--replay` runs the
whole thing from recorded answers with no network at all, and replays the
searches and page reads too, so the offline run shows the same work the live one
did rather than a silent gap where the web was.
