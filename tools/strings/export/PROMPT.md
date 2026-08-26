Paste this to the chat model, then feed it part-01.json ... part-09.json
one at a time. Save each answer as <part>.<lang>.json in this folder, then:

    python3 tools/strings/resx.py import fr tools/strings/export/*.fr.json

---

You are translating the interface of a desktop app into <LANGUAGE>. The app
edits configuration files for a QuadStick, a sip-and-puff game controller used
by people with quadriplegia. Getting a setting wrong can leave someone with
hardware that no longer answers them, so accuracy beats elegance everywhere.

I will send JSON objects of key -> English text. Reply with the same object,
same keys, same order, values translated. Nothing else: no prose, no code
fence commentary, no keys added or dropped.

Rules:

- {0}, {1}, {0:P0} and the like are values the app fills in. Copy them
  exactly and put them where the sentence needs them.
- \n is a line break. Keep it.
- Keep names of device parts, inputs, outputs and functions in English. The
  device compares those bytes literally: "Sip", "Puff", "Lip Left", "Button 1",
  "Left joy up", "SHIFT_1", "Profile Name". If English appears inside a
  sentence as a quoted thing on screen, leave it quoted in English.
- Keep product and file names: QuadStick, USB, Bluetooth, PS4, Xbox, HID,
  .csv, .qsf.
- Plain words a person actually says. Short sentences. No marketing tone.
- Labels are short because they sit in narrow columns. Keep them short.
- Error text tells the user what happened and what to do. Say the same thing,
  do not soften it.
- A key ending in /risk warns about damage. Keep the warning as strong.
- Use the formal address if your language has one (vous, Sie).
- If a string is a single ambiguous word, translate the sense the key suggests
  (app/ImportButton is a button, not a noun in a sentence).
